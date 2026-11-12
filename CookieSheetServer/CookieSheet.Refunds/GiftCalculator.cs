namespace CookieSheet.Refunds;

public sealed record GiftOrderSnapshot(string OrderId, int UserId, string Batch, DateTimeOffset OrderedAt,
    decimal PaidAmount, IReadOnlyList<ProductRefundResult> Products);
public sealed record GiftAllocation(string Batch, int UserId, string TargetOrderId, int FullGiftCount, int? SetGiftCount);

public static class GiftCalculator
{
    public static bool IsRegular(OrderedProduct product) =>
        !RefundRules.IsPostageProduct(product.Name, product.Model)
        && !SoldProductsQuery.IsGift(product.Name.Trim(), product.Model.Trim());

    private static string Model(OrderedProduct product) =>
        string.IsNullOrWhiteSpace(product.Model) ? product.Name.Trim() : product.Model.Trim();

    public static List<GiftAllocation> Calculate(IEnumerable<GiftOrderSnapshot> orders, bool fullGift = true, bool setGift = true)
    {
        var allocations = new List<GiftAllocation>();
        foreach (var batch in orders.GroupBy(o => o.Batch, StringComparer.Ordinal))
        {
            // Keep the existing definition of a set: one of every model in this batch.
            // The catalogue uses original items, even when a model has been fully refunded.
            var requiredModels = batch.SelectMany(o => o.Products).Where(p => IsRegular(p.Product))
                .Select(p => Model(p.Product)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var owner in batch.GroupBy(o => o.UserId))
            {
                var relevant = owner.Where(o => o.Products.Any(p => IsRegular(p.Product))).ToList();
                if (relevant.Count == 0) continue;
                var target = relevant.OrderBy(o => o.OrderedAt).ThenBy(o => o.OrderId, StringComparer.Ordinal).Last();
                var fullCount = fullGift ? checked((int)Math.Max(0, decimal.Floor(relevant.Sum(o => o.PaidAmount) / 99m))) : 0;
                var counts = requiredModels.ToDictionary(model => model, _ => 0L, StringComparer.Ordinal);
                var pending = false;
                foreach (var item in relevant.SelectMany(o => o.Products).Where(p => setGift && IsRegular(p.Product)))
                {
                    if (!item.RefundedQuantity.HasValue || item.Product.Quantity < 0
                        || item.Status is ProductRefundStatus.NeedsReview or ProductRefundStatus.AwaitingDetails)
                    {
                        pending = true;
                        continue;
                    }
                    counts[Model(item.Product)] += Math.Max(0, item.Product.Quantity - item.RefundedQuantity.Value);
                }
                int? setCount = !setGift ? 0 : pending ? null : counts.Count == 0 ? 0 : checked((int)counts.Values.Min());
                allocations.Add(new(batch.Key, owner.Key, target.OrderId, fullCount, setCount));
            }
        }
        return allocations;
    }
}
