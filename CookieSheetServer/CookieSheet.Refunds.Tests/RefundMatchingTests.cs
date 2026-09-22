using Xunit;

namespace CookieSheet.Refunds.Tests;

public class RefundMatchingTests
{
    private static RefundRecord Refund(string id, string orderId, string name = "企划", string model = "明信片", string status = "退款成功") =>
        new() { refundId = id, orderId = orderId, productName = name, productModel = model, status = status };

    [Fact]
    public void UnmatchedListExcludesPostageOtherStatusesAndMatchedProducts()
    {
        var orders = new Dictionary<string, IReadOnlyList<OrderedProduct>>
        {
            ["existing"] = [new("企划", "明信片", 5, 7)],
            ["ambiguous"] = [new("企划", "明信片", 1, 7), new("企划", "明信片", 2, 7)]
        };
        var refunds = new[]
        {
            Refund("missing-order", "missing"), Refund("missing-product", "existing", model: "色纸"),
            Refund("multiple", "ambiguous"), Refund("matched", "existing"),
            Refund("postage", "missing", name: " 补邮 "), Refund("complete", "missing", status: "退款完成"),
            Refund("pending", "missing", status: "商家发起退款，待买家处理"),
            Refund("cancelled", "missing", status: "买家取消退款，交易恢复正常")
        };
        var results = RefundMatching.FindUnmatched(refunds, orders).ToDictionary(r => r.Refund.refundId);
        Assert.Equal(3, results.Count);
        Assert.Equal(RefundMatchFailure.MissingOrder, results["missing-order"].Reason);
        Assert.Equal(RefundMatchFailure.MissingProduct, results["missing-product"].Reason);
        Assert.Equal(RefundMatchFailure.AmbiguousProduct, results["multiple"].Reason);
    }

    [Fact]
    public void RecheckingAfterOrderImportRemovesResolvedRefundAndDeduplicatesIds()
    {
        var refund = Refund("r1", "o1");
        var orders = new Dictionary<string, IReadOnlyList<OrderedProduct>>();
        Assert.Single(RefundMatching.FindUnmatched([refund, refund], orders));
        orders["o1"] = [new("企划", "明信片", 5, 7)];
        Assert.Empty(RefundMatching.FindUnmatched([refund, refund], orders));
    }

    [Fact]
    public void QuantityReviewIsNotMisreportedAsUnmatchedProduct()
    {
        var refund = Refund("r1", "o1");
        refund.refundAmount = 10;
        var product = new OrderedProduct("企划", "明信片", 5);
        var orders = new Dictionary<string, IReadOnlyList<OrderedProduct>> { ["o1"] = [product] };
        Assert.Equal(ProductRefundStatus.NeedsReview, Assert.Single(RefundCalculator.Calculate("o1", [product], [refund])).Status);
        Assert.Empty(RefundMatching.FindUnmatched([refund], orders));
    }
}
