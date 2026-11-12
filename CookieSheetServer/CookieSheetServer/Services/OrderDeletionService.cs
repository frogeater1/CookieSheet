using CookieSheetServer.Data;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Services;

public sealed record OrderDeleteRequest(string OrderId, int ExpectedUserId, string ExpectedBatch,
    bool? FullGift = null, bool? SetGift = null);
public sealed record OrderDeleteResult(bool Deleted, GiftRecalculationResult Gifts);
public sealed class GiftChoiceRequiredException() : ArgumentException("该批次没有赠品选项，请选择是否计算满赠和 Set赠。");

public static class OrderDeletionService
{
    public static async Task<OrderDeleteResult> DeleteAsync(AppDbContext db, OrderDeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OrderId)) throw new ArgumentException("订单编号不能为空。");
        await using var transaction = await GiftRecalculationService.BeginImportAsync(db);
        var order = await db.order.SingleOrDefaultAsync(o => o.orderId == request.OrderId);
        if (order == null) return new(false, GiftRecalculationResult.Empty);
        if (order.userId != request.ExpectedUserId || order.tag != request.ExpectedBatch)
            throw new InvalidOperationException("订单归属或批次已变化，请刷新后重新确认删除。");
        var choice = await db.giftChoices.SingleOrDefaultAsync(c => c.Batch == order.tag);
        if (choice == null)
        {
            if (!request.FullGift.HasValue || !request.SetGift.HasValue) throw new GiftChoiceRequiredException();
            db.giftChoices.Add(new() { Batch = order.tag, FullGift = request.FullGift.Value, SetGift = request.SetGift.Value });
        }
        else if ((request.FullGift.HasValue && request.FullGift != choice.FullGift)
            || (request.SetGift.HasValue && request.SetGift != choice.SetGift))
            throw new InvalidOperationException("该批次的赠品选项已由其他操作保存，请重新删除以使用最新选项。");
        var batch = order.tag;
        // Keep independent refunds so a corrected reimport can match them again.
        db.order.Remove(order);
        await db.SaveChangesAsync();
        var gifts = await GiftRecalculationService.RecalculateAsync(db, [batch]);
        if (transaction != null) await transaction.CommitAsync();
        return new(true, gifts);
    }
}
