using Xunit;

namespace CookieSheet.Refunds.Tests;

public class InvalidRefundFallbackTests
{
    private static RefundRecord Refund(string id, string model, decimal amount, decimal paid = 21) => new()
    {
        refundId = id, orderId = "o1", productName = "商品", productModel = model,
        refundAmount = amount, productPaidAmount = paid, status = "退款成功"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidProductKeepsAllUnitsWhileValidRefundsStillDeduct(bool conflictingPaid)
    {
        OrderedProduct[] products = [new("商品", "A", 3), new("商品", "B", 3)];
        List<RefundRecord> refunds = conflictingPaid
            ? [Refund("bad1", "A", 7), Refund("bad2", "A", 7, 28)]
            : [Refund("bad1", "A", 28)];
        refunds.Add(Refund("good", "B", 7));
        var results = RefundCalculator.Calculate("o1", products, refunds);
        Assert.Equal(ProductRefundStatus.InvalidDetails, results[0].Status);
        Assert.Equal(0, results[0].RefundedQuantity);
        Assert.True(results[0].RefundedAmount > 0); // Keep the financial evidence for review.
        Assert.Contains("按未退款发货", results[0].Annotation);
        Assert.Equal(1, results[1].RefundedQuantity);
        var summary = SoldProductsQuery.Apply([new("o1", "批次", results)]);
        Assert.Equal(5, summary.NetQuantity);
        Assert.Equal(0, summary.ReviewCount);
        Assert.Equal(1, summary.InvalidDetailsCount);
        Assert.Equal(3, SoldProductsQuery.Apply([new("o1", "批次", results)], keyword: "A").NetQuantity);
        Assert.Equal(2, SoldProductsQuery.Apply([new("o1", "批次", results)], keyword: "B").NetQuantity);
        var set = Assert.Single(GiftCalculator.Calculate([new("o1", 1, "批次", DateTimeOffset.UtcNow, 120, results)]));
        Assert.Equal(2, set.SetGiftCount);
        Assert.Equal(1, set.FullGiftCount);
    }

    [Fact]
    public void StaleOrderSnapshotKeepsEveryModelAndCorrectionRestoresConfirmedRefunds()
    {
        OrderedProduct[] products = [new("商品", "A", 3), new("商品", "B", 3)];
        var refund = Refund("r1", "A", 21);
        var before = RefundCalculator.Calculate("o1", products, [refund], new(0, 42, 42, ""));
        Assert.All(before, p => { Assert.Equal(ProductRefundStatus.InvalidDetails, p.Status); Assert.Equal(0, p.RefundedQuantity); });
        Assert.Equal(6, SoldProductsQuery.Apply([new("o1", "批次", before)]).NetQuantity);
        Assert.Equal(3, Assert.Single(GiftCalculator.Calculate([new("o1", 1, "批次", DateTimeOffset.UtcNow, 42, before)])).SetGiftCount);
        var corrected = RefundCalculator.Calculate("o1", products, [refund], new(21, 42, 21, "退款完成(部分退款)"));
        Assert.Equal(ProductRefundStatus.Full, corrected[0].Status);
        Assert.Equal(3, corrected[0].RefundedQuantity);
        Assert.Equal(3, SoldProductsQuery.Apply([new("o1", "批次", corrected)]).NetQuantity);
        Assert.Equal(0, Assert.Single(GiftCalculator.Calculate([new("o1", 1, "批次", DateTimeOffset.UtcNow, 21, corrected)])).SetGiftCount);
    }

    [Fact]
    public void InvalidFallbackDoesNotHideMissingOrFractionalRefundsOnOtherOrders()
    {
        OrderedProduct[] products = [new("商品", "A", 3)];
        var invalid = RefundCalculator.Calculate("o1", products, [Refund("r1", "A", 28)]);
        var missing = RefundCalculator.Calculate("o2", products, [], new(7, 21, 14, ""));
        var fractional = RefundCalculator.Calculate("o1", products, [Refund("r2", "A", 5)]);
        foreach (var unresolved in new[] { missing, fractional })
        {
            var summary = SoldProductsQuery.Apply([new("o1", "批次", invalid), new("o2", "批次", unresolved)]);
            Assert.Null(summary.NetQuantity);
            Assert.Equal(1, summary.ReviewCount);
            Assert.Null(Assert.Single(GiftCalculator.Calculate([
                new("o1", 1, "批次", DateTimeOffset.UtcNow, 21, invalid),
                new("o2", 1, "批次", DateTimeOffset.UtcNow, 21, unresolved)])).SetGiftCount);
        }
    }

    [Fact]
    public void AwaitingDetailsDisplaysOriginalUnitsPlusValidNetSalesAndRespectsFilters()
    {
        OrderedProduct[] products = [new("商品", "A", 3)];
        var awaiting = RefundCalculator.Calculate("o1", products, [Refund("r1", "A", 7)], new(14, 21, 7, ""));
        var confirmedRefund = Refund("r2", "A", 7);
        confirmedRefund.orderId = "o2";
        var valid = RefundCalculator.Calculate("o2", products, [confirmedRefund], new(7, 21, 14, ""));
        SoldOrderSnapshot[] orders = [new("o1", "批次A", awaiting), new("o2", "批次A", valid), new("o3", "批次B", valid)];
        var summary = SoldProductsQuery.Apply(orders, tag: "批次A");
        Assert.Null(summary.NetQuantity);
        Assert.Equal(5, summary.DisplayNetQuantity); // 3 original awaiting + 2 after a confirmed refund.
        Assert.Equal("5（待导入退款明细）", Assert.Single(summary.Products).NetText);
        Assert.Equal("净售出 5 件（待导入退款明细）", summary.NetSummaryText);
        Assert.Equal("净售出 2 件", SoldProductsQuery.Apply(orders, tag: "批次B").NetSummaryText);
        Assert.Equal(0, SoldProductsQuery.Apply(orders, keyword: "无匹配").DisplayNetQuantity);
        var completed = RefundCalculator.Calculate("o1", products, [Refund("r1", "A", 14)], new(14, 21, 7, ""));
        var refreshed = SoldProductsQuery.Apply([new("o1", "批次A", completed), new("o2", "批次A", valid)]);
        Assert.Equal("净售出 3 件", refreshed.NetSummaryText);
        Assert.Equal(3, refreshed.NetQuantity);
    }
}
