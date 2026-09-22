namespace CookieSheet.Refunds;

public sealed record SoldOrderSnapshot(string OrderId, string? Tag, IReadOnlyList<ProductRefundResult> Products);

public sealed record SoldProductSummary(string Tag, string Name, string Model, bool IsGift, long SoldQuantity,
    long ConfirmedRefundedQuantity, int ReviewCount, int OrderCount)
{
    public long? NetQuantity => ReviewCount == 0 ? SoldQuantity - ConfirmedRefundedQuantity : null;
}

public sealed record SoldProductsResult(IReadOnlyList<SoldProductSummary> Products)
{
    public long SoldQuantity => Products.Sum(p => p.SoldQuantity);
    public long ConfirmedRefundedQuantity => Products.Sum(p => p.ConfirmedRefundedQuantity);
    public int ReviewCount => Products.Sum(p => p.ReviewCount);
    public long? NetQuantity => ReviewCount == 0 ? SoldQuantity - ConfirmedRefundedQuantity : null;
}

public static class SoldProductsQuery
{
    public static SoldProductsResult Apply(IEnumerable<SoldOrderSnapshot> orders,
        string? keyword = null, string? tag = null, bool includeGifts = false)
    {
        var search = keyword?.Trim() ?? "";
        var lines = orders.Where(o => tag == null || NormalizeTag(o.Tag) == tag)
            .SelectMany(o => o.Products.Select(p => new
            {
                o.OrderId,
                Tag = NormalizeTag(o.Tag),
                Result = p,
                Name = p.Product.Name.Trim(),
                Model = string.IsNullOrWhiteSpace(p.Product.Model) ? p.Product.Name.Trim() : p.Product.Model.Trim()
            }))
            .Where(p => !RefundRules.IsPostageProduct(p.Name, p.Result.Product.Model))
            .Where(p => includeGifts || !IsGift(p.Name, p.Model))
            .Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Model.Contains(search, StringComparison.OrdinalIgnoreCase));

        var products = lines.GroupBy(p => (p.Tag, p.Name, p.Model))
            .Select(group => new SoldProductSummary(group.Key.Tag, group.Key.Name, group.Key.Model,
                IsGift(group.Key.Name, group.Key.Model),
                group.Sum(p => (long)p.Result.Product.Quantity),
                group.Where(p => !NeedsReview(p.Result)).Sum(p => (long)p.Result.RefundedQuantity!.Value),
                group.Count(p => NeedsReview(p.Result)),
                group.Select(p => p.OrderId).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(p => p.SoldQuantity)
            .ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Model, StringComparer.Ordinal)
            .ThenBy(p => p.Tag, StringComparer.Ordinal)
            .ToList();
        return new(products);
    }

    public static string NormalizeTag(string? tag) => string.IsNullOrWhiteSpace(tag) ? "" : tag;
    private static bool IsGift(string name, string model) => name is "99满赠" or "Set赠" || model is "99满赠" or "Set赠";
    private static bool NeedsReview(ProductRefundResult result) =>
        result.Status == ProductRefundStatus.NeedsReview || !result.RefundedQuantity.HasValue;
}
