using System.Security.Cryptography;
using System.Text.Json;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public sealed class BackupPostgresFactAttribute : FactAttribute
{
    public BackupPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN")))
            Skip = "Requires local PostgreSQL; creates and drops its own isolated database.";
    }
}

public class DataBackupTests
{
    [Fact]
    public async Task MissingOrCorruptBackupDoesNotTouchDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cookiesheet-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new DataBackupService(directory);
            using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Assert.Null(await service.GetLatestAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(db, Guid.NewGuid()));
            await File.WriteAllTextAsync(Path.Combine(directory, "latest.json"), "{broken");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(db, Guid.NewGuid()));
            await File.WriteAllTextAsync(Path.Combine(directory, "latest.json"),
                JsonSerializer.Serialize(new { Payload = new byte[] { 1, 2, 3 }, Sha256 = "wrong" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetLatestAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [BackupPostgresFact]
    public async Task SnapshotReplacementRestoreRollbackAndIdentityWorkInPostgres()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN"));
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(55439, builder.Port);
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var database = "cookiesheet_backup_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        builder.Pooling = false;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options;
        var directory = Path.Combine(Path.GetTempPath(), database);
        var service = new DataBackupService(directory);
        var path = Path.Combine(directory, "latest.json");
        try
        {
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync();
            var now = DateTimeOffset.Parse("2026-09-22T10:20:30Z");
            db.user.Add(new user { userId = 900, tel = "123", qq = "456", cn = "中文", receiver = "收件人",
                account = "账户", address = "地址", price = 12.34m });
            db.order.Add(new order { orderId = "订单一", userId = 900, tel = "789", orderedAt = now,
                productName = ["商品", "99满赠"], productModel = ["型号", ""], productCount = [3, 1],
                productUnitPrice = [4.12m, 0], productId = ["p1", "gift"], price = 12.36m, tag = "批次一", sended = true });
            db.refunds.AddRange(new RefundRecord { refundId = "r1", orderId = "订单一", productName = "商品", productModel = "型号",
                productId = "p1", productCode = "code", modelCode = "model", productPaidAmount = 12.36m,
                shippingPaidAmount = 2, refundAmount = 4.12m, shippingRefundAmount = 1, status = "退款成功",
                refundType = "退货退款", saleStage = "售后", initiator = "买家", reason = "原因", shippingStatus = "已寄回",
                orderPaidAt = now, requestedAt = now, completedAt = now, deadlineAt = now,
                sourceFile = "原表.xlsx", sourceRow = 20, importedAt = now },
                new RefundRecord { refundId = "unmatched", orderId = "尚无订单", status = "退款成功", importedAt = now });
            await db.SaveChangesAsync();
            var expected = await ReadData(db);
            var first = await service.CreateAsync(db);
            Assert.Equal((1, 1, 2), (first.Users, first.Orders, first.Refunds));
            Assert.Equal(first, await new DataBackupService(directory).GetLatestAsync());
            var firstBytes = await File.ReadAllBytesAsync(path);

            // A failed new backup cannot destroy the previous backup.
            Directory.CreateDirectory(path + ".tmp");
            await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(db));
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(path));
            Directory.Delete(path + ".tmp");

            await db.refunds.ExecuteDeleteAsync();
            await db.order.ExecuteDeleteAsync();
            await db.user.ExecuteDeleteAsync();
            db.ChangeTracker.Clear();
            db.user.Add(new user { userId = 2, tel = "", qq = "", cn = "新增", receiver = "", account = "", address = "" });
            await db.SaveChangesAsync();
            var changed = await ReadData(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(db, Guid.NewGuid()));
            Assert.Equal(changed, await ReadData(db));

            // A syntactically valid but incompatible row fails during insertion, rolling back all deletions.
            using (var envelope = JsonDocument.Parse(firstBytes))
            {
                var snapshot = JsonSerializer.Deserialize<DataBackupService.Snapshot>(envelope.RootElement.GetProperty("Payload").GetBytesFromBase64())!;
                snapshot.Orders[0].tel = null!;
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Payload = payload, Sha256 = Convert.ToHexString(SHA256.HashData(payload)) }));
            }
            await Assert.ThrowsAsync<DbUpdateException>(() => service.RestoreAsync(db, first.Id));
            Assert.Equal(changed, await ReadData(db));
            await File.WriteAllBytesAsync(path, firstBytes);
            await service.RestoreAsync(db, first.Id);
            Assert.Equal(expected, await ReadData(db));
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(path));
            await service.RestoreAsync(db, first.Id);
            Assert.Equal(expected, await ReadData(db));

            // Restored explicit user IDs must not collide with the next automatically assigned ID.
            db.ChangeTracker.Clear();
            var added = new user { cn = "之后新增", tel = "", qq = "", receiver = "", account = "", address = "" };
            db.user.Add(added);
            await db.SaveChangesAsync();
            Assert.True(added.userId > 900);
            var second = await service.CreateAsync(db);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, second.Users);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(db, first.Id));
            Assert.Equal(second, await service.GetLatestAsync());

            // Empty data is a valid explicitly requested snapshot and restores to empty.
            await db.refunds.ExecuteDeleteAsync();
            await db.order.ExecuteDeleteAsync();
            await db.user.ExecuteDeleteAsync();
            var empty = await service.CreateAsync(db);
            Assert.Equal((0, 0, 0), (empty.Users, empty.Orders, empty.Refunds));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(db, second.Id));
            await service.RestoreAsync(db, empty.Id);
            Assert.Empty(await db.user.AsNoTracking().ToListAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> ReadData(AppDbContext db) => JsonSerializer.Serialize(new
    {
        Users = await db.user.AsNoTracking().OrderBy(u => u.userId).ToListAsync(),
        Orders = await db.order.AsNoTracking().OrderBy(o => o.orderId).ToListAsync(),
        Refunds = await db.refunds.AsNoTracking().OrderBy(r => r.refundId).ToListAsync()
    });
}
