using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using CookieSheet.Refunds;
using CookieSheetServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddSingleton(new DataBackupService(
    Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data-backups")));

// Register AppDbContext with PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(Environment.GetEnvironmentVariable("DATABASE")));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Backup operations never run on a schedule or as a side effect of restore.
app.MapGet("/api/backups/latest", async (DataBackupService backups) =>
    await BackupResult(async () => (await backups.GetLatestAsync()) is { } info ? Results.Ok(info) : Results.NoContent()));
app.MapPost("/api/backups", async (AppDbContext db, DataBackupService backups) =>
    await BackupResult(async () => Results.Ok(await backups.CreateAsync(db))));
app.MapPost("/api/backups/restore", async (RestoreBackupRequest request, AppDbContext db, DataBackupService backups) =>
    await BackupResult(async () => Results.Ok(await backups.RestoreAsync(db, request.BackupId))));

async Task<IResult> BackupResult(Func<Task<IResult>> operation)
{
    try { return await operation(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Manual backup/restore failed");
        return Results.Json(new { message = "操作未成功完成，请检查服务器日志和备份目录权限后重试。" }, statusCode: 500);
    }
}

// API Endpoints for Users
app.MapGet("/api/users", async (AppDbContext db) =>
    await db.user.ToListAsync());

app.MapPost("/api/users", async (user user, AppDbContext db) =>
{
    var existingUser = await db.user.FindAsync(user.userId);
    if (existingUser == null)
    {
        db.user.Add(user);
    }
    else
    {
        db.Entry(existingUser).CurrentValues.SetValues(user);
    }

    await db.SaveChangesAsync();
    return Results.Ok(user);
});

// API Endpoints for Orders
app.MapGet("/api/orders", async (AppDbContext db) => await db.order.ToListAsync());

app.MapPost("/api/orders", async (order order, AppDbContext db) =>
{
    order.orderedAt = order.orderedAt.ToUniversalTime();

    var existingOrder = await db.order.FindAsync(order.orderId);
    if (existingOrder == null)
    {
        db.order.Add(order);
    }
    else
    {
        db.Entry(existingOrder).CurrentValues.SetValues(order);
    }


    await db.SaveChangesAsync();
    return Results.Ok(order);
});

app.MapPost("/api/users/batch", async (List<user> users, AppDbContext db) =>
{
    var newUsers = new List<user>();

    foreach (var user in users)
    {
        var existingUser = await db.user.FindAsync(user.userId);
        if (existingUser == null)
        {
            db.user.Add(user);
            newUsers.Add(user);
        }
        else
        {
            db.Entry(existingUser).CurrentValues.SetValues(user);
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(newUsers);
});

// Older clients do not ask for gift choices; do not silently enable gifts on their behalf.
app.MapPost("/api/orders/import", () => Results.BadRequest(new { message = "请更新客户端，每次导入需手动确认各批次是否计算赠品。" }));
app.MapPost("/api/refunds/batch", () => Results.BadRequest(new { message = "请更新客户端，每次导入需手动确认各批次是否计算赠品。" }));

app.MapPost("/api/orders/import-with-gifts", () => Results.BadRequest(new { message = "请更新客户端，导入时需要识别并排除已关闭订单。" }));

app.MapPost("/api/orders/import-excluding-closed", async (OrderImportRequest request, AppDbContext db) =>
{
    try
    {
        if (request.GiftChoices == null) throw new ArgumentException("请先逐批确认是否计算满赠和 Set赠。");
        return Results.Ok(await OrderImportService.SaveAsync(db, request));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "本次导入未保存，可能有其他导入同时修改了数据，请刷新后重试。" });
    }
});

app.MapPost("/api/orders/delete", async (OrderDeleteRequest request, AppDbContext db) =>
{
    try { return Results.Ok(await OrderDeletionService.DeleteAsync(db, request)); }
    catch (GiftChoiceRequiredException ex) { return Results.Conflict(new { code = "gift_choice_required", message = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
    catch (DbUpdateException) { return Results.Conflict(new { message = "删除未完成，请刷新数据后重试。" }); }
});

app.MapPost("/api/orders/batch", async (List<order> orders, AppDbContext db) =>
{
    foreach (var order in orders)
    {
        order.orderedAt = order.orderedAt.ToUniversalTime();

        var existingOrder = await db.order.FindAsync(order.orderId);
        if (existingOrder == null)
        {
            db.order.Add(order);
        }
        else
        {
            db.Entry(existingOrder).CurrentValues.SetValues(order);
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/refunds", async (AppDbContext db) => await db.refunds.AsNoTracking().ToListAsync());

app.MapPost("/api/refunds/delete-batch", async (RefundDeleteRequest request, AppDbContext db) =>
{
    try { return Results.Ok(new { Deleted = await RefundImportService.DeleteBatchAsync(db, request.RefundIds) }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapDelete("/api/refunds/{refundId}", async (string refundId, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(refundId)) return Results.BadRequest(new { message = "退款编号不能为空。" });
    await RefundImportService.DeleteAsync(db, refundId);
    return Results.NoContent();
});

app.MapPost("/api/refunds/import-with-gifts", async (RefundImportRequest request, AppDbContext db) =>
{
    try
    {
        if (request.GiftChoices == null) throw new ArgumentException("请先逐批确认是否计算满赠和 Set赠。");
        return Results.Ok(await RefundImportService.SaveAsync(db, request.Records, request.GiftChoices));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
    {
        return Results.Conflict(new { message = "另一次导入同时更新了退款记录，请重新导入。" });
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { message = "退款记录已被其他操作删除，本次导入已撤回，请重新导入以跳过已删除记录。" });
    }
});

app.MapPost("/api/clear", async (AppDbContext db) =>
{
    db.giftChoices.RemoveRange(db.giftChoices);
    db.refunds.RemoveRange(db.refunds.IgnoreQueryFilters());
    db.order.RemoveRange(db.order);
    db.user.RemoveRange(db.user);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// Seed database or ensure created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate(); // Use this if you have migrations
    db.Database.EnsureCreated(); // Simple approach for now
}

app.Run();
