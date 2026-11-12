using CookieSheet.Refunds;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class ExcelImportTests
{
    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public void OrdersPreserveChineseIdentifiersNumbersDatesAndBlankCells(string extension)
    {
        using var stream = Open("orders", extension);
        var rows = OrderImportReader.Read(stream, $"orders.{extension.ToUpperInvariant()}").ToList();
        Assert.Equal(2, rows.Count);
        var row = rows[0];
        Assert.Equal("QQ:00123456;CN:测试", row.下单模板信息);
        Assert.Equal("", row.卖家备注);
        Assert.Equal("00123456", OrderImportReader.ResolveQq(row, "00123456"));
        Assert.Equal("13800138000", row.手机号);
        Assert.Equal("测试收件人", row.收货人);
        Assert.Equal("测试账号", row.下单账号);
        Assert.Equal("测试地址", row.详细地址);
        Assert.Equal("企划", row.商品名称);
        Assert.Equal("明信片", row.商品型号);
        Assert.Equal("5", row.商品件数);
        Assert.Equal("8", row.运费);
        Assert.Equal("43.5", row.实付金额);
        Assert.Equal("00144115526827130368", row.订单编号);
        Assert.Equal("2026-09-18 15:52:35", row.下单时间);
        Assert.Equal("", row.发货时间);
        Assert.Equal("", row.退款状态);
        Assert.Equal("待发货", row.订单状态);
        Assert.Equal("13800138001", rows[1].手机号);
        Assert.Equal("色纸;挂件", rows[1].商品名称);
        Assert.Equal("", rows[1].商品型号);
        Assert.Equal("2;3", rows[1].商品件数);
        Assert.Equal("2026-09-19 10:20:30", rows[1].下单时间);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public void RefundsPreserveValuesDeduplicateAndSkipUnsuccessfulRows(string extension)
    {
        using var stream = Open("refunds", extension);
        var result = RefundImportReader.Read(stream, $"refunds.{extension.ToUpperInvariant()}");
        var row = Assert.Single(result.Records);
        Assert.Equal(3, result.SourceRows);
        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal(1, result.SkippedRows);
        Assert.Equal("00144115526827130368", row.orderId);
        Assert.Equal("144115526827130368", row.refundId);
        Assert.Equal("企划", row.productName);
        Assert.Equal("明信片", row.productModel);
        Assert.Equal(35m, row.productPaidAmount);
        Assert.Equal(7.5m, row.refundAmount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T07:52:35Z"), row.requestedAt);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public void OrdersRejectRefundTableInsteadOfSilentlyImportingNothing(string extension)
    {
        using var stream = Open("refunds", extension);
        Assert.Contains("订单表表头", Assert.Throws<FormatException>(() =>
            OrderImportReader.Read(stream, $"refunds.{extension}").ToList()).Message);
    }

    private static FileStream Open(string name, string extension) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.{extension}"));
}
