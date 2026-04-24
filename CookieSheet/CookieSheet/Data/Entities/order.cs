using System;
using System.Text.Json.Serialization;

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

    public decimal price { get; set; }
    public bool sended { get; set; }

    public string tag { get; set; }

    public string ProductDetails
    {
        get
        {
            var details = new System.Collections.Generic.List<string>();
            for (var i = 0; i < Math.Min(productModel.Length, productCount.Length); i++)
            {
                var name = string.IsNullOrEmpty(productModel[i]) ? productName[i] : productModel[i];
                details.Add($"{name} x{productCount[i]}");
            }

            return string.Join("; ", details);
        }
    }

    public string FormattedOrderedAt => orderedAt.ToString("MM-dd HH:mm:ss");

    public string FormattedPrice => price.ToString("C");

    public double ItemOpacity => sended ? 0.5 : 1.0;

    public Microsoft.UI.Xaml.Visibility PostageButtonVisibility => sended ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
