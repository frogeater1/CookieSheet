using System.Text.Json;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class OrderImportTests
{
    private static DbContextOptions<AppDbContext> Options()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        db.giftChoices.AddRange(new[] { "原批次", "新批次", "同批次", "9批", "10批", "1批" }
            .Select(batch => new BatchGiftChoice { Batch = batch, FullGift = true, SetGift = true }));
        db.SaveChanges();
        return options;
    }

    private static order Order(string id, bool postage = false) => new()
    {
        orderId = id, userId = 1, tel = "原电话", orderedAt = DateTimeOffset.Parse("2026-09-20T10:00:00Z"),
        productName = ["原商品", "99满赠", "Set赠"], productModel = ["原型号", "99满赠", "Set赠"],
        productCount = [5, 1, 1],
        price = 35, sended = postage, tag = "原批次"
    };

    [Theory]
    [InlineData(true, "原批次")]
    [InlineData(false, "原批次")]
    [InlineData(true, "新批次")]
    [InlineData(false, "新批次")]
    public async Task ReimportUpdatesBatchAndAmountsAndPreservesEveryOtherStoredField(bool postage, string batch)
    {
        var options = Options();
        var original = Order("o1", postage);
        using (var db = new AppDbContext(options))
        {
            db.order.Add(original);
            db.order.Add(Order("omitted"));
            db.refunds.Add(new RefundRecord { refundId = "r1", orderId = "o1", productName = "原商品",
                productModel = "原型号", status = "退款成功", refundAmount = 7 });
            await db.SaveChangesAsync();
        }
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = batch, Orders = [new order
            {
                orderId = "o1", userId = 999, tel = "新电话", orderedAt = DateTimeOffset.UtcNow,
                productName = ["新商品"], productModel = ["新型号"], productCount = [99],
                price = 28, receivableAmount = 35, productRefundAmount = 7, orderRefundStatus = "退款完成(部分退款)",
                sended = !postage, tag = "新文件名"
            }] });
        }
        using (var db = new AppDbContext(options))
        {
            original.price = 28;
            original.tag = batch;
            original.receivableAmount = 35;
            original.productRefundAmount = 7;
            original.orderRefundStatus = "退款完成(部分退款)";
            original.productName = ["原商品"];
            original.productModel = ["原型号"];
            original.productCount = [5]; // Missing/invalid refund details suppress old Set gifts.
            var saved = await db.order.SingleAsync(o => o.orderId == "o1");
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(saved));
            Assert.Equal(35, (await db.order.SingleAsync(o => o.orderId == "omitted")).price);
            Assert.Equal(7, (await db.refunds.SingleAsync()).refundAmount);
            Assert.Empty(await db.user.ToListAsync());
        }
    }

    [Fact]
    public async Task AmountStatusPersistsWithoutRefundTableAndReimportCanClearOldColor()
    {
        var options = Options();
        var incoming = Order("o1");
        incoming.price = 0;
        incoming.receivableAmount = 35;
        incoming.orderRefundStatus = "退款完成(全额退款)";
        using (var db = new AppDbContext(options))
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "原批次", Orders = [incoming] });
        using (var db = new AppDbContext(options))
        {
            var saved = await db.order.SingleAsync();
            Assert.Empty(await db.refunds.ToListAsync());
            Assert.Equal(0, saved.price);
            Assert.Equal(35, saved.receivableAmount);
            Assert.Equal(OrderAmountRefundStatus.Full, OrderAmountRules.Classify(saved.orderRefundStatus));
            var corrected = Order("o1");
            corrected.receivableAmount = 35;
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "原批次", Orders = [corrected] });
        }
        using (var db = new AppDbContext(options))
        {
            var saved = await db.order.SingleAsync();
            Assert.Equal(35, saved.price);
            Assert.Equal(OrderAmountRefundStatus.None, OrderAmountRules.Classify(saved.orderRefundStatus));
        }
    }

    [Fact]
    public async Task NewOrdersAreAddedAndRepeatedIdsUpdateAmountWithoutDuplicatingOrReplacingProducts()
    {
        var options = Options();
        var first = Order("new");
        var later = Order("new", true);
        later.price = 21;
        later.productCount = [1];
        using (var db = new AppDbContext(options))
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "原批次", Orders = [first, later] });
        using (var db = new AppDbContext(options))
        {
            var saved = Assert.Single(await db.order.ToListAsync());
            Assert.Equal(21, saved.price);
            Assert.Equal([5, 5], saved.productCount);
            Assert.Equal(["原商品", "Set赠"], saved.productName);
            Assert.False(saved.sended);
            Assert.Equal("原批次", saved.tag);
        }
    }

    private static user NewUser() => new()
    {
        tel = "", qq = "", cn = "导入测试", receiver = "", account = "", address = ""
    };

    [Fact]
    public async Task RefundImportEnrichesEachModelAndSoldTotalsWithoutChangingOrderAmountsOrColor()
    {
        var options = Options();
        var original = Order("o1", true);
        original.productName = ["企划", "企划"];
        original.productModel = ["明信片", "色纸"];
        original.productCount = [3, 2];
        original.price = 45;
        original.receivableAmount = 97;
        original.orderRefundStatus = "退款完成(部分退款)";
        var before = JsonSerializer.Serialize(original);
        var products = new[] { new OrderedProduct("企划", "明信片", 3),
            new OrderedProduct("企划", "色纸", 2) };
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "原批次", Orders = [original] });
            Assert.All(RefundCalculator.Calculate("o1", products, await db.refunds.ToListAsync()),
                p => Assert.Equal(ProductRefundStatus.None, p.Status));
        }
        List<RefundRecord> Refunds() => [
            new() { orderId = "o1", refundId = "r1", productName = "企划", productModel = "明信片",
                productId = "p1", productPaidAmount = 21, refundAmount = 7, status = "退款成功" },
            new() { orderId = "o1", refundId = "r2", productName = "企划", productModel = "明信片",
                productId = "p1", productPaidAmount = 21, refundAmount = 7, status = "退款成功" },
            new() { orderId = "o1", refundId = "r3", productName = "企划", productModel = "色纸",
                productId = "p2", productPaidAmount = 76, refundAmount = 38, status = "退款成功" }];
        using (var db = new AppDbContext(options))
        {
            await RefundImportService.SaveAsync(db, Refunds());
            await RefundImportService.SaveAsync(db, Refunds());
        }
        using (var db = new AppDbContext(options))
        {
            var saved = await db.order.SingleAsync();
            var expected = JsonSerializer.Deserialize<order>(before)!;
            expected.productName = [..expected.productName, "Set赠"];
            expected.productModel = [..expected.productModel, "Set赠"];
            expected.productCount = [..expected.productCount, 1];
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(saved));
            Assert.Equal(OrderAmountRefundStatus.Partial, OrderAmountRules.Classify(saved.orderRefundStatus));
            var refunds = await db.refunds.ToListAsync();
            Assert.Equal(3, refunds.Count);
            var results = RefundCalculator.Calculate("o1", products, refunds);
            Assert.Equal(2, results[0].RefundedQuantity);
            Assert.Equal(1, results[1].RefundedQuantity);
            Assert.Contains("已退 2/3，剩 1", results[0].Annotation);
            Assert.Contains("已退 1/2，剩 1", results[1].Annotation);
            var summary = SoldProductsQuery.Apply([new("o1", saved.tag, results)]);
            Assert.Equal(3, summary.ConfirmedRefundedQuantity);
            Assert.Equal(2, summary.NetQuantity);
        }
    }

    [Fact]
    public async Task CrossBatchImportMovesExistingOrderAndSavesNewOrdersAndPostageTogether()
    {
        var options = Options();
        using (var db = new AppDbContext(options))
        {
            db.order.AddRange(Order("conflict", true), Order("postage-target"));
            db.refunds.Add(new RefundRecord { refundId = "r", orderId = "conflict", productName = "原商品",
                status = "退款成功", refundAmount = 35 });
            await db.SaveChangesAsync();
        }
        using (var db = new AppDbContext(options))
        {
            var incoming = Order("conflict");
            incoming.price = 0;
            var request = new OrderImportRequest { Batch = "新批次", Orders = [Order("new"), incoming],
                NewUsers = [NewUser()], NewUserAssignments = new() { ["new"] = 0 },
                PostageOrderIds = ["postage-target"] };
            await OrderImportService.SaveAsync(db, request);
        }
        using (var db = new AppDbContext(options))
        {
            Assert.Single(await db.user.ToListAsync());
            Assert.Equal(3, await db.order.CountAsync());
            Assert.Equal(0, (await db.order.SingleAsync(o => o.orderId == "conflict")).price);
            Assert.Equal("新批次", (await db.order.SingleAsync(o => o.orderId == "conflict")).tag);
            Assert.Equal("新批次", (await db.order.SingleAsync(o => o.orderId == "new")).tag);
            Assert.True((await db.order.SingleAsync(o => o.orderId == "conflict")).sended);
            Assert.True((await db.order.SingleAsync(o => o.orderId == "postage-target")).sended);
            Assert.Equal("原批次", (await db.order.SingleAsync(o => o.orderId == "postage-target")).tag);
            Assert.Equal(35, (await db.refunds.SingleAsync()).refundAmount);
        }
    }

    [Fact]
    public async Task SameBatchLaterImportWinsForAllAmountFieldsAndKeepsOneOrder()
    {
        var options = Options();
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new() { Batch = "同批次", Orders = [Order("same", true)] });
        }
        using (var db = new AppDbContext(options))
        {
            var newer = Order("same");
            newer.price = 21;
            newer.receivableAmount = 35;
            newer.productRefundAmount = 14;
            newer.orderRefundStatus = "退款完成(部分退款)";
            await OrderImportService.SaveAsync(db, new() { Batch = "同批次", Orders = [newer] });
        }
        using (var db = new AppDbContext(options))
        {
            var saved = Assert.Single(await db.order.ToListAsync());
            Assert.Equal("同批次", saved.tag);
            Assert.Equal(21, saved.price);
            Assert.Equal(35, saved.receivableAmount);
            Assert.Equal(14, saved.productRefundAmount);
            Assert.Equal("退款完成(部分退款)", saved.orderRefundStatus);
            Assert.True(saved.sended);
            Assert.Equal([5], saved.productCount); // Missing refund details suppress Set gifts.
        }
    }

    [Fact]
    public async Task LastImportedBatchWinsEvenWhenNameSortsEarlierAndSalesMoveWithoutDuplication()
    {
        var options = Options();
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new() { Batch = "9批", Orders = [Order("move"), Order("stay")] });
            await OrderImportService.SaveAsync(db, new() { Batch = "10批", Orders = [Order("move")] });
        }
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new() { Batch = "1批", Orders = [Order("move")] });
        }
        using (var db = new AppDbContext(options))
        {
            var orders = await db.order.ToListAsync();
            Assert.Equal(2, orders.Count);
            Assert.Equal("1批", orders.Single(o => o.orderId == "move").tag);
            Assert.Equal("9批", orders.Single(o => o.orderId == "stay").tag);
            var snapshots = orders.Select(o => new SoldOrderSnapshot(o.orderId, o.tag,
                RefundCalculator.Calculate(o.orderId, [new(o.productName[0], o.productModel[0], o.productCount[0])], []))).ToList();
            Assert.Equal(10, SoldProductsQuery.Apply(snapshots).NetQuantity);
            Assert.Equal(5, SoldProductsQuery.Apply(snapshots, tag: "1批").NetQuantity);
            Assert.Equal(5, SoldProductsQuery.Apply(snapshots, tag: "9批").NetQuantity);
            Assert.Empty(SoldProductsQuery.Apply(snapshots, tag: "10批").Products);
        }
    }

    [Fact]
    public async Task PostageOnlyImportCanMarkOrdersFromAnotherBatchAndRefundImportIsIndependent()
    {
        var options = Options();
        using (var db = new AppDbContext(options))
        {
            db.order.Add(Order("o1"));
            await db.SaveChangesAsync();
            await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "补邮", PostageOrderIds = ["o1"] });
            await RefundImportService.SaveAsync(db, [new RefundRecord { refundId = "r", orderId = "o1",
                productName = "原商品", status = "退款成功", refundAmount = 7, sourceFile = "其他批次退款.xls" }]);
        }
        using (var db = new AppDbContext(options))
        {
            var saved = await db.order.SingleAsync();
            Assert.True(saved.sended);
            Assert.Equal("原批次", saved.tag);
            Assert.Equal(35, saved.price);
            Assert.Single(await db.refunds.ToListAsync());
        }
    }

    [BackupPostgresFact]
    public async Task PostgresImportSavesNewUserLinksAndRollsBackWholeRequestOnWriteFailure()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COOKIESHEET_BACKUP_TEST_ADMIN"));
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(55439, builder.Port);
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var database = "cookiesheet_import_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        builder.Pooling = false;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options;
        try
        {
            using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.giftChoices.AddRange(new[] { "原批次", "新批次" }
                    .Select(batch => new BatchGiftChoice { Batch = batch, FullGift = true, SetGift = true }));
                await db.SaveChangesAsync();
                await OrderImportService.SaveAsync(db, new OrderImportRequest { Batch = "原批次",
                    Orders = [Order("first"), Order("second")], NewUsers = [NewUser()],
                    NewUserAssignments = new() { ["first"] = 0, ["second"] = 0 } });
            }
            using (var db = new AppDbContext(options))
            {
                var owner = await db.user.SingleAsync();
                Assert.All(await db.order.ToListAsync(), o => Assert.Equal(owner.userId, o.userId));
                var update = Order("first");
                update.price = 7;
                var invalid = Order("invalid");
                invalid.userId = int.MaxValue; // Deliberately fail the foreign key after validation.
                await Assert.ThrowsAsync<DbUpdateException>(() => OrderImportService.SaveAsync(db,
                    new OrderImportRequest { Batch = "新批次", Orders = [update, Order("new"), invalid],
                        NewUsers = [NewUser()], NewUserAssignments = new() { ["new"] = 0 },
                        PostageOrderIds = ["second"] }));
            }
            using (var db = new AppDbContext(options))
            {
                Assert.Single(await db.user.ToListAsync());
                Assert.Equal(2, await db.order.CountAsync());
                Assert.All(await db.order.ToListAsync(), o => { Assert.Equal(35, o.price); Assert.False(o.sended); Assert.Equal("原批次", o.tag); });
            }
            using (var db = new AppDbContext(options))
            {
                var overflow = Order("first");
                overflow.price = ((decimal)int.MaxValue + 1) * 99;
                await Assert.ThrowsAsync<OverflowException>(() => OrderImportService.SaveAsync(db,
                    new() { Batch = "计算失败批次", Orders = [overflow], GiftChoices =
                        [new() { Batch = "原批次" }, new() { Batch = "计算失败批次", FullGift = true }] }));
            }
            using (var db = new AppDbContext(options))
            {
                var saved = await db.order.SingleAsync(o => o.orderId == "first");
                Assert.Equal(35, saved.price);
                Assert.Equal("原批次", saved.tag);
                Assert.Null(await db.giftChoices.FindAsync("计算失败批次"));
                Assert.True((await db.giftChoices.FindAsync("原批次"))!.FullGift);
                Assert.True((await db.giftChoices.FindAsync("原批次"))!.SetGift);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
