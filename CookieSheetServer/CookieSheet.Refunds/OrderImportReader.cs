using System.Globalization;
using System.Text.RegularExpressions;

namespace CookieSheet.Refunds;

public sealed class ImportRecord
{
    public string 下单模板信息 { get; init; } = "";
    public string 卖家备注 { get; init; } = "";
    public string 手机号 { get; init; } = "";
    public string 收货人 { get; init; } = "";
    public string 下单账号 { get; init; } = "";
    public string 详细地址 { get; init; } = "";
    public string 商品名称 { get; init; } = "";
    public string 商品型号 { get; init; } = "";
    public string 商品件数 { get; init; } = "";
    public string 运费 { get; init; } = "";
    public string 实付金额 { get; init; } = "";
    public string 应收金额 { get; init; } = "";
    public string 商品已退款金额 { get; init; } = "";
    public string 订单编号 { get; init; } = "";
    public string 下单时间 { get; init; } = "";
    public string 发货时间 { get; init; } = "";
    public string 退款状态 { get; init; } = "";
    public string 订单状态 { get; init; } = "";
    public bool IsClosed => 订单状态.Trim() == "已关闭";
}

public static class OrderImportReader
{
    private static readonly Regex RemarkQq = new(@"(?<![A-Za-z0-9_])qq[^\S\r\n]*[:：][^\S\r\n]*([0-9]+)(?![A-Za-z0-9_@])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static string ResolveQq(ImportRecord record, string templateQq)
    {
        var numbers = RemarkQq.Matches(record.卖家备注).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (numbers.Length > 1)
            throw new FormatException($"订单 {record.订单编号} 的卖家备注包含多个不同 QQ 号，请只保留正确的 QQ 后重新导入。");
        return numbers.Length == 1 ? numbers[0] : templateQq;
    }

    // The last row for an order determines its status as well as its latest amounts.
    public static List<ImportRecord> LatestByOrderId(IEnumerable<ImportRecord> records) => records
        .GroupBy(r => r.订单编号, StringComparer.Ordinal).Select(g => g.Last()).ToList();

    // Preserve the order import's first-worksheet behavior for both Excel formats.
    public static IEnumerable<ImportRecord> Read(Stream stream, string sourceFile)
    {
        using var reader = ExcelImportReader.Open(stream, sourceFile);
        if (!reader.Read()) throw new FormatException("订单表中没有表头。");
        var headers = Enumerable.Range(0, reader.FieldCount)
            .Select(i => (Name: Text(reader.GetValue(i)).Trim(), Index: i))
            .Where(x => x.Name.Length > 0)
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);
        string[] required = ["订单编号", "订单状态", "商品名称", "商品件数", "实付金额"];
        if (!required.All(headers.ContainsKey))
            throw new FormatException("没有找到订单表表头，请选择含订单编号、订单状态、商品名称、商品件数和实付金额的文件。");

        while (reader.Read())
        {
            string Get(string name) => headers.TryGetValue(name, out var index) ? Text(reader.GetValue(index)) : "";
            if (string.IsNullOrWhiteSpace(Get("订单编号"))) continue;
            yield return new ImportRecord
            {
                下单模板信息 = Get("下单模板信息"), 手机号 = Get("收货人/提货人手机号"),
                卖家备注 = Get("卖家备注"),
                收货人 = Get("收货人/提货人姓名"), 下单账号 = Get("下单账号"),
                详细地址 = Get("收货/提货详细地址"), 商品名称 = Get("商品名称"),
                商品型号 = Get("商品型号"), 商品件数 = Get("商品件数"),
                运费 = Get("运费"),
                实付金额 = Get("实付金额"), 应收金额 = Get("应收金额"), 订单编号 = Get("订单编号"),
                商品已退款金额 = Get("商品已退款金额"),
                下单时间 = Get("下单时间"), 发货时间 = Get("发货时间"),
                退款状态 = Get("退款状态"), 订单状态 = Get("订单状态")
            };
        }
    }

    private static string Text(object? value) => value is DateTime date
        ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
