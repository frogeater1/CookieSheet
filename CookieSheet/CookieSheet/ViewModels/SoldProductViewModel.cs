using CookieSheet.Refunds;
using System.Collections.Generic;
using System.Linq;

namespace CookieSheet;

public sealed class SoldProductViewModel(SoldProductSummary source)
{
    public string TagText => source.Tag.Length == 0 ? "无标签" : source.Tag;
    public string Name => source.Name.Length == 0 ? "未命名商品" : source.Name;
    public string Model => source.Model.Length == 0 ? "未填写" : source.Model;
    public string NetText => source.NetText;
    public string NetTooltip => source.NetText
        + (source.AwaitingDetailsCount > 0 ? "；待导入退款明细的记录先按原件数计入，补齐明细后重新扣减。" : "")
        + (source.InvalidDetailsCount > 0 ? $"；其中 {source.InvalidDetailsCount} 条商品记录退款明细有误，已按未退款保留原件数。" : "");
}

public sealed class SoldProductGroup(string tag, IEnumerable<SoldProductSummary> products)
{
    public string Label => tag.Length == 0 ? "批次：无标签" : $"批次：{tag}";
    public List<SoldProductViewModel> Items { get; } = products.Select(p => new SoldProductViewModel(p)).ToList();
    public string CountText => $"{Items.Count:N0} 项商品";
}
