using CookieSheet.Refunds;
using CookieSheetServer.Data;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Services;

public sealed record RefundSaveResult(int Added, int Updated, int Unchanged, int Skipped, int DeletedSkipped = 0,
    GiftRecalculationResult? Gifts = null);
public sealed record RefundDeleteRequest(List<string> RefundIds);
public sealed record RefundImportRequest(List<RefundRecord> Records, List<BatchGiftChoice> GiftChoices);

public static class RefundImportService
{
    public static Task<int> DeleteAsync(AppDbContext db, string refundId) => DeleteBatchAsync(db, [refundId]);

    public static async Task<int> DeleteBatchAsync(AppDbContext db, IReadOnlyList<string> refundIds)
    {
        if (refundIds == null || refundIds.Count == 0 || refundIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("请选择有效的退款编号。");
        var ids = refundIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var transaction = await GiftRecalculationService.BeginImportAsync(db);
        var query = db.refunds.Where(r => ids.Contains(r.refundId));
        var orderIds = await query.Select(r => r.orderId).Distinct().ToArrayAsync();
        // One SQL statement makes a batch atomic and repeated deletions idempotent.
        int deleted;
        if (db.Database.IsRelational())
            deleted = await query.ExecuteUpdateAsync(update => update.SetProperty(r => r.isDeleted, true));
        else
        {
            var records = await query.ToListAsync();
            foreach (var record in records) record.isDeleted = true;
            await db.SaveChangesAsync();
            deleted = records.Count;
        }
        var batches = await db.order.Where(o => orderIds.Contains(o.orderId)).Select(o => o.tag).Distinct().ToArrayAsync();
        await GiftRecalculationService.RecalculateAsync(db, batches);
        if (transaction != null) await transaction.CommitAsync();
        return deleted;
    }

    public static async Task<RefundSaveResult> SaveAsync(AppDbContext db, List<RefundRecord> records,
        List<BatchGiftChoice>? giftChoices = null)
    {
        await using var transaction = await GiftRecalculationService.BeginImportAsync(db);
        var skipped = records.Count(r => !RefundRules.IsSuccessfulStatus(r.status));
        records = records.Where(r => RefundRules.IsSuccessfulStatus(r.status)).ToList();
        var ids = records.Select(r => r.refundId).ToArray();
        var existing = await db.refunds.IgnoreQueryFilters().Where(r => ids.Contains(r.refundId)).ToDictionaryAsync(r => r.refundId);
        var deletedIds = existing.Values.Where(r => r.isDeleted).Select(r => r.refundId).ToHashSet(StringComparer.Ordinal);
        var deletedSkipped = records.Where(r => deletedIds.Contains(r.refundId)).Select(r => r.refundId).Distinct().Count();
        // Deleted IDs are ignored even if a later export changes the order/product identity.
        records = records.Where(r => !deletedIds.Contains(r.refundId)).ToList();
        foreach (var r in records)
        {
            // Import payloads cannot set or clear deletion state.
            r.isDeleted = false;
        }
        foreach (var r in records)
        {
            r.status = r.status.Trim();
            if (string.IsNullOrWhiteSpace(r.refundId) || string.IsNullOrWhiteSpace(r.orderId)
                || string.IsNullOrWhiteSpace(r.productName) || string.IsNullOrWhiteSpace(r.status)
                || r.refundAmount < 0 || r.productPaidAmount < 0 || r.shippingRefundAmount < 0 || r.shippingPaidAmount < 0)
                throw new ArgumentException("退款记录缺少编号、商品、状态，或包含负金额。");
        }
        foreach (var g in records.GroupBy(r => r.refundId))
            if (g.Select(r => (r.orderId, r.productName, r.productModel)).Distinct().Count() != 1)
                throw new ArgumentException($"退款编号 {g.Key} 对应多个订单或商品。");

        var incoming = RefundRules.LatestById(records);
        // Validate the whole batch before changing tracked records.
        foreach (var r in incoming)
            if (existing.TryGetValue(r.refundId, out var old)
                && (old.orderId != r.orderId || old.productName != r.productName || old.productModel != r.productModel))
                throw new ArgumentException($"退款编号 {r.refundId} 已属于另一订单或商品。");

        int added = 0, updated = 0;
        foreach (var r in incoming)
        {
            r.importedAt = DateTimeOffset.UtcNow;
            if (!existing.TryGetValue(r.refundId, out var old))
            {
                db.refunds.Add(r);
                added++;
            }
            else
            {
                // Across files, the later successful import wins, regardless of event timestamps.
                // LatestById above only resolves duplicate rows within this incoming file.
                db.Entry(old).CurrentValues.SetValues(r);
                updated++;
            }
        }
        var affectedIds = incoming.Select(r => r.orderId).Distinct().ToArray();
        var batches = await db.order.Where(o => affectedIds.Contains(o.orderId)).Select(o => o.tag).Distinct().ToArrayAsync();
        if (giftChoices != null) await GiftRecalculationService.SaveChoicesAsync(db, batches, giftChoices);
        await db.SaveChangesAsync();
        var gifts = await GiftRecalculationService.RecalculateAsync(db, batches);
        if (transaction != null) await transaction.CommitAsync();
        return new(added, updated, 0, skipped, deletedSkipped, gifts.Orders > 0 ? gifts : null);
    }
}
