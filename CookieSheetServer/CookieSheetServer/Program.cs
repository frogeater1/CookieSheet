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

app.MapDelete("/api/refunds/{refundId}", async (string refundId, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(refundId)) return Results.BadRequest(new { message = "退款编号不能为空。" });
    await RefundImportService.DeleteAsync(db, refundId);
    return Results.NoContent();
});

app.MapPost("/api/refunds/batch", async (List<RefundRecord> records, AppDbContext db) =>
{
    try
    {
        return Results.Ok(await RefundImportService.SaveAsync(db, records));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
    {
        return Results.Conflict(new { message = "另一次导入同时更新了退款记录，请重新导入。" });
    }
});

app.MapPost("/api/clear", async (AppDbContext db) =>
{
    db.refunds.RemoveRange(db.refunds);
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
