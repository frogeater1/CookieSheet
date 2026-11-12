using System;
using System.ComponentModel;
using CookieSheet.Refunds;

namespace CookieSheet;

public sealed class UnmatchedRefundViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public UnmatchedRefundViewModel(UnmatchedRefund item)
    {
        var refund = item.Refund;
        RefundedAt = refund.completedAt ?? refund.requestedAt;
        OrderId = refund.orderId;
        RefundId = refund.refundId;
        ProductName = refund.productName;
        ProductModel = string.IsNullOrWhiteSpace(refund.productModel) ? refund.productName : refund.productModel;
        Amount = refund.refundAmount;
        Reason = item.ReasonText;
        SearchText = string.Join("\n", OrderId, RefundId, ProductName, ProductModel, Reason);
    }

    public DateTimeOffset? RefundedAt { get; }
    public string TimeText => RefundedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未提供时间";
    public string FullTimeText => RefundedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未提供时间";
    public string OrderId { get; }
    public string RefundId { get; }
    public string ProductName { get; }
    public string ProductModel { get; }
    public decimal Amount { get; }
    public string AmountText => $"¥{Amount:N2}";
    public string Reason { get; }
    public string SearchText { get; }
}
