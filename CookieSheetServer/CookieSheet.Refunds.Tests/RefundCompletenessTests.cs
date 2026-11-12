using Xunit;

namespace CookieSheet.Refunds.Tests;

public class RefundCompletenessTests
{
    private static readonly OrderedProduct Card = new("企划", "明信片", 3);
    private static RefundRecord Refund(string id, decimal amount, string model = "明信片") => new()
    {
        refundId = id, orderId = "o1", productName = "企划", productModel = model,
        productPaidAmount = 21, refundAmount = amount, status = "退款成功"
    };

    [Theory]
    [InlineData(21, 0, ProductRefundStatus.AwaitingDetails, "待导入退款明细")]
    [InlineData(14, 7, ProductRefundStatus.AwaitingDetails, "待导入退款明细")]
    [InlineData(7, 14, ProductRefundStatus.InvalidDetails, "退款明细有误")]
    [InlineData(0, 7, ProductRefundStatus.InvalidDetails, "退款明细有误")]
    public void MissingDetailsRemainUnknownButInvalidDetailsKeepOriginalQuantity(decimal expected, decimal actual,
        ProductRefundStatus status, string text)
    {
        var refunds = actual == 0 ? new List<RefundRecord>() : [Refund("r1", actual)];
        var product = Assert.Single(RefundCalculator.Calculate("o1", [Card], refunds,
            new(expected, 21, 21 - expected, "退款完成(部分退款)")));
        Assert.Equal(status, product.Status);
        Assert.Equal(status == ProductRefundStatus.InvalidDetails ? 0 : (int?)null, product.RefundedQuantity);
        Assert.Contains(text, product.Annotation);
        var summary = SoldProductsQuery.Apply([new("o1", "批次", [product])]);
        Assert.Equal(status == ProductRefundStatus.InvalidDetails ? 3 : (long?)null, summary.NetQuantity);
        Assert.Equal(status == ProductRefundStatus.InvalidDetails ? "3" : $"3（{text}）", Assert.Single(summary.Products).NetText);
    }

    [Fact]
    public void CompletingDetailsRestoresQuantityAndRepeatedIdsDoNotDoubleCount()
    {
        var snapshot = new OrderRefundSnapshot(14, 21, 7, "退款完成(部分退款)");
        List<RefundRecord> refunds = [Refund("r1", 7)];
        Assert.Equal(ProductRefundStatus.AwaitingDetails,
            Assert.Single(RefundCalculator.Calculate("o1", [Card], refunds, snapshot)).Status);
        refunds.Add(Refund("r2", 7));
        refunds.Add(Refund("r2", 7));
        refunds.Add(new() { refundId = "pending", orderId = "o1", refundAmount = 7, status = "待处理" });
        var products = RefundCalculator.Calculate("o1", [Card], refunds, snapshot);
        Assert.Equal(2, Assert.Single(products).RefundedQuantity);
        Assert.Equal(1, SoldProductsQuery.Apply([new("o1", "批次", products)]).NetQuantity);
    }

    [Fact]
    public void CompleteFullRefundStillDisplaysZero()
    {
        var products = RefundCalculator.Calculate("o1", [Card], [Refund("r1", 21)], new(21, 21, 0, "退款完成(全额退款)"));
        Assert.Equal("0", Assert.Single(SoldProductsQuery.Apply([new("o1", "批次", products)]).Products).NetText);
    }

    [Fact]
    public void MissingModelCannotBeInferredFromOrderTotalAndGiftsRemainIncluded()
    {
        var products = RefundCalculator.Calculate("o1", [Card, new("企划", "色纸", 2),
            new("99满赠", "99满赠", 1), new("Set赠", "Set赠", 1), new("补邮", "补邮", 1)],
            [Refund("r1", 7)], new(14, 97, 83, "退款完成(部分退款)"));
        Assert.All(products.Take(2), p => Assert.Equal(ProductRefundStatus.AwaitingDetails, p.Status));
        Assert.All(products.Skip(2), p => Assert.Equal(ProductRefundStatus.None, p.Status));
        var orders = new[] { new SoldOrderSnapshot("o1", "批次", products) };
        Assert.Null(SoldProductsQuery.Apply(orders).NetQuantity);
        Assert.Equal(1, SoldProductsQuery.Apply(orders, "99满赠", includeGifts: true).NetQuantity);
        Assert.Equal(4, SoldProductsQuery.Apply(orders, includeGifts: true).Products.Count);
        Assert.Empty(SoldProductsQuery.Apply(orders, tag: "其他批次").Products);
    }

    [Fact]
    public void ShippingIsNotComparedToProductRefundAndLegacyTotalsIncludeShipping()
    {
        var refund = Refund("r1", 0);
        refund.shippingRefundAmount = 8;
        foreach (decimal? expected in new decimal?[] { 0, null })
        {
            var product = Assert.Single(RefundCalculator.Calculate("o1", [Card], [refund],
                new(expected, 29, 21, "退款完成(部分退款)")));
            Assert.Equal(ProductRefundStatus.None, product.Status);
            Assert.Equal(0, product.RefundedQuantity);
        }
        // A known zero product refund needs no shipping detail to determine item sales.
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [Card], [],
            new(0, 29, 21, "退款完成(部分退款)"))).Status);
    }

    [Fact]
    public void UnmatchedDetailCannotSatisfyCompletenessAndDoesNotDeduct()
    {
        var product = Assert.Single(RefundCalculator.Calculate("o1", [Card], [Refund("r1", 7, "错误型号")],
            new(7, 21, 14, "退款完成(部分退款)")));
        Assert.Equal(ProductRefundStatus.InvalidDetails, product.Status);
        Assert.Equal(0, product.RefundedAmount);
        Assert.Equal(0, product.RefundedQuantity);
    }

    [Fact]
    public void ExistingOrdersWithoutNewFieldUseAmountAndStatusFallback()
    {
        Assert.Equal(ProductRefundStatus.AwaitingDetails, Assert.Single(RefundCalculator.Calculate("o1", [Card], [],
            new(null, 21, 0, "退款完成(全额退款)"))).Status);
        Assert.Equal(ProductRefundStatus.AwaitingDetails, Assert.Single(RefundCalculator.Calculate("o1", [Card], [],
            new(null, null, 0, "退款完成(全额退款)"))).Status);
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [Card], [],
            new(null, null, 21, ""))).Status);
    }

    [Fact]
    public void MatchingAmountsDoNotHideUncertainQuantities()
    {
        var product = Assert.Single(RefundCalculator.Calculate("o1", [Card], [Refund("r1", 5)],
            new(5, 21, 16, "退款完成(部分退款)")));
        Assert.Equal(ProductRefundStatus.NeedsReview, product.Status);
        Assert.Equal("待核对", Assert.Single(SoldProductsQuery.Apply([new("o1", "批次", [product])]).Products).NetText);
    }

    [Fact]
    public void KnownInvalidDetailTakesPriorityOverOtherMissingDetails()
    {
        var product = Assert.Single(RefundCalculator.Calculate("o1", [Card],
            [Refund("r1", 22)], new(30, 40, 10, "退款完成(部分退款)")));
        Assert.Equal(ProductRefundStatus.InvalidDetails, product.Status);
        var missing = RefundCalculator.Calculate("o2", [Card], [], new(7, 21, 14, "退款完成(部分退款)"));
        var healthy = RefundCalculator.Calculate("o3", [Card], [], new(0, 21, 21, ""));
        SoldOrderSnapshot[] orders = [new("o1", "批次A", [product]), new("o2", "批次A", missing), new("o3", "批次B", healthy)];
        var summary = SoldProductsQuery.Apply(orders);
        Assert.Null(summary.NetQuantity);
        Assert.Equal(1, summary.InvalidDetailsCount);
        Assert.Equal(1, summary.AwaitingDetailsCount);
        Assert.Equal("6（待导入退款明细）", Assert.Single(summary.Products.Where(p => p.Tag == "批次A")).NetText);
        Assert.Equal(3, SoldProductsQuery.Apply(orders, tag: "批次B").NetQuantity);
    }

    [Fact]
    public void FixedOrderHeaderPreservesUnknownVersusZeroAndRejectsBadMoney()
    {
        using var file = RefundTests.Spreadsheet([
            ["订单编号", "商品名称", "商品件数", "实付金额", "商品已退款金额", "订单状态"],
            ["o1", "企划", "3", "7", "14", "已完成"],
            ["o2", "企划", "3", "21", "0", "已完成"],
            ["o3", "企划", "3", "21", "", "已完成"]]);
        Assert.Equal(new decimal?[] { 14, 0, null }, OrderImportReader.Read(file, "批次.xlsx")
            .Select(r => OrderAmountRules.ReadProductRefund(r.商品已退款金额)));
        Assert.Throws<FormatException>(() => OrderAmountRules.ReadProductRefund("-1"));
        Assert.Throws<FormatException>(() => OrderAmountRules.ReadProductRefund("错误"));
    }
}
