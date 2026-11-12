namespace CookieSheet.Refunds;

public sealed class OrderedProduct(string name, string model, int quantity)
{
    public string Name { get; } = name;
    public string Model { get; } = model;
    public int Quantity { get; } = quantity;
}
public enum ProductRefundStatus { None, Partial, Full, NeedsReview, AwaitingDetails, InvalidDetails }

public sealed record OrderRefundSnapshot(decimal? ProductRefundAmount, decimal? ReceivableAmount,
    decimal PaidAmount, string? RefundStatus);

public sealed class ProductRefundResult(OrderedProduct product, ProductRefundStatus status,
    int? refundedQuantity, decimal refundedAmount, string explanation)
{
    public OrderedProduct Product { get; } = product;
    public ProductRefundStatus Status { get; } = status;
    // Invalid details remain visible for review, but must not remove merchandise from fulfillment.
    public int? RefundedQuantity { get; } = status == ProductRefundStatus.InvalidDetails ? 0 : refundedQuantity;
    public decimal RefundedAmount { get; } = refundedAmount;
    public string Explanation { get; } = status == ProductRefundStatus.InvalidDetails
        ? explanation + "；已按未退款容错处理，保留原件数正常发货，未扣减销量。" : explanation;
    public string Name => string.IsNullOrWhiteSpace(Product.Model) ? Product.Name : Product.Model;
    public string Annotation => Status switch
    {
        ProductRefundStatus.Full => $"（已退 {RefundedQuantity}/{Product.Quantity}）",
        ProductRefundStatus.Partial => $"（已退 {RefundedQuantity}/{Product.Quantity}，剩 {Product.Quantity - RefundedQuantity}）",
        ProductRefundStatus.NeedsReview => $"（退款 ¥{RefundedAmount:0.##}，数量待核对）",
        ProductRefundStatus.AwaitingDetails => "（待导入退款明细）",
        ProductRefundStatus.InvalidDetails => "（退款明细有误，按未退款发货）",
        _ => ""
    };
    public string DisplayText => $"{Name} × {Product.Quantity}{Annotation}";
}

public static class RefundCalculator
{
    private static string Normalize(string? value) => value?.Trim() ?? "";
    private static string Model(string? name, string? model) =>
        string.IsNullOrWhiteSpace(model) ? Normalize(name) : Normalize(model);

    public static bool Matches(OrderedProduct product, RefundRecord refund) =>
        !RefundRules.IsPostageProduct(product.Name, product.Model)
        && !RefundRules.IsPostageProduct(refund.productName, refund.productModel)
        &&
        Normalize(product.Name) == Normalize(refund.productName)
        && Model(product.Name, product.Model) == Model(refund.productName, refund.productModel);

    public static List<ProductRefundResult> Calculate(string orderId, IReadOnlyList<OrderedProduct> products,
        IEnumerable<RefundRecord> refunds, OrderRefundSnapshot? snapshot = null)
    {
        var relevant = RefundRules.LatestById(refunds.Where(r => r.orderId == orderId));
        var results = products.Select(product => CalculateProduct(product, products, relevant)).ToList();
        var issue = CheckDetails(products, relevant, snapshot);
        if (issue == null) return results;
        // Missing details remain unknown. Inconsistent order totals instead retain
        // all original merchandise for fulfillment, while preserving the diagnostic status.
        return results.Select(result => result.Status == ProductRefundStatus.InvalidDetails
            || RefundRules.IsPostageProduct(result.Product.Name, result.Product.Model)
            || (SoldProductsQuery.IsGift(result.Product.Name.Trim(), result.Product.Model.Trim())
                && result.Status == ProductRefundStatus.None)
            ? result : new ProductRefundResult(result.Product, issue.Value.Status,
                null, result.RefundedAmount, issue.Value.Reason)).ToList();
    }

    private static (ProductRefundStatus Status, string Reason)? CheckDetails(IReadOnlyList<OrderedProduct> products,
        List<RefundRecord> refunds, OrderRefundSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        var successful = refunds.Where(RefundRules.IsSuccessful).ToList();
        if (successful.Any(r => r.refundAmount > 0 && !RefundRules.IsPostageProduct(r.productName, r.productModel)
            && !products.Any(p => Matches(p, r))))
            return (ProductRefundStatus.InvalidDetails, "退款明细有误：存在未匹配到商品的成功退款，请到“待匹配退款”核对。");
        static (ProductRefundStatus, string)? Compare(decimal expected, decimal actual)
        {
            if (Math.Abs(expected - actual) <= 0.005m) return null;
            return actual < expected
                ? (ProductRefundStatus.AwaitingDetails, $"待导入退款明细：订单表已退款 ¥{expected:0.##}，成功退款明细合计 ¥{actual:0.##}，明细尚未补齐。")
                : (ProductRefundStatus.InvalidDetails, $"退款明细有误：成功退款明细合计 ¥{actual:0.##} 超过订单表已退款 ¥{expected:0.##}；也可能是两张表导出时间不同，请核对并导入一致的最新数据。");
        }
        var productRefund = successful.Where(r => !RefundRules.IsPostageProduct(r.productName, r.productModel))
            .Sum(r => r.refundAmount);
        if (snapshot.ProductRefundAmount is decimal expected)
        {
            return Compare(expected, productRefund);
        }
        else if (snapshot.ReceivableAmount is decimal receivable)
        {
            // Older cached orders lack the product refund total. Compare the complete
            // refund including shipping, never mistake a shipping refund for item units.
            var total = successful.Sum(r => r.refundAmount + r.shippingRefundAmount);
            return Compare(receivable - snapshot.PaidAmount, total);
        }
        else if (OrderAmountRules.Classify(snapshot.RefundStatus) != OrderAmountRefundStatus.None)
            return (ProductRefundStatus.AwaitingDetails, "待导入退款明细：订单已退款，但缺少用于确认明细完整性的订单金额，请重新导入订单表和退款明细。");
        return null;
    }

    private static ProductRefundResult CalculateProduct(OrderedProduct product, IReadOnlyList<OrderedProduct> products,
        List<RefundRecord> refunds)
    {
        var matched = refunds.Where(r => Matches(product, r)).ToList();
        var successful = matched.Where(RefundRules.IsSuccessful).ToList();
        var amount = successful.Sum(r => r.refundAmount);
        if (successful.Count == 0 || amount == 0)
            return new(product, ProductRefundStatus.None, 0, amount, matched.Count == 0 ? "" : "尚无成功的商品退款");

        ProductRefundResult Review(string reason) => new(product, ProductRefundStatus.NeedsReview, null, amount, reason);
        ProductRefundResult Invalid(string reason) => new(product, ProductRefundStatus.InvalidDetails, null, amount, reason);
        if (product.Quantity <= 0) return Review("原订单缺少有效商品数量");
        if (successful.Any(r => products.Count(p => Matches(p, r)) != 1))
            return Review("同一商品型号在原订单中有多行，无法唯一分配退款数量");

        // This fixed order export has no per-product unit price. Historical stored prices may
        // have been inferred from a post-refund order total and must not affect quantities.
        var paidAmounts = successful.Select(r => r.productPaidAmount).Distinct().ToList();
        if (paidAmounts.Any(p => p <= 0)) return Review("退款表缺少有效的原商品实付金额");
        if (paidAmounts.Count > 1) return Invalid("退款明细有误：同一商品的原实付金额不一致");
        var originalPaid = paidAmounts[0];
        var unitPrice = originalPaid / product.Quantity;
        var quantity = decimal.Round(amount / originalPaid * product.Quantity, 0, MidpointRounding.AwayFromZero);
        if (amount < 0 || amount - originalPaid > 0.005m || quantity > product.Quantity)
            return Invalid("退款明细有误：累计成功退款金额超过原商品实付金额");
        // Only accept an integer quantity whose price reproduces the refund to the cent.
        if (quantity < 1 || Math.Abs(amount - unitPrice * quantity) > 0.005m)
            return Review("退款金额不能按商品单价换算为整件数量");

        return new(product, quantity == product.Quantity ? ProductRefundStatus.Full : ProductRefundStatus.Partial,
            (int)quantity, amount, $"成功商品退款 ¥{amount:0.##} × 原购 {product.Quantity} 件 ÷ 原商品实付 ¥{originalPaid:0.##}；退款编号：{string.Join("、", successful.Select(r => r.refundId))}");
    }
}
