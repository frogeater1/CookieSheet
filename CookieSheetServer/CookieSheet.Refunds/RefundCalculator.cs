namespace CookieSheet.Refunds;

public sealed class OrderedProduct(string name, string model, int quantity, decimal unitPrice = 0, string productId = "")
{
    public string Name { get; } = name;
    public string Model { get; } = model;
    public int Quantity { get; } = quantity;
    public decimal UnitPrice { get; } = unitPrice;
    public string ProductId { get; } = productId;
}
public enum ProductRefundStatus { None, Partial, Full, NeedsReview }

public sealed class ProductRefundResult(OrderedProduct product, ProductRefundStatus status,
    int? refundedQuantity, decimal refundedAmount, string explanation)
{
    public OrderedProduct Product { get; } = product;
    public ProductRefundStatus Status { get; } = status;
    public int? RefundedQuantity { get; } = refundedQuantity;
    public decimal RefundedAmount { get; } = refundedAmount;
    public string Explanation { get; } = explanation;
    public string Name => string.IsNullOrWhiteSpace(Product.Model) ? Product.Name : Product.Model;
    public string Annotation => Status switch
    {
        ProductRefundStatus.Full => $"（已退 {RefundedQuantity}/{Product.Quantity}）",
        ProductRefundStatus.Partial => $"（已退 {RefundedQuantity}/{Product.Quantity}，剩 {Product.Quantity - RefundedQuantity}）",
        ProductRefundStatus.NeedsReview => $"（退款 ¥{RefundedAmount:0.##}，数量待核对）",
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
        (Normalize(product.ProductId).Length > 0 && Normalize(refund.productId).Length > 0
            ? Normalize(product.ProductId) == Normalize(refund.productId)
            : Normalize(product.Name) == Normalize(refund.productName))
        && Model(product.Name, product.Model) == Model(refund.productName, refund.productModel);

    public static List<ProductRefundResult> Calculate(string orderId, IReadOnlyList<OrderedProduct> products,
        IEnumerable<RefundRecord> refunds)
    {
        var relevant = RefundRules.LatestById(refunds.Where(r => r.orderId == orderId));
        return products.Select(product => CalculateProduct(product, products, relevant)).ToList();
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
        if (product.Quantity <= 0) return Review("原订单缺少有效商品数量");
        if (successful.Any(r => products.Count(p => Matches(p, r)) != 1))
            return Review("同一商品型号在原订单中有多行，无法唯一分配退款数量");

        decimal unitPrice = product.UnitPrice;
        var paidAmounts = matched.Where(r => r.productPaidAmount > 0).Select(r => r.productPaidAmount).Distinct().ToList();
        if (paidAmounts.Count > 1) return Review("同一商品的实付金额不一致");
        if (unitPrice <= 0)
        {
            if (paidAmounts.Count == 0) return Review("缺少商品单价和商品实付金额");
            unitPrice = paidAmounts[0] / product.Quantity;
        }
        else if (paidAmounts.Count == 1 && Math.Abs(unitPrice * product.Quantity - paidAmounts[0]) > 0.005m)
            return Review("原订单单价与退款表商品实付金额不一致");

        if (unitPrice <= 0) return Review("商品单价必须大于零");
        var quantity = decimal.Round(amount / unitPrice, 0, MidpointRounding.AwayFromZero);
        if (amount < 0 || amount - unitPrice * product.Quantity > 0.005m || quantity > product.Quantity)
            return Review("累计成功退款金额超过原商品实付金额");
        // Only accept an integer quantity whose price reproduces the refund to the cent.
        if (quantity < 1 || Math.Abs(amount - unitPrice * quantity) > 0.005m)
            return Review("退款金额不能按商品单价换算为整件数量");

        return new(product, quantity == product.Quantity ? ProductRefundStatus.Full : ProductRefundStatus.Partial,
            (int)quantity, amount, $"成功退款 ¥{amount:0.##} ÷ 单价 ¥{unitPrice:0.####}；退款编号：{string.Join("、", successful.Select(r => r.refundId))}");
    }
}
