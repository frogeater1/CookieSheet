using System;
using System.Collections.Generic;
using System.Linq;
using CookieSheet.Data;
using CookieSheet.Refunds;

namespace CookieSheet;

public sealed class OrderListItemViewModel
{
    public OrderListItemViewModel(order source)
    {
        OrderedAt = source.orderedAt;
        LocalOrderedAt = source.orderedAt.ToLocalTime();
        OrderId = source.orderId ?? "";
        Tag = NormalizeTag(source.tag);
        Price = source.price;
        PostageStatus = source.sended ? "已补邮" : "未补邮";
        ItemOpacity = source.ItemOpacity;

        var owner = source.user;
        UserName = string.IsNullOrWhiteSpace(owner?.cn) ? $"用户 #{source.userId}" : owner.cn;
        UserQq = string.IsNullOrWhiteSpace(owner?.qq) ? "QQ：未填写" : $"QQ：{owner.qq}";
        UserDetails = owner == null
            ? $"用户 #{source.userId}（未关联用户资料）"
            : $"{UserName}\n{UserQq}\n收件人：{owner.receiver}\n手机：{owner.tel}\n下单账号：{owner.account}";

        var names = source.productName ?? [];
        var models = source.productModel ?? [];
        var counts = source.productCount ?? [];
        var products = new List<string>();
        for (var i = 0; i < Math.Max(counts.Length, Math.Max(names.Length, models.Length)); i++)
        {
            var name = i < names.Length ? names[i] : "";
            var model = i < models.Length ? models[i] : "";
            var description = string.Join(" / ", new[] { name, model }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            if (description.Length == 0) description = "未命名商品";
            products.Add(i < counts.Length ? $"{description} × {counts[i]}" : description);
        }

        ProductDetails = products.Count == 0 ? "暂无商品信息" : string.Join("；", products);
        ProductRefunds = source.ProductRefunds;
        SearchText = string.Join("\n", OrderId, UserName, owner?.qq, owner?.receiver,
            owner?.account, owner?.tel, source.tel, ProductDetails);
    }

    public DateTimeOffset OrderedAt { get; }
    public DateTimeOffset LocalOrderedAt { get; }
    public string OrderedAtText => LocalOrderedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string ShortOrderedAtText => LocalOrderedAt.ToString("MM-dd HH:mm:ss");
    public string OrderId { get; }
    public string UserName { get; }
    public string UserQq { get; }
    public string UserDetails { get; }
    public string ProductDetails { get; }
    public List<ProductRefundResult> ProductRefunds { get; }
    public decimal Price { get; }
    public string PriceText => $"¥{Price:N2}";
    public string Tag { get; }
    public string TagText => Tag.Length == 0 ? "无标签" : Tag;
    public string PostageStatus { get; }
    public double ItemOpacity { get; }
    public string SearchText { get; }

    public static string NormalizeTag(string? tag) => string.IsNullOrWhiteSpace(tag) ? "" : tag;
}
