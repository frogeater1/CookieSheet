using System.Globalization;

namespace CookieSheet.Refunds;

public enum OrderAmountRefundStatus { None, Full, Partial }

public static class OrderAmountRules
{
    public static OrderAmountRefundStatus Classify(string? refundStatus) =>
        refundStatus?.Trim().Replace('（', '(').Replace('）', ')') switch
        {
            "退款完成(全额退款)" => OrderAmountRefundStatus.Full,
            "退款完成(部分退款)" => OrderAmountRefundStatus.Partial,
            _ => OrderAmountRefundStatus.None
        };

    public static decimal? ReadReceivable(string? value) => ReadAmount(value, "应收金额");

    public static decimal? ReadProductRefund(string? value) => ReadAmount(value, "商品已退款金额");

    private static decimal? ReadAmount(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0)
            return amount;
        throw new FormatException($"订单{field}无效：{value}");
    }
}
