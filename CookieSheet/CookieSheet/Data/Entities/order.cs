using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using CookieSheet.Refunds;

namespace CookieSheet.Data;

public class order : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string orderId { get; set; }

    [JsonIgnore]
    public user user { get; set; }

    public int userId { get; set; }
    public string tel { get; set; }
    public DateTimeOffset orderedAt { get; set; }

    public string[] productName { get; set; }
    public string[] productModel { get; set; }

    public int[] productCount { get; set; }

    [JsonIgnore] public List<ProductRefundResult> ProductRefunds { get; set; } = [];

    public List<OrderedProduct> GetOrderedProducts()
    {
        var names = productName ?? [];
        var models = productModel ?? [];
        var counts = productCount ?? [];
        return Enumerable.Range(0, Math.Max(names.Length, Math.Max(models.Length, counts.Length)))
            .Select(i => new OrderedProduct(names.ElementAtOrDefault(i) ?? "", models.ElementAtOrDefault(i) ?? "",
                counts.ElementAtOrDefault(i)))
            .ToList();
    }

    public void ApplyRefunds(IEnumerable<RefundRecord> records) =>
        ProductRefunds = RefundCalculator.Calculate(orderId, GetOrderedProducts(), records,
            new OrderRefundSnapshot(productRefundAmount, receivableAmount, price, orderRefundStatus));

    public decimal price { get; set; }
    public decimal? receivableAmount { get; set; }
    public decimal? productRefundAmount { get; set; }
    public string orderRefundStatus { get; set; } = "";
    private bool _sended;
    public bool sended
    {
        get => _sended;
        set
        {
            if (_sended == value) return;
            _sended = value;
            PropertyChanged?.Invoke(this, new(nameof(sended)));
            PropertyChanged?.Invoke(this, new(nameof(ItemOpacity)));
            PropertyChanged?.Invoke(this, new(nameof(PostageButtonVisibility)));
        }
    }

    public string tag { get; set; }

    [JsonIgnore] public string ProductDetails
    {
        get
        {
            return string.Join("；", ProductRefunds.Select(p => p.DisplayText));
        }
    }

    public string FormattedOrderedAt => orderedAt.ToString("MM-dd HH:mm:ss");

    public string FormattedPrice => price.ToString("C");
    [JsonIgnore] public string AmountTooltip => $"实付金额：{FormattedPrice}"
        + (receivableAmount.HasValue ? $"\n应收金额：{receivableAmount.Value:C}" : "")
        + (string.IsNullOrWhiteSpace(orderRefundStatus) ? "" : $"\n订单表退款状态：{orderRefundStatus}");

    public double ItemOpacity => sended ? 0.5 : 1.0;

    public Microsoft.UI.Xaml.Visibility PostageButtonVisibility => sended ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
