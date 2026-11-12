using System.Text.Json;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class OrderDeletionTests
{
    private static order Order(string id, int owner = 1) => new()
    {
        orderId = id, userId = owner, tel = "原电话", tag = "批次", price = 99,
        orderedAt = DateTimeOffset.Parse("2026-09-20T00:00:00Z"), sended = true,
        productName = ["商品"], productModel = ["型号"], productCount = [3]
    };
    private static DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task DeleteRecalculatesGiftsRetainsRefundsAndAllowsCorrectedReimport(bool full, bool set)
    {
        var options = Options();
        string refundBefore;
        using (var db = new AppDbContext(options))
        {
            db.order.AddRange(Order("wrong"), Order("stay"));
            db.refunds.Add(new() { refundId = "r", orderId = "wrong", status = "退款成功",
                productName = "商品", productModel = "型号", productPaidAmount = 21, refundAmount = 7 });
            db.giftChoices.Add(new() { Batch = "批次", FullGift = full, SetGift = set });
            await db.SaveChangesAsync();
            await GiftRecalculationService.RecalculateAsync(db, ["批次"]);
            refundBefore = JsonSerializer.Serialize(await db.refunds.SingleAsync());
        }
        using (var db = new AppDbContext(options))
        {
            var result = await OrderDeletionService.DeleteAsync(db, new("wrong", 1, "批次"));
            Assert.True(result.Deleted);
            var remaining = Assert.Single(await db.order.ToListAsync());
            Assert.Equal("stay", remaining.orderId);
            Assert.Equal(99, remaining.price);
            Assert.True(remaining.sended);
            Assert.Equal(3, remaining.productCount[0]);
            int Gift(string name) => remaining.productName.Select((n, i) => n == name ? remaining.productCount[i] : 0).Sum();
            Assert.Equal(full ? 1 : 0, Gift("99满赠"));
            Assert.Equal(set ? 3 : 0, Gift("Set赠"));
            Assert.Equal(refundBefore, JsonSerializer.Serialize(await db.refunds.SingleAsync()));
            Assert.False((await OrderDeletionService.DeleteAsync(db, new("wrong", 1, "批次"))).Deleted);
        }
        using (var db = new AppDbContext(options))
        {
            var corrected = Order("wrong", 2);
            corrected.sended = false;
            await OrderImportService.SaveAsync(db, new() { Batch = "批次", Orders = [corrected] });
            var saved = await db.order.SingleAsync(o => o.orderId == "wrong");
            Assert.Equal(2, saved.userId);
            Assert.False(saved.sended);
            var product = Assert.Single(RefundCalculator.Calculate("wrong", [new("商品", "型号", 3)], await db.refunds.ToListAsync()));
            Assert.Equal(1, product.RefundedQuantity);
            Assert.Equal(refundBefore, JsonSerializer.Serialize(await db.refunds.SingleAsync()));
        }
    }

    [Theory]
    [InlineData(2, "批次")]
    [InlineData(1, "旧批次")]
    public async Task StaleConfirmationCannotDeleteMovedOrder(int owner, string batch)
    {
        using var db = new AppDbContext(Options());
        db.order.Add(Order("o1"));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => OrderDeletionService.DeleteAsync(db, new("o1", owner, batch)));
        Assert.Single(await db.order.ToListAsync());
    }

    [Fact]
    public async Task MissingGiftChoiceDoesNotDeleteOrderOrInventConsent()
    {
        using var db = new AppDbContext(Options());
        db.order.Add(Order("o1"));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<GiftChoiceRequiredException>(() => OrderDeletionService.DeleteAsync(db, new("o1", 1, "批次")));
        Assert.Single(await db.order.ToListAsync());
        Assert.Empty(await db.giftChoices.ToListAsync());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MissingChoicesCanBeSelectedAndSavedWithDeletion(bool full, bool set)
    {
        using var db = new AppDbContext(Options());
        db.order.AddRange(Order("remove"), Order("stay"));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<GiftChoiceRequiredException>(() =>
            OrderDeletionService.DeleteAsync(db, new("remove", 1, "批次", full, null)));
        Assert.Equal(2, await db.order.CountAsync());
        Assert.Empty(await db.giftChoices.ToListAsync());
        Assert.True((await OrderDeletionService.DeleteAsync(db, new("remove", 1, "批次", full, set))).Deleted);
        var choice = await db.giftChoices.SingleAsync();
        Assert.Equal(full, choice.FullGift);
        Assert.Equal(set, choice.SetGift);
        var stay = await db.order.SingleAsync();
        Assert.Equal(full, stay.productName.Contains("99满赠"));
        Assert.Equal(set, stay.productName.Contains("Set赠"));
    }

    [Fact]
    public async Task ConcurrentSavedChoiceIsNotOverwrittenByDeletionPrompt()
    {
        using var db = new AppDbContext(Options());
        db.order.Add(Order("o1"));
        db.giftChoices.Add(new() { Batch = "批次", FullGift = false, SetGift = false });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrderDeletionService.DeleteAsync(db, new("o1", 1, "批次", true, true)));
        Assert.Single(await db.order.ToListAsync());
        Assert.False((await db.giftChoices.SingleAsync()).FullGift);
    }

    [BackupPostgresFact]
    public async Task PostgresRollsBackDeletionIfGiftRecalculationFails()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN"));
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(55439, builder.Port);
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var name = "cookiesheet_delete_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = name;
        builder.Pooling = false;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options;
        try
        {
            using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.user.Add(new() { userId = 1, tel = "", qq = "123", cn = "测试", receiver = "", account = "", address = "" });
                var overflow = Order("stay");
                overflow.price = ((decimal)int.MaxValue + 1) * 99;
                db.order.AddRange(Order("delete"), overflow);
                await db.SaveChangesAsync();
                await Assert.ThrowsAsync<OverflowException>(() => OrderDeletionService.DeleteAsync(db, new("delete", 1, "批次", true, false)));
            }
            using (var db = new AppDbContext(options))
            {
                Assert.Equal(2, await db.order.CountAsync());
                Assert.Empty(await db.giftChoices.ToListAsync());
                var stay = await db.order.SingleAsync(o => o.orderId == "stay");
                stay.price = 99;
                await db.SaveChangesAsync();
                Assert.True((await OrderDeletionService.DeleteAsync(db, new("delete", 1, "批次", true, false))).Deleted);
            }
            using (var db = new AppDbContext(options))
            {
                var stay = Assert.Single(await db.order.ToListAsync());
                Assert.Equal("stay", stay.orderId);
                Assert.Contains("99满赠", stay.productName);
                Assert.True(stay.sended);
                Assert.Single(await db.user.ToListAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
