using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using CookieSheet.Refunds;
using CookieSheetServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

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
