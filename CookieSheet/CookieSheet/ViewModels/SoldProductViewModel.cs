using CookieSheet.Refunds;
using System.Collections.Generic;
using System.Linq;

namespace CookieSheet;

public sealed class SoldProductViewModel(SoldProductSummary source)
{
    public string TagText => source.Tag.Length == 0 ? "无标签" : source.Tag;
    public string Name => source.Name.Length == 0 ? "未命名商品" : source.Name;
    public string Model => source.Model.Length == 0 ? "未填写" : source.Model;
    public string NetText => source.NetQuantity?.ToString("N0") ?? "待核对";
}

public sealed class SoldProductGroup(string tag, IEnumerable<SoldProductSummary> products)
{
    public string Label => tag.Length == 0 ? "批次：无标签" : $"批次：{tag}";
    public List<SoldProductViewModel> Items { get; } = products.Select(p => new SoldProductViewModel(p)).ToList();
    public string CountText => $"{Items.Count:N0} 项商品";
}
