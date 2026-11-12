using CookieSheet.Refunds;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CookieSheetServer.Services;

public sealed record GiftRecalculationResult(int Batches, int Orders, int PendingSetGroups)
{
    public static GiftRecalculationResult Empty => new(0, 0, 0);
}

public static class GiftRecalculationService
{
    public static async Task SaveChoicesAsync(AppDbContext db, IEnumerable<string> batches, List<BatchGiftChoice> choices)
    {
        var tags = batches.ToHashSet(StringComparer.Ordinal);
        if (choices.Any(c => c == null || c.Batch == null)
            || choices.Select(c => c.Batch).Distinct(StringComparer.Ordinal).Count() != choices.Count
            || tags.Except(choices.Select(c => c.Batch), StringComparer.Ordinal).Any())
            throw new ArgumentException("相关批次已变化或赠品选择不完整，请重新导入并逐批确认是否计算赠品。");
        // Ignore stale extra batches (e.g. refunds deleted while the dialog was open).
        foreach (var choice in choices.Where(c => tags.Contains(c.Batch)))
        {
            var previous = await db.giftChoices.FindAsync(choice.Batch);
            if (previous == null) db.giftChoices.Add(choice);
            else { previous.FullGift = choice.FullGift; previous.SetGift = choice.SetGift; }
        }
    }

    public static async Task<IDbContextTransaction?> BeginImportAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational()) return null;
        var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // Serialize imports and deletes so two recalculations cannot save stale gifts.
            // Lock order matches backup/restore. Ordinary readers remain unblocked.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"user\", \"order\", refunds, \"giftChoices\" IN SHARE ROW EXCLUSIVE MODE");
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public static async Task<GiftRecalculationResult> RecalculateAsync(AppDbContext db, IEnumerable<string> batches)
    {
        var tags = batches.Distinct(StringComparer.Ordinal).ToArray();
        if (tags.Length == 0) return GiftRecalculationResult.Empty;
        // Historical batches have no recorded consent: leave them untouched until a manual import choice.
        var choices = await db.giftChoices.Where(c => tags.Contains(c.Batch)).ToDictionaryAsync(c => c.Batch);
        tags = tags.Where(choices.ContainsKey).ToArray();
        var orders = await db.order.Where(o => tags.Contains(o.tag)).ToListAsync();
        var ids = orders.Select(o => o.orderId).ToArray();
        var refunds = (await db.refunds.Where(r => ids.Contains(r.orderId)).ToListAsync()).ToLookup(r => r.orderId);
        var snapshots = orders.Select(o => new GiftOrderSnapshot(o.orderId, o.userId, o.tag, o.orderedAt, o.price,
            RefundCalculator.Calculate(o.orderId, Products(o), refunds[o.orderId],
                new(o.productRefundAmount, o.receivableAmount, o.price, o.orderRefundStatus)))).ToList();
        var allocations = snapshots.GroupBy(o => o.Batch).SelectMany(group =>
            GiftCalculator.Calculate(group, choices[group.Key].FullGift, choices[group.Key].SetGift)).ToList();
        var byId = orders.ToDictionary(o => o.orderId, StringComparer.Ordinal);
        // Rebuild only generated gift rows. Original merchandise, postage and amounts stay intact.
        foreach (var order in orders) RemoveGifts(order);
        foreach (var gift in allocations)
        {
            var target = byId[gift.TargetOrderId];
            if (gift.FullGiftCount > 0) AddGift(target, "99满赠", gift.FullGiftCount);
            if (gift.SetGiftCount > 0) AddGift(target, "Set赠", gift.SetGiftCount.Value);
        }
        await db.SaveChangesAsync();
        return new(tags.Length, orders.Count, allocations.Count(g => !g.SetGiftCount.HasValue));
    }

    private static List<OrderedProduct> Products(order o) => Enumerable.Range(0, o.productName.Length)
        .Select(i => new OrderedProduct(o.productName[i], o.productModel.ElementAtOrDefault(i) ?? o.productName[i],
            o.productCount.ElementAtOrDefault(i))).Where(GiftCalculator.IsRegular).ToList();

    private static void RemoveGifts(order o)
    {
        var indexes = Enumerable.Range(0, o.productName.Length).Where(i =>
            !SoldProductsQuery.IsGift(o.productName[i].Trim(), o.productModel.ElementAtOrDefault(i)?.Trim() ?? "")).ToArray();
        if (indexes.Length == o.productName.Length) return;
        var names = o.productName;
        o.productName = indexes.Select(i => names[i]).ToArray();
        o.productModel = indexes.Select(i => o.productModel.ElementAtOrDefault(i) ?? names[i]).ToArray();
        o.productCount = indexes.Select(i => o.productCount.ElementAtOrDefault(i)).ToArray();
    }

    private static void AddGift(order o, string name, int quantity)
    {
        o.productName = [..o.productName, name];
        o.productModel = [..o.productModel, name];
        o.productCount = [..o.productCount, quantity];
    }
}
