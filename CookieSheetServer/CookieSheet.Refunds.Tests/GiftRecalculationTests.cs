using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class GiftRecalculationTests
{
    private static AppDbContext Database()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.giftChoices.AddRange(new BatchGiftChoice { Batch = "A", FullGift = true, SetGift = true },
            new BatchGiftChoice { Batch = "B", FullGift = true, SetGift = true });
        db.SaveChanges();
        return db;
    }
    private static order Order(string id, decimal paid, int owner = 1, int quantity = 1, params string[] models) => new()
    {
        orderId = id, userId = owner, tag = "A", price = paid, tel = "原电话", sended = true,
        orderedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(id.Length),
        productName = models.Select(_ => "商品").ToArray(), productModel = models,
        productCount = models.Select(_ => quantity).ToArray()
    };
    private static int Gift(IEnumerable<order> orders, string name) => orders.Sum(o =>
        o.productModel.Select((model, i) => model == name ? o.productCount[i] : 0).Sum());
    private static RefundRecord Refund(string id, decimal amount, decimal paid = 21) => new()
    {
        refundId = id, orderId = "o1", productName = "商品", productModel = "A型", status = "退款成功",
        productPaidAmount = paid, refundAmount = amount
    };

    [Fact]
    public async Task AppendingExistingBatchAndReimportingRecalculatesWithoutAccumulatingGifts()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 98, 1, 1, "A型", "B型")] });
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "99满赠"));
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o2", 2, 1, 1, "A型", "B型")] });
        Assert.Equal(1, Gift(await db.order.ToListAsync(), "99满赠"));
        Assert.Equal(2, Gift(await db.order.ToListAsync(), "Set赠"));
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 98, 1, 999, "错误商品")] });
        var orders = await db.order.ToListAsync();
        Assert.Equal(1, Gift(orders, "99满赠"));
        Assert.Equal(2, Gift(orders, "Set赠"));
        Assert.All(orders, o => { Assert.True(o.sended); Assert.Equal("原电话", o.tel); });
        Assert.Equal(2, orders.Count);
        Assert.Equal(1, orders.Single(o => o.orderId == "o1").productCount[0]);
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 50, 1, 1, "A型", "B型")] });
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "99满赠"));
    }

    [Fact]
    public async Task MovingOrderRecalculatesOldAndNewBatchAndLeavesUnrelatedBatchUntouched()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 60, 1, 1, "A型"), Order("o2", 60, 1, 1, "A型")] });
        await OrderImportService.SaveAsync(db, new() { Batch = "B", Orders = [Order("o3", 60, 1, 1, "A型")] });
        var untouched = Order("untouched", 0, 3, 7, "99满赠");
        untouched.tag = "C";
        db.order.Add(untouched);
        await db.SaveChangesAsync();
        var summary = await OrderImportService.SaveAsync(db, new() { Batch = "B", Orders = [Order("o2", 60, 1, 1, "A型")] });
        var all = await db.order.ToListAsync();
        Assert.Equal(2, summary.Batches);
        Assert.Equal(0, Gift(all.Where(o => o.tag == "A"), "99满赠"));
        Assert.Equal(1, Gift(all.Where(o => o.tag == "A"), "Set赠"));
        Assert.Equal(1, Gift(all.Where(o => o.tag == "B"), "99满赠"));
        Assert.Equal(2, Gift(all.Where(o => o.tag == "B"), "Set赠"));
        Assert.Equal(7, Gift(all.Where(o => o.tag == "C"), "99满赠"));
    }

    [Fact]
    public async Task NewModelRecalculatesOtherUsersInBatchRatherThanOnlyImportedOrders()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 20, 1, 1, "A型", "B型")] });
        Assert.Equal(1, Gift(await db.order.ToListAsync(), "Set赠"));
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o2", 20, 2, 1, "C型")] });
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
    }

    [Theory]
    [InlineData(7, 2)]
    [InlineData(14, 1)]
    [InlineData(21, 0)]
    public async Task SetGiftUsesRemainingQuantitiesAndRefundImportPreservesOrderMoney(decimal amount, int expected)
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 42, 1, 3, "A型", "B型")] });
        Assert.Equal(3, Gift(await db.order.ToListAsync(), "Set赠"));
        var result = await RefundImportService.SaveAsync(db, [Refund("r1", amount)]);
        Assert.Equal(0, result.Gifts!.PendingSetGroups);
        var saved = await db.order.SingleAsync();
        Assert.Equal(expected, Gift([saved], "Set赠"));
        Assert.Equal(42, saved.price);
        Assert.Equal([3, 3], saved.productCount.Take(2));
        Assert.True(saved.sended);
        await RefundImportService.SaveAsync(db, [Refund("r1", amount)]);
        Assert.Equal(expected, Gift(await db.order.ToListAsync(), "Set赠"));
    }

    [Fact]
    public async Task MissingRefundDetailsRemoveOldSetAndCompletingDetailsRestoresCorrectGift()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 42, 1, 3, "A型", "B型")] });
        var updated = Order("o1", 28, 1, 3, "A型", "B型");
        updated.productRefundAmount = 14;
        var pending = await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [updated] });
        Assert.Equal(1, pending.PendingSetGroups);
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
        var incomplete = await RefundImportService.SaveAsync(db, [Refund("r1", 7)]);
        Assert.Equal(1, incomplete.Gifts!.PendingSetGroups);
        var complete = await RefundImportService.SaveAsync(db, [Refund("r2", 7)]);
        Assert.Equal(0, complete.Gifts!.PendingSetGroups);
        Assert.Equal(1, Gift(await db.order.ToListAsync(), "Set赠"));
        await RefundImportService.DeleteAsync(db, "r2");
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
        Assert.Equal(1, (await RefundImportService.SaveAsync(db, [Refund("r2", 7)])).DeletedSkipped);
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
    }

    [Fact]
    public async Task FractionalRefundSuppressesSetButDoesNotAffectOtherUsers()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 120, 1, 3, "A型"), Order("o2", 120, 2, 3, "A型")] });
        var result = await RefundImportService.SaveAsync(db, [Refund("r1", 5)]);
        Assert.Equal(1, result.Gifts!.PendingSetGroups);
        var all = await db.order.ToListAsync();
        Assert.Equal(0, Gift(all.Where(o => o.userId == 1), "Set赠"));
        Assert.Equal(3, Gift(all.Where(o => o.userId == 2), "Set赠"));
        Assert.Equal(2, Gift(all, "99满赠"));
    }

    [Fact]
    public void CalculatorIgnoresPostageGiftsAndKeepsCatalogueOfFullyRefundedModels()
    {
        ProductRefundResult Line(string model, int quantity, int refunded = 0) =>
            new(new("商品", model, quantity), refunded == 0 ? ProductRefundStatus.None : ProductRefundStatus.Full, refunded, 0, "");
        var result = GiftCalculator.Calculate([
            new("o1",1,"A",DateTimeOffset.UtcNow,198,[Line("A型",2,2),Line("B型",2),Line("99满赠",999),Line("非偏远地区补邮",999)]),
            new("o2",2,"A",DateTimeOffset.UtcNow,0,[Line("B型",2)])]);
        Assert.All(result, r => Assert.Equal(0, r.SetGiftCount));
        Assert.Equal(2, result.Single(r => r.UserId == 1).FullGiftCount);
    }

    [Theory]
    [InlineData(false, false, 0, 0)]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, true, 0, 3)]
    [InlineData(true, true, 1, 3)]
    public async Task ReimportChoiceReplacesPreviousChoiceAndOldGifts(bool full, bool set, int fullCount, int setCount)
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 120, 1, 3, "A型")] });
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 120, 1, 99, "错误商品")],
            GiftChoices = [new() { Batch = "A", FullGift = full, SetGift = set }] });
        Assert.Equal(fullCount, Gift(await db.order.ToListAsync(), "99满赠"));
        Assert.Equal(setCount, Gift(await db.order.ToListAsync(), "Set赠"));
        Assert.Equal(full, (await db.giftChoices.FindAsync("A"))!.FullGift);
        Assert.Equal(set, (await db.giftChoices.FindAsync("A"))!.SetGift);
        // A later import can independently change both choices again.
        await RefundImportService.SaveAsync(db, [Refund("r1", 7)],
            [new() { Batch = "A", FullGift = !full, SetGift = !set }]);
        Assert.Equal(full ? 0 : 1, Gift(await db.order.ToListAsync(), "99满赠"));
        Assert.Equal(set ? 0 : 2, Gift(await db.order.ToListAsync(), "Set赠"));
        await RefundImportService.DeleteAsync(db, "r1");
        Assert.Equal(set ? 0 : 3, Gift(await db.order.ToListAsync(), "Set赠"));
    }

    [Fact]
    public async Task DisabledSetDoesNotReportReviewAndDisabledFullDoesNotCalculateAmount()
    {
        await using var db = Database();
        var original = Order("o1", ((decimal)int.MaxValue + 1) * 99, 1, 3, "A型");
        original.productRefundAmount = 7;
        var result = await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [original],
            GiftChoices = [new() { Batch = "A" }] });
        Assert.Equal(0, result.PendingSetGroups);
        var saved = await db.order.SingleAsync();
        Assert.Equal([3], saved.productCount);
    }

    [Fact]
    public async Task MovingBetweenBatchesUsesEachCurrentChoice()
    {
        await using var db = Database();
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [Order("o1", 120, 1, 3, "A型"), Order("o2", 120, 1, 3, "A型")] });
        await OrderImportService.SaveAsync(db, new() { Batch = "B", Orders = [Order("o1", 120)], GiftChoices =
            [new() { Batch = "A", FullGift = true }, new() { Batch = "B", SetGift = true }] });
        var all = await db.order.ToListAsync();
        Assert.Equal(1, Gift(all.Where(o => o.tag == "A"), "99满赠"));
        Assert.Equal(0, Gift(all.Where(o => o.tag == "A"), "Set赠"));
        Assert.Equal(0, Gift(all.Where(o => o.tag == "B"), "99满赠"));
        Assert.Equal(3, Gift(all.Where(o => o.tag == "B"), "Set赠"));
    }

    [Fact]
    public async Task MissingChoiceFailsBeforeSavingAndUnknownHistoricalBatchIsUntouched()
    {
        await using var db = Database();
        var original = Order("old", 120, 1, 5, "99满赠", "Set赠");
        original.tag = "历史批次";
        db.order.Add(original);
        await db.SaveChangesAsync();
        await GiftRecalculationService.RecalculateAsync(db, ["历史批次"]);
        Assert.Equal(5, Gift(await db.order.ToListAsync(), "Set赠"));
        await Assert.ThrowsAsync<ArgumentException>(() => OrderImportService.SaveAsync(db,
            new() { Batch = "新批次", Orders = [Order("new", 120)], GiftChoices = [] }));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.order.CountAsync());
    }

    [Fact]
    public async Task InvalidRefundImportKeepsSetAndOriginalUnitsUntilOrderSnapshotIsCorrected()
    {
        await using var db = Database();
        var original = Order("o1", 42, 1, 3, "A型", "B型");
        original.productRefundAmount = 0;
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [original] });
        var summary = await RefundImportService.SaveAsync(db, [Refund("r1", 21)],
            [new() { Batch = "A", SetGift = true }]);
        Assert.Equal(0, summary.Gifts!.PendingSetGroups);
        Assert.Equal(3, Gift(await db.order.ToListAsync(), "Set赠"));
        var stored = await db.order.SingleAsync();
        Assert.Equal(42, stored.price);
        Assert.Equal([3, 3], stored.productCount.Take(2));
        Assert.Equal(21, (await db.refunds.SingleAsync()).refundAmount);
        await RefundImportService.SaveAsync(db, [Refund("r1", 21)], [new() { Batch = "A" }]);
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
        var corrected = Order("o1", 21, 1, 3, "A型", "B型");
        corrected.productRefundAmount = 21;
        await OrderImportService.SaveAsync(db, new() { Batch = "A", Orders = [corrected],
            GiftChoices = [new() { Batch = "A", SetGift = true }] });
        Assert.Equal(0, Gift(await db.order.ToListAsync(), "Set赠"));
    }
}
