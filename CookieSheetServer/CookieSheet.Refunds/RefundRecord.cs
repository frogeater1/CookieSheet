namespace CookieSheet.Refunds;

// Intentionally has no order/user foreign key: the refund may arrive first.
public sealed class RefundRecord
{
    public string refundId { get; set; } = "";
    public string orderId { get; set; } = "";
    public string productName { get; set; } = "";
    public string productModel { get; set; } = "";
    public string productId { get; set; } = "";
    public string productCode { get; set; } = "";
    public string modelCode { get; set; } = "";
    public decimal productPaidAmount { get; set; }
    public decimal shippingPaidAmount { get; set; }
    public decimal refundAmount { get; set; }
    public decimal shippingRefundAmount { get; set; }
    public string status { get; set; } = "";
    public string refundType { get; set; } = "";
    public string saleStage { get; set; } = "";
    public string initiator { get; set; } = "";
    public string reason { get; set; } = "";
    public string shippingStatus { get; set; } = "";
    public DateTimeOffset? orderPaidAt { get; set; }
    public DateTimeOffset? requestedAt { get; set; }
    public DateTimeOffset? completedAt { get; set; }
    public DateTimeOffset? deadlineAt { get; set; }
    public string sourceFile { get; set; } = "";
    public int sourceRow { get; set; }
    public DateTimeOffset importedAt { get; set; }
}

public static class RefundRules
{
    public static bool IsSuccessfulStatus(string? status) => status?.Trim() == "退款成功";

    public static bool IsSuccessful(RefundRecord record) => IsSuccessfulStatus(record.status);

    public static bool IsPostageProduct(string? name) => name?.Trim() == "补邮";

    public static bool ShouldReplace(RefundRecord current, RefundRecord incoming)
    {
        // An old export must not turn a completed refund back into a pending one.
        if (IsSuccessful(current) && !IsSuccessful(incoming)) return false;
        if (current.completedAt.HasValue && !incoming.completedAt.HasValue) return false;
        var currentEvent = current.completedAt ?? current.requestedAt;
        var incomingEvent = incoming.completedAt ?? incoming.requestedAt;
        return !currentEvent.HasValue || (incomingEvent.HasValue && incomingEvent >= currentEvent);
    }

    public static List<RefundRecord> LatestById(IEnumerable<RefundRecord> records)
    {
        var result = new Dictionary<string, RefundRecord>(StringComparer.Ordinal);
        foreach (var record in records)
            if (!result.TryGetValue(record.refundId, out var previous) || ShouldReplace(previous, record))
                result[record.refundId] = record;
        return result.Values.ToList();
    }
}
