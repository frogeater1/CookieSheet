using Xunit;

namespace CookieSheet.Refunds.Tests;

public class SellerRemarkQqTests
{
    [Theory]
    [InlineData("qq:123456")]
    [InlineData("QQ:123456")]
    [InlineData("Qq：123456")]
    [InlineData("qQ：123456")]
    [InlineData("改为qq : 123456，谢谢")]
    [InlineData("备注\nQQ： 123456\n请核对")]
    public void SellerRemarkOverridesTemplateForAllSupportedSpellings(string remark)
    {
        Assert.Equal("123456", OrderImportReader.ResolveQq(new() { 卖家备注 = remark }, "999999"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("请尽快发货")]
    [InlineData("qq:没有号码")]
    [InlineData("qq:")]
    [InlineData("qq:123abc")]
    [InlineData("notqq:123456")]
    [InlineData("手机号：13800138000；订单号：842123456789；微信：wx123456")]
    [InlineData("QQ群：123456；QQ邮箱：123456@qq.com")]
    [InlineData("QQ:123456@qq.com")]
    [InlineData("qq:\n13800138000")]
    public void MissingOrInvalidRemarkNumberFallsBackToTemplate(string remark)
    {
        Assert.Equal("999999", OrderImportReader.ResolveQq(new() { 卖家备注 = remark }, "999999"));
        Assert.Equal("", OrderImportReader.ResolveQq(new() { 卖家备注 = remark }, ""));
    }

    [Fact]
    public void PreservesIdentifierZerosAndAllowsRepeatedIdenticalNumber()
    {
        Assert.Equal("00123456", OrderImportReader.ResolveQq(new() { 卖家备注 = "qq:00123456；QQ：00123456" }, ""));
    }

    [Fact]
    public void ConflictingRemarkNumbersAreRejectedInsteadOfChoosingWrongOwner()
    {
        Assert.Contains("o1", Assert.Throws<FormatException>(() => OrderImportReader.ResolveQq(
            new() { 订单编号 = "o1", 卖家备注 = "旧qq:123456 新QQ：654321" }, "111111")).Message);
    }

    [Fact]
    public void ExtractsOnlyExplicitQqAmongUnrelatedNotesAndNumbers()
    {
        var row = new ImportRecord { 卖家备注 = "订单号842123456789；收件人小明；电话13800138000；QQ群：777777；QQ邮箱：888888@qq.com；改正qQ ： 00123456，地址上海市12号，赠品2份。" };
        Assert.Equal("00123456", OrderImportReader.ResolveQq(row, "999999"));
    }

    [Fact]
    public void OrderSheetReadsSellerRemarkAndUsesItsQq()
    {
        using var file = RefundTests.Spreadsheet([
            ["订单编号", "订单状态", "商品名称", "商品件数", "实付金额", "下单模板信息", "卖家备注"],
            ["o1", "已完成", "商品", "1", "99", "QQ:999999", "已确认 QQ：123456"]]);
        var row = Assert.Single(OrderImportReader.Read(file, "订单.xlsx"));
        Assert.Equal("已确认 QQ：123456", row.卖家备注);
        Assert.Equal("123456", OrderImportReader.ResolveQq(row, "999999"));
    }
}
