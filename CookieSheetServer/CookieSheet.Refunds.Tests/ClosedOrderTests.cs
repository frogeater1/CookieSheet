using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class ClosedOrderTests
{
    private static order Order(string id, decimal paid = 100) => new()
    {
        orderId = id, userId = 1, tel = "", orderedAt = DateTimeOffset.UtcNow,
        productName = ["商品"], productModel = ["型号"], productCount = [2],
        price = paid, tag = "批次", sended = true
    };

    private static List<BatchGiftChoice> Choices() => [new() { Batch = "批次", FullGift = true, SetGift = true }];

    [Fact]
    public void MissingOrderStatusColumnIsRejectedInsteadOfImportingUnknownClosedOrders()
    {
        using var file = RefundTests.Spreadsheet([
            ["订单编号", "商品名称", "商品件数", "实付金额"],
            ["o1", "商品", "1", "100"]]);
        Assert.Contains("订单状态", Assert.Throws<FormatException>(() => OrderImportReader.Read(file, "批次.xlsx").ToList()).Message);
    }

    [Theory]
    [InlineData("已关闭", true)]
    [InlineData(" 已关闭 ", true)]
    [InlineData("已完成", false)]
    [InlineData("已发货", false)]
    [InlineData("", false)]
    public void ClosedStatusIsIndependentOfRefundStatus(string status, bool closed)
    {
        Assert.Equal(closed, new ImportRecord { 订单状态 = status, 退款状态 = "退款完成(全额退款)" }.IsClosed);
    }

    [Theory]
    [InlineData("已关闭", "已完成", false)]
    [InlineData("已完成", "已关闭", true)]
    public void LastDuplicateDeterminesClosedStatus(string earlier, string later, bool closed)
    {
        var row = Assert.Single(OrderImportReader.LatestByOrderId([
            new() { 订单编号 = "o1", 订单状态 = earlier, 实付金额 = "100" },
            new() { 订单编号 = "o1", 订单状态 = later, 实付金额 = "50" }
        ]));
        Assert.Equal(closed, row.IsClosed);
        Assert.Equal("50", row.实付金额);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public async Task ClosingExistingOrderDeletesItAndRecalculatesMoneyProductsAndGifts(decimal closedPaid)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new() { Batch = "批次",
                Orders = [Order("keep"), Order("close", closedPaid)], GiftChoices = Choices() });
            db.refunds.AddRange(
                new RefundRecord { refundId = "closed-refund", orderId = "close", status = "退款成功" },
                new RefundRecord { refundId = "unknown-refund", orderId = "unknown", status = "退款成功" });
            await db.SaveChangesAsync();
        }
        using (var db = new AppDbContext(options))
        {
            await OrderImportService.SaveAsync(db, new() { Batch = "其他文件名",
                ClosedOrderIds = ["close", "never-imported"], GiftChoices = Choices() });
        }
        using (var db = new AppDbContext(options))
        {
            var remaining = Assert.Single(await db.order.ToListAsync());
            Assert.Equal("keep", remaining.orderId);
            Assert.Equal(100, remaining.price);
            Assert.Equal(["商品", "99满赠", "Set赠"], remaining.productName);
            Assert.Equal([2, 1, 2], remaining.productCount);
            var totals = SoldProductsQuery.Apply([new(remaining.orderId, remaining.tag,
                RefundCalculator.Calculate(remaining.orderId, [new("商品", "型号", remaining.productCount[0])], []))]);
            Assert.Equal(2, totals.NetQuantity);
            Assert.Equal(1, await db.order.CountAsync());
            Assert.False(await db.order.AnyAsync(o => o.orderId == "close"));
            Assert.Equal(2, await db.refunds.CountAsync());

            // Repeat closure does not require choices for an already excluded historical batch.
            await OrderImportService.SaveAsync(db, new() { Batch = "其他文件名", ClosedOrderIds = ["close"], GiftChoices = [] });
        }
    }

    [Fact]
    public async Task OnlyClosedNewRowsCreateNoOrdersOrUsers()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await OrderImportService.SaveAsync(db, new() { Batch = "批次", ClosedOrderIds = ["new"], GiftChoices = [] });
        Assert.Empty(await db.order.ToListAsync());
        Assert.Empty(await db.user.ToListAsync());
    }

}
