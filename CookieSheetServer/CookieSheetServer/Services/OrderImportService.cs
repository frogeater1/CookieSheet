using CookieSheetServer.Data;
using CookieSheet.Refunds;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Services;

public sealed class OrderImportRequest
{
    public string Batch { get; set; } = "";
    public List<order> Orders { get; set; } = [];
    public List<user> NewUsers { get; set; } = [];
    public Dictionary<string, int> NewUserAssignments { get; set; } = [];
    public List<string> PostageOrderIds { get; set; } = [];
    public List<string> ClosedOrderIds { get; set; } = [];
    public List<BatchGiftChoice>? GiftChoices { get; set; }
}

public static class OrderImportService
{
    public static async Task<GiftRecalculationResult> SaveAsync(AppDbContext db, OrderImportRequest request)
    {
        await using var transaction = await GiftRecalculationService.BeginImportAsync(db);
        var ids = request.Orders.Select(o => o.orderId).ToArray();
        var closedIds = request.ClosedOrderIds.Distinct(StringComparer.Ordinal).ToArray();
        if (closedIds.Any(string.IsNullOrWhiteSpace) || closedIds.Intersect(ids).Any())
            throw new ArgumentException("关闭订单编号无效，或同一订单同时被标记为关闭与正常。");
        var affectedIds = ids.Concat(closedIds).ToArray();
        var existingOrders = await db.order.Where(o => affectedIds.Contains(o.orderId)).ToListAsync();
        var affectedBatches = existingOrders.Select(o => o.tag).ToHashSet(StringComparer.Ordinal);
        if (request.Orders.Count > 0) affectedBatches.Add(request.Batch);
        if (request.NewUserAssignments.Values.Any(i => i < 0 || i >= request.NewUsers.Count)
            || request.NewUsers.Any(u => u.userId != 0))
            throw new ArgumentException("新用户与订单的关联无效。");

        var byId = existingOrders.ToDictionary(o => o.orderId);
        db.order.RemoveRange(existingOrders.Where(o => closedIds.Contains(o.orderId)));
        foreach (var incoming in request.Orders)
        {
            if (!byId.TryGetValue(incoming.orderId, out var existing))
            {
                incoming.orderedAt = incoming.orderedAt.ToUniversalTime();
                incoming.tag = request.Batch;
                if (request.NewUserAssignments.TryGetValue(incoming.orderId, out var index))
                {
                    incoming.user = request.NewUsers[index];
                    incoming.userId = 0;
                }
                db.order.Add(incoming);
                byId.Add(incoming.orderId, incoming);
            }
            else
            {
                // The most recently imported order sheet determines the batch and amounts.
                // Preserve postage, ownership and original quantities; gifts are recalculated below.
                existing.tag = request.Batch;
                existing.price = incoming.price;
                existing.receivableAmount = incoming.receivableAmount;
                existing.productRefundAmount = incoming.productRefundAmount;
                existing.orderRefundStatus = incoming.orderRefundStatus;
            }
        }
        // A reimported ordinary order keeps its postage flag, even if the file also includes postage rows.
        var postageIds = request.PostageOrderIds.Except(affectedIds).ToArray();
        foreach (var existing in await db.order.Where(o => postageIds.Contains(o.orderId)).ToListAsync())
        {
            existing.sended = true;
            affectedBatches.Add(existing.tag);
        }
        // The outer transaction also includes gift recalculation after generated user IDs exist.
        if (request.GiftChoices != null)
            await GiftRecalculationService.SaveChoicesAsync(db, affectedBatches, request.GiftChoices);
        await db.SaveChangesAsync();
        var gifts = await GiftRecalculationService.RecalculateAsync(db, affectedBatches);
        if (transaction != null) await transaction.CommitAsync();
        return gifts;
    }
}
