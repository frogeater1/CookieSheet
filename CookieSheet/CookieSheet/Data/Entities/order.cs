using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using CookieSheet.Refunds;

namespace CookieSheet.Data;

public class order
{
    public string orderId { get; set; }

    [JsonIgnore]
    public user user { get; set; }

    public int userId { get; set; }
    public string tel { get; set; }
    public DateTimeOffset orderedAt { get; set; }

    public string[] productName { get; set; }
    public string[] productModel { get; set; }

    public int[] productCount { get; set; }
    public decimal[] productUnitPrice { get; set; } = [];
    public string[] productId { get; set; } = [];

    [JsonIgnore] public List<ProductRefundResult> ProductRefunds { get; set; } = [];

    public List<OrderedProduct> GetOrderedProducts()
    {
        var names = productName ?? [];
        var models = productModel ?? [];
        var counts = productCount ?? [];
        return Enumerable.Range(0, Math.Max(names.Length, Math.Max(models.Length, counts.Length)))
            .Select(i => new OrderedProduct(names.ElementAtOrDefault(i) ?? "", models.ElementAtOrDefault(i) ?? "",
                counts.ElementAtOrDefault(i), (productUnitPrice ?? []).ElementAtOrDefault(i), (productId ?? []).ElementAtOrDefault(i) ?? ""))
            .ToList();
    }

    public void ApplyRefunds(IEnumerable<RefundRecord> records) =>
        ProductRefunds = RefundCalculator.Calculate(orderId, GetOrderedProducts(), records);

    public decimal price { get; set; }
    public bool sended { get; set; }

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

    public double ItemOpacity => sended ? 0.5 : 1.0;

    public Microsoft.UI.Xaml.Visibility PostageButtonVisibility => sended ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
