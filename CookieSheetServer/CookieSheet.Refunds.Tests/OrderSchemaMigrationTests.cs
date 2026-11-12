using System.Text.Json;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class OrderSchemaMigrationTests
{
    [Fact]
    public void OldBackupOrderCanBeReadWithoutRestoringRetiredFields()
    {
        var restored = JsonSerializer.Deserialize<order>("""
            {"orderId":"o1","productName":["企划"],"productModel":["明信片"],"productCount":[3],
             "productId":["old-id"],"productUnitPrice":[2.33],"price":7,"tag":"原批次","sended":true}
            """)!;
        Assert.Equal(7, restored.price);
        Assert.True(restored.sended);
        Assert.Equal([3], restored.productCount);
        var output = JsonSerializer.Serialize(restored);
        Assert.DoesNotContain("productId", output);
        Assert.DoesNotContain("productUnitPrice", output);
    }

    [BackupPostgresFact]
    public async Task MigrationDropsOnlyRetiredOrderColumnsAndPreservesOtherValues()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN"));
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(55439, builder.Port);
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var name = "cookiesheet_schema_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = name;
        builder.Pooling = false;
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260923061332_AddOrderAmountRefundStatus");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "user" ("userId",tel,qq,cn,receiver,account,price,address)
                VALUES (1,'','','测试','','',0,'');
                INSERT INTO "order" ("orderId","userId",tel,"orderedAt","productName","productModel","productCount",
                  "productUnitPrice","productId",price,sended,tag,"receivableAmount","orderRefundStatus")
                VALUES ('o1',1,'','2026-09-23T00:00:00Z',ARRAY['企划','99满赠'],ARRAY['明信片','99满赠'],ARRAY[3,1],
                  ARRAY[2.33,0],ARRAY['old-id',''],7,true,'原批次',21,'退款完成(部分退款)');
                """);
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            const string kept = "SELECT (to_jsonb(o) - 'productId' - 'productUnitPrice' - 'productRefundAmount')::text FROM \"order\" o";
            async Task<string> Read() { await using var cmd = new NpgsqlCommand(kept, connection); return (string)(await cmd.ExecuteScalarAsync())!; }
            var before = await Read();
            await migrator.MigrateAsync();
            Assert.Equal(before, await Read());
            await using var columns = new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='order' AND column_name IN ('productId','productUnitPrice')", connection);
            Assert.Equal(0L, await columns.ExecuteScalarAsync());
            var saved = await db.order.SingleAsync();
            Assert.Null(saved.productRefundAmount);
            Assert.True(saved.sended);
            Assert.Equal([3, 1], saved.productCount);
            Assert.Single(await db.user.ToListAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
