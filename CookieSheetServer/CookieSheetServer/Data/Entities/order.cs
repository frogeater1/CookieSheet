using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace CookieSheetServer.Data.Entities;

public class order
{
    [Key] public string orderId { get; set; }

    [JsonIgnore]
    [ForeignKey("userId")] public user user { get; set; }

    public int userId { get; set; }
    
    public string tel { get; set; }
    
    public DateTimeOffset orderedAt { get; set; }
    
    public string[] productName { get; set; }
    
    public string[] productModel { get; set; }
    
    public int[] productCount { get; set; }

    public decimal price { get; set; }
    public decimal? receivableAmount { get; set; }
    public decimal? productRefundAmount { get; set; }
    public string orderRefundStatus { get; set; } = "";
    
    public bool sended { get; set; }

    public string tag { get; set; }

    // [NotMapped]
    // public string ProductDetails
    // {
    //     get
    //     {
    //         var details = new System.Collections.Generic.List<string>();
    //         var length = Math.Min(productName?.Length ?? 0, Math.Min(productModel?.Length ?? 0, productCount?.Length ?? 0));
    //         for (var i = 0; i < length; i++)
    //         {
    //             var name = string.IsNullOrEmpty(productModel[i]) ? productName[i] : productModel[i];
    //             details.Add($"{name} x{productCount[i]}");
    //         }
    //
    //         return string.Join("; ", details);
    //     }
    // }
}
