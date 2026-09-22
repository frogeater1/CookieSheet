namespace CookieSheet.Refunds;

public enum RefundMatchFailure { MissingOrder, MissingProduct, AmbiguousProduct }

public sealed class UnmatchedRefund(RefundRecord refund, RefundMatchFailure reason)
{
    public RefundRecord Refund { get; } = refund;
    public RefundMatchFailure Reason { get; } = reason;
    public string ReasonText => Reason switch
    {
        RefundMatchFailure.MissingOrder => "未找到原订单",
        RefundMatchFailure.MissingProduct => "未找到对应商品型号",
        _ => "匹配到多个商品，待核对"
    };
}

public static class RefundMatching
{
    public static List<UnmatchedRefund> FindUnmatched(IEnumerable<RefundRecord> refunds,
        IReadOnlyDictionary<string, IReadOnlyList<OrderedProduct>> orders)
    {
        var result = new List<UnmatchedRefund>();
        foreach (var refund in RefundRules.LatestById(refunds.Where(r =>
                     RefundRules.IsSuccessful(r) && !RefundRules.IsPostageProduct(r.productName, r.productModel))))
        {
            if (!orders.TryGetValue(refund.orderId, out var products))
            {
                result.Add(new(refund, RefundMatchFailure.MissingOrder));
                continue;
            }
            var matches = products.Count(p => RefundCalculator.Matches(p, refund));
            if (matches != 1)
                result.Add(new(refund, matches == 0 ? RefundMatchFailure.MissingProduct : RefundMatchFailure.AmbiguousProduct));
        }
        return result;
    }
}
