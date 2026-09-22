using System;
using System.Collections.Generic;
using System.Linq;
using CookieSheet.Refunds;

namespace CookieSheet.Helpers;

public static class OrderListQuery
{
    public static List<OrderListItemViewModel> Apply(
        IEnumerable<OrderListItemViewModel> orders,
        string? keyword = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? tag = null,
        bool newestFirst = true,
        OrderRefundFilter refundFilter = OrderRefundFilter.All)
    {
        if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
            throw new ArgumentException("开始日期不能晚于结束日期。");

        var query = orders;
        var search = keyword?.Trim();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(item => item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        // Compare calendar dates in the same local time zone used by the displayed timestamps.
        if (startDate.HasValue)
            query = query.Where(item => item.LocalOrderedAt.Date >= startDate.Value.Date);
        if (endDate.HasValue)
            query = query.Where(item => item.LocalOrderedAt.Date <= endDate.Value.Date);
        if (tag != null)
            query = query.Where(item => item.Tag == tag);

        query = refundFilter switch
        {
            OrderRefundFilter.AnyRefund => query.Where(item => item.ProductRefunds.Any(p => p.Status != ProductRefundStatus.None)),
            OrderRefundFilter.Full => query.Where(item => item.ProductRefunds.Any(p => p.Status == ProductRefundStatus.Full)),
            OrderRefundFilter.Partial => query.Where(item => item.ProductRefunds.Any(p => p.Status == ProductRefundStatus.Partial)),
            OrderRefundFilter.NeedsReview => query.Where(item => item.ProductRefunds.Any(p => p.Status == ProductRefundStatus.NeedsReview)),
            OrderRefundFilter.None => query.Where(item => item.ProductRefunds.All(p => p.Status == ProductRefundStatus.None)),
            _ => query
        };

        var sorted = newestFirst
            ? query.OrderByDescending(item => item.OrderedAt)
            : query.OrderBy(item => item.OrderedAt);

        return sorted.ThenBy(item => item.OrderId, StringComparer.Ordinal).ToList();
    }
}

public enum OrderRefundFilter { All, AnyRefund, Full, Partial, NeedsReview, None }
