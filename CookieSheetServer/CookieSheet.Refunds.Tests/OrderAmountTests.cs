using Xunit;

namespace CookieSheet.Refunds.Tests;

public class OrderAmountTests
{
    [Theory]
    [InlineData("退款完成(全额退款)", OrderAmountRefundStatus.Full)]
    [InlineData(" 退款完成（全额退款） ", OrderAmountRefundStatus.Full)]
    [InlineData("退款完成(部分退款)", OrderAmountRefundStatus.Partial)]
    [InlineData("退款完成（部分退款）", OrderAmountRefundStatus.Partial)]
    [InlineData("退款关闭", OrderAmountRefundStatus.None)]
    [InlineData("退款中", OrderAmountRefundStatus.None)]
    [InlineData("", OrderAmountRefundStatus.None)]
    [InlineData(null, OrderAmountRefundStatus.None)]
    public void ColorsComeFromOrderSheetRefundStatus(string? status, OrderAmountRefundStatus expected) =>
        Assert.Equal(expected, OrderAmountRules.Classify(status));

    [Theory]
    [InlineData("", "21", OrderAmountRefundStatus.None)]
    [InlineData("退款关闭", "21", OrderAmountRefundStatus.None)]
    [InlineData("退款完成(部分退款)", "7", OrderAmountRefundStatus.Partial)]
    [InlineData("退款完成(全额退款)", "0", OrderAmountRefundStatus.Full)]
    public void FixedOrderHeadersProvidePaidReceivableAndRefundStatusWithoutUnitPricesOrRefundTable(
        string status, string paid, OrderAmountRefundStatus expected)
    {
        using var file = RefundTests.Spreadsheet([
            ["订单编号", "商品名称", "商品型号", "商品件数", "应收金额", "实付金额", "退款状态", "订单状态"],
            ["o1", "企划", "明信片", "3", "21", paid, status, "已完成"]]);
        var row = Assert.Single(OrderImportReader.Read(file, "批次.xlsx"));
        Assert.Equal(paid, row.实付金额);
        Assert.Equal(21, OrderAmountRules.ReadReceivable(row.应收金额));
        Assert.Equal(expected, OrderAmountRules.Classify(row.退款状态));
    }

    [Fact]
    public void MissingReceivableStaysUnknownAndInvalidValueIsRejected()
    {
        Assert.Null(OrderAmountRules.ReadReceivable(""));
        Assert.Equal(0.01m, OrderAmountRules.ReadReceivable("0.01"));
        Assert.Throws<FormatException>(() => OrderAmountRules.ReadReceivable("无效"));
        Assert.Throws<FormatException>(() => OrderAmountRules.ReadReceivable("-1"));
    }
}
