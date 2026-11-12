using CookieSheet.Refunds;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class RefundDeletionTests
{
    [Fact]
    public async Task DeletesOnlySelectedRefundAndPersistsWithoutChangingOrdersOrUsers()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var db = new AppDbContext(options))
        {
            db.user.Add(new user { userId = 1, tel = "", qq = "", cn = "测试", receiver = "", account = "", address = "" });
            db.order.Add(new order { orderId = "o1", userId = 1, tel = "", tag = "测试", productName = ["原商品"],
                productModel = ["型号"], productCount = [5], price = 35 });
            db.refunds.AddRange(new RefundRecord { refundId = "delete-me", orderId = "o1", productName = "未匹配商品", status = "退款成功" },
                new RefundRecord { refundId = "keep-me", orderId = "o1", productName = "原商品", status = "退款成功" });
            await db.SaveChangesAsync();
            await RefundImportService.DeleteAsync(db, "delete-me");
        }
        using (var db = new AppDbContext(options))
        {
            Assert.Equal("keep-me", (await db.refunds.SingleAsync()).refundId);
            Assert.True((await db.refunds.IgnoreQueryFilters().SingleAsync(r => r.refundId == "delete-me")).isDeleted);
            Assert.Equal(5, (await db.order.SingleAsync()).productCount[0]);
            Assert.Equal(35, (await db.order.SingleAsync()).price);
            Assert.Single(await db.user.ToListAsync());
            await RefundImportService.DeleteAsync(db, "delete-me");
            await RefundImportService.DeleteAsync(db, "missing");
            Assert.Single(await db.refunds.ToListAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => RefundImportService.DeleteAsync(db, " "));
            Assert.Single(await db.refunds.ToListAsync());
        }
    }

    private static RefundRecord Record(string id, decimal amount = 7) => new()
    {
        refundId = id, orderId = "o1", productName = "企划", productModel = "明信片",
        productPaidAmount = 21, refundAmount = amount, status = "退款成功"
    };

    [Fact]
    public void OldBackupWithoutDeletionFieldRemainsActive()
    {
        var record = JsonSerializer.Deserialize<RefundRecord>("""{"refundId":"old","status":"退款成功"}""")!;
        Assert.False(record.isDeleted);
        Assert.True(RefundRules.IsSuccessful(record));
    }

    [Fact]
    public async Task BatchDeletionIsIdempotentAndReimportSkipsDeletedIdsRegardlessOfIdentity()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var db = new AppDbContext(options))
        {
            db.refunds.AddRange(Record("r1"), Record("r2"), Record("keep"));
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => RefundImportService.DeleteBatchAsync(db, ["r1", " "]));
            Assert.Equal(3, await db.refunds.CountAsync());
            Assert.Equal(2, await RefundImportService.DeleteBatchAsync(db, ["r1", "r2", "r1", "missing"]));
            Assert.Equal(0, await RefundImportService.DeleteBatchAsync(db, ["r1", "r2"]));
        }
        using (var db = new AppDbContext(options))
        {
            var altered = Record("r1", 14);
            altered.orderId = "changed";
            altered.productName = "不同商品";
            var result = await RefundImportService.SaveAsync(db, [altered, Record("r2"), Record("keep", 14), Record("new")]);
            Assert.Equal(new RefundSaveResult(1, 1, 0, 0, 2), result);
            Assert.Equal(2, await db.refunds.CountAsync());
            Assert.Equal(4, await db.refunds.IgnoreQueryFilters().CountAsync());
            var deleted = await db.refunds.IgnoreQueryFilters().SingleAsync(r => r.refundId == "r1");
            Assert.True(deleted.isDeleted);
            Assert.Equal("o1", deleted.orderId);
            Assert.Equal(7, deleted.refundAmount);
            Assert.Equal(14, (await db.refunds.SingleAsync(r => r.refundId == "keep")).refundAmount);
        }
    }

    [Fact]
    public void DeletedRecordsDoNotAffectMatchingOrQuantity()
    {
        var deleted = Record("deleted");
        deleted.isDeleted = true;
        Assert.False(RefundRules.IsSuccessful(deleted));
        Assert.Empty(RefundMatching.FindUnmatched([deleted], new Dictionary<string, IReadOnlyList<OrderedProduct>>()));
        var product = Assert.Single(RefundCalculator.Calculate("o1", [new("企划", "明信片", 3)], [deleted]));
        Assert.Equal(0, product.RefundedQuantity);
        Assert.Equal(ProductRefundStatus.None, product.Status);
    }

    [BackupPostgresFact]
    public async Task ConcurrentImportCannotResurrectDeletionAndItsWholeBatchRollsBack()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN"));
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(55439, builder.Port);
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var database = "cookiesheet_softdelete_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        builder.Pooling = false;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options;
        try
        {
            await using var importing = new AppDbContext(options);
            await importing.Database.MigrateAsync();
            importing.refunds.AddRange(Record("r1"), Record("r2"));
            await importing.SaveChangesAsync();
            // The importing context still holds the rows read before the deletion.
            await using (var deleting = new AppDbContext(options))
            {
                Assert.Equal(1, await RefundImportService.DeleteBatchAsync(deleting, ["r1"]));
                Assert.Equal(0, await RefundImportService.DeleteBatchAsync(deleting, ["r1"]));
            }
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => RefundImportService.SaveAsync(importing,
                [Record("r1", 14), Record("r2", 14), Record("new")]));
            await using var checking = new AppDbContext(options);
            Assert.Equal(7, (await checking.refunds.SingleAsync()).refundAmount);
            Assert.True((await checking.refunds.IgnoreQueryFilters().SingleAsync(r => r.refundId == "r1")).isDeleted);
            var retry = await RefundImportService.SaveAsync(checking, [Record("r1", 14), Record("r2", 14)]);
            Assert.Equal(1, retry.DeletedSkipped);
            Assert.Equal(1, retry.Updated);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
