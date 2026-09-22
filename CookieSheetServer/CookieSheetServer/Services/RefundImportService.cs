using CookieSheet.Refunds;
using CookieSheetServer.Data;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Services;

public sealed record RefundSaveResult(int Added, int Updated, int Unchanged, int Skipped);

public static class RefundImportService
{
    public static async Task<RefundSaveResult> SaveAsync(AppDbContext db, List<RefundRecord> records)
    {
        var skipped = records.Count(r => !RefundRules.IsSuccessful(r));
        records = records.Where(RefundRules.IsSuccessful).ToList();
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
        var ids = incoming.Select(r => r.refundId).ToArray();
        var existing = await db.refunds.Where(r => ids.Contains(r.refundId)).ToDictionaryAsync(r => r.refundId);
        // Validate the whole batch before changing tracked records.
        foreach (var r in incoming)
            if (existing.TryGetValue(r.refundId, out var old)
                && (old.orderId != r.orderId || old.productName != r.productName || old.productModel != r.productModel))
                throw new ArgumentException($"退款编号 {r.refundId} 已属于另一订单或商品。");

        int added = 0, updated = 0, unchanged = 0;
        foreach (var r in incoming)
        {
            r.importedAt = DateTimeOffset.UtcNow;
            if (!existing.TryGetValue(r.refundId, out var old))
            {
                db.refunds.Add(r);
                added++;
            }
            else if (RefundRules.ShouldReplace(old, r))
            {
                db.Entry(old).CurrentValues.SetValues(r);
                updated++;
            }
            else unchanged++;
        }
        await db.SaveChangesAsync();
        return new(added, updated, unchanged, skipped);
    }
}
