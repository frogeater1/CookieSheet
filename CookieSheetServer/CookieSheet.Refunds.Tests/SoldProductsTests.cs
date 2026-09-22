using CookieSheet.Refunds;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class SoldProductsTests
{
    private static ProductRefundResult Product(string name = "企划", string model = "明信片", int quantity = 5,
        int? refunded = 0, ProductRefundStatus status = ProductRefundStatus.None) =>
        new(new OrderedProduct(name, model, quantity, 7), status, refunded, 0, "");

    [Fact]
    public void SameBatchCombinesOrdersButDifferentBatchesAndModelsStaySeparate()
    {
        var result = SoldProductsQuery.Apply([
            new("o1", "春季", [Product(quantity: 2), Product(quantity: 3)]),
            new("o2", "春季", [Product(quantity: 4)]),
            new("o3", "秋季", [Product(quantity: 8)]),
            new("o4", "春季", [Product(model: "挂件", quantity: 1)])]);
        Assert.Equal(3, result.Products.Count);
        var spring = Assert.Single(result.Products.Where(p => p.Tag == "春季" && p.Model == "明信片"));
        Assert.Equal(9, spring.SoldQuantity);
        Assert.Equal(2, spring.OrderCount);
        Assert.Equal(8, Assert.Single(result.Products.Where(p => p.Tag == "秋季")).SoldQuantity);
        Assert.Equal(18, result.SoldQuantity);
    }

    [Fact]
    public void TrimsNamesAndModelsAndNormalizesBlankModelsAndTags()
    {
        var result = SoldProductsQuery.Apply([
            new("o1", null, [Product("  色纸  ", " ", 2)]),
            new("o2", "  ", [Product("色纸", " 色纸 ", 3)]),
            new("o3", "无标签", [Product("色纸", "色纸", 4)])]);
        Assert.Equal(2, result.Products.Count);
        var untagged = Assert.Single(SoldProductsQuery.Apply([
            new("o1", null, [Product("色纸", "", 2)]),
            new("o2", "无标签", [Product("色纸", "色纸", 4)])], tag: "").Products);
        Assert.Equal("", untagged.Tag);
        Assert.Equal(2, untagged.SoldQuantity);
        Assert.Equal(5, Assert.Single(result.Products.Where(p => p.Tag == "")).SoldQuantity);
    }

    [Fact]
    public void GiftsAreOptInAndPostageIsAlwaysExcluded()
    {
        SoldOrderSnapshot[] orders = [new("o1", "批次", [Product(), Product("99满赠", "99满赠"),
            Product("企划", " Set赠 "), Product(" 补邮 ", "补邮", 10)])];
        Assert.Single(SoldProductsQuery.Apply(orders).Products);
        var all = SoldProductsQuery.Apply(orders, includeGifts: true);
        Assert.Equal(3, all.Products.Count);
        Assert.Equal(2, all.Products.Count(p => p.IsGift));
        Assert.Equal(15, all.SoldQuantity);
    }

    [Fact]
    public void PartialAndFullRefundsUseExistingCalculatorAndKeepFullyRefundedProducts()
    {
        var products = new[] { new OrderedProduct("企划", "明信片", 5, 7), new OrderedProduct("企划", "挂件", 2, 7) };
        var refunds = new[] {
            new RefundRecord { orderId = "o1", refundId = "r1", productName = "企划", productModel = "明信片", status = "退款成功", refundAmount = 21 },
            new RefundRecord { orderId = "o1", refundId = "r2", productName = "企划", productModel = "挂件", status = "退款成功", refundAmount = 14 },
            new RefundRecord { orderId = "missing", refundId = "r3", productName = "企划", productModel = "明信片", status = "退款成功", refundAmount = 7 },
            new RefundRecord { orderId = "o1", refundId = "r4", productName = "别的商品", productModel = "明信片", status = "退款成功", refundAmount = 7 },
            new RefundRecord { orderId = "o1", refundId = "r5", productName = "企划", productModel = "明信片", status = "退款中", refundAmount = 7 }
        };
        var result = SoldProductsQuery.Apply([new("o1", "批次", RefundCalculator.Calculate("o1", products, refunds))]);
        Assert.Equal(2, result.Products.Count);
        Assert.Equal(7, result.SoldQuantity);
        Assert.Equal(5, result.ConfirmedRefundedQuantity);
        Assert.Equal(2, result.NetQuantity);
        Assert.Equal(0, Assert.Single(result.Products.Where(p => p.Model == "挂件")).NetQuantity);
        Assert.Equal(2, RefundMatching.FindUnmatched(refunds,
            new Dictionary<string, IReadOnlyList<OrderedProduct>> { ["o1"] = products }).Count);
    }

    [Fact]
    public void UnknownRefundsNeverBecomeZeroOrProduceDefiniteNetTotals()
    {
        SoldOrderSnapshot[] orders = [
            new("o1", "批次", [Product(refunded: 2, status: ProductRefundStatus.Partial)]),
            new("o2", "批次", [Product(refunded: null, status: ProductRefundStatus.NeedsReview)]),
            new("o3", "批次", [Product("正常商品", "色纸", 2)])];
        var result = SoldProductsQuery.Apply(orders);
        Assert.Equal(12, result.SoldQuantity);
        Assert.Equal(2, result.ConfirmedRefundedQuantity);
        Assert.Equal(1, result.ReviewCount);
        Assert.Null(result.NetQuantity);
        Assert.Null(Assert.Single(result.Products.Where(p => p.Name == "企划")).NetQuantity);
        Assert.Equal(2, SoldProductsQuery.Apply(orders, "正常商品").NetQuantity);
    }

    [Fact]
    public void AmbiguousDuplicateOrderLinesRemainPending()
    {
        OrderedProduct[] products = [new("企划", "明信片", 5, 7), new("企划", "明信片", 2, 7)];
        var refund = new RefundRecord { orderId = "o1", refundId = "r1", productName = "企划", productModel = "明信片", status = "退款成功", refundAmount = 7 };
        var result = SoldProductsQuery.Apply([new("o1", "批次", RefundCalculator.Calculate("o1", products, [refund]))]);
        var row = Assert.Single(result.Products);
        Assert.Equal(1, row.OrderCount);
        Assert.Equal(2, row.ReviewCount);
        Assert.Equal(0, row.ConfirmedRefundedQuantity);
        Assert.Null(result.NetQuantity);
    }

    [Fact]
    public void FiltersAndTotalsUseOnlyVisibleBatchProductsAndSortingIsStable()
    {
        SoldOrderSnapshot[] orders = [
            new("o1", "B", [Product("B", "SKU", 4), Product("A", "Z", 4), Product("A", "A", 4)]),
            new("o2", "A", [Product("B", "SKU", 4)]),
            new("o3", "B", [Product("99满赠", "99满赠", 20)])];
        var all = SoldProductsQuery.Apply(orders);
        Assert.Equal(new[] { ("A", "A", "B"), ("A", "Z", "B"), ("B", "SKU", "A"), ("B", "SKU", "B") },
            all.Products.Select(p => (p.Name, p.Model, p.Tag)));
        var filtered = SoldProductsQuery.Apply(orders, " skU ", "B");
        Assert.Single(filtered.Products);
        Assert.Equal(4, filtered.SoldQuantity);
        Assert.Equal(4, filtered.NetQuantity);
        Assert.Equal(32, SoldProductsQuery.Apply(orders, tag: "B", includeGifts: true).SoldQuantity);
        Assert.True(SoldProductsQuery.Apply(orders, includeGifts: true).Products[0].IsGift);
    }

    [Fact]
    public void EmptyInputAndUnmatchedSearchReturnZeroTotals()
    {
        foreach (var result in new[] { SoldProductsQuery.Apply([]),
            SoldProductsQuery.Apply([new("o1", "批次", [Product()])], "无结果") })
        {
            Assert.Empty(result.Products);
            Assert.Equal(0, result.SoldQuantity);
            Assert.Equal(0, result.NetQuantity);
            Assert.Equal(0, result.ReviewCount);
        }
    }

    [Fact]
    public void QuantitiesAccumulateBeyondIntegerRange()
    {
        var result = SoldProductsQuery.Apply([new("o1", "批次", [Product(quantity: int.MaxValue)]),
            new("o2", "批次", [Product(quantity: int.MaxValue)])]);
        Assert.Equal(2L * int.MaxValue, result.SoldQuantity);
    }
}
