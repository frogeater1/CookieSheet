using Xunit;

namespace CookieSheet.Refunds.Tests;

public class PostageRecognitionTests
{
    [Theory]
    [InlineData("补邮", "任意型号", true)]
    [InlineData(" 补邮 ", null, true)]
    [InlineData("补邮或者补差价", "非偏远地区补邮", true)]
    [InlineData("其他名称", " 非偏远地区补邮 ", true)]
    [InlineData(null, "非偏远地区补邮", true)]
    [InlineData("补邮或者补差价", "补差价", false)]
    [InlineData("补邮或者补差价", "偏远地区补邮", false)]
    [InlineData("补邮或者补差价", "非偏远地区补邮说明", false)]
    [InlineData("非偏远地区补邮", "", false)]
    [InlineData("企划", "明信片", false)]
    [InlineData(null, null, false)]
    public void RecognizesExactPostageModelOrExistingPostageName(string? name, string? model, bool expected)
    {
        Assert.Equal(expected, RefundRules.IsPostageProduct(name, model));
    }

    [Fact]
    public void PostageModelDoesNotMatchRefundsOrEnterUnmatchedList()
    {
        var product = new OrderedProduct("补邮或者补差价", "非偏远地区补邮", 1);
        var refund = new RefundRecord
        {
            refundId = "postage-refund", orderId = "o1", productName = product.Name,
            productModel = product.Model,
            status = "退款成功", refundAmount = 8, productPaidAmount = 8
        };

        Assert.False(RefundCalculator.Matches(product, refund));
        var result = Assert.Single(RefundCalculator.Calculate("o1", [product], [refund]));
        Assert.Equal(ProductRefundStatus.None, result.Status);
        Assert.Equal(0, result.RefundedQuantity);
        Assert.Empty(RefundMatching.FindUnmatched([refund], new Dictionary<string, IReadOnlyList<OrderedProduct>>()));
        Assert.Empty(RefundMatching.FindUnmatched([refund],
            new Dictionary<string, IReadOnlyList<OrderedProduct>> { ["o1"] = [product] }));
    }

    [Fact]
    public void SalesExcludePostageModelButRetainOtherModelsOfSameProduct()
    {
        var products = new[]
        {
            new OrderedProduct("补邮或者补差价", "非偏远地区补邮", 1),
            new OrderedProduct("补邮或者补差价", "补差价", 2),
            new OrderedProduct("非偏远地区补邮", "", 1)
        };
        var result = SoldProductsQuery.Apply([new("o1", "批次", RefundCalculator.Calculate("o1", products, []))]);
        Assert.Equal(2, result.Products.Count);
        Assert.Equal(3, result.SoldQuantity);
        Assert.Contains(result.Products, p => p.Name == "补邮或者补差价" && p.Model == "补差价");
        Assert.Contains(result.Products, p => p.Name == "非偏远地区补邮");
    }
}
