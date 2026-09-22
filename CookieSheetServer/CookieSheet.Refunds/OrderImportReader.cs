using System.Globalization;

namespace CookieSheet.Refunds;

public sealed class ImportRecord
{
    public string 下单模板信息 { get; init; } = "";
    public string 手机号 { get; init; } = "";
    public string 收货人 { get; init; } = "";
    public string 下单账号 { get; init; } = "";
    public string 详细地址 { get; init; } = "";
    public string 商品名称 { get; init; } = "";
    public string 商品型号 { get; init; } = "";
    public string 商品件数 { get; init; } = "";
    public string 商品ID { get; init; } = "";
    public string 商品单价 { get; init; } = "";
    public string 运费 { get; init; } = "";
    public string 实付金额 { get; init; } = "";
    public string 订单编号 { get; init; } = "";
    public string 下单时间 { get; init; } = "";
    public string 发货时间 { get; init; } = "";
    public string 退款状态 { get; init; } = "";
    public string 订单状态 { get; init; } = "";
}

public static class OrderImportReader
{
    // Preserve the order import's first-worksheet behavior for both Excel formats.
    public static IEnumerable<ImportRecord> Read(Stream stream, string sourceFile)
    {
        using var reader = ExcelImportReader.Open(stream, sourceFile);
        if (!reader.Read()) throw new FormatException("订单表中没有表头。");
        var headers = Enumerable.Range(0, reader.FieldCount)
            .Select(i => (Name: Text(reader.GetValue(i)).Trim(), Index: i))
            .Where(x => x.Name.Length > 0)
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);
        string[] required = ["订单编号", "商品名称", "商品件数", "实付金额"];
        if (!required.All(headers.ContainsKey))
            throw new FormatException("没有找到订单表表头，请选择含订单编号、商品名称、商品件数和实付金额的文件。");

        while (reader.Read())
        {
            string Get(string name) => headers.TryGetValue(name, out var index) ? Text(reader.GetValue(index)) : "";
            if (string.IsNullOrWhiteSpace(Get("订单编号"))) continue;
            yield return new ImportRecord
            {
                下单模板信息 = Get("下单模板信息"), 手机号 = Get("收货人/提货人手机号"),
                收货人 = Get("收货人/提货人姓名"), 下单账号 = Get("下单账号"),
                详细地址 = Get("收货/提货详细地址"), 商品名称 = Get("商品名称"),
                商品型号 = Get("商品型号"), 商品件数 = Get("商品件数"),
                商品ID = Get("商品ID"), 商品单价 = Get("商品单价"), 运费 = Get("运费"),
                实付金额 = Get("实付金额"), 订单编号 = Get("订单编号"),
                下单时间 = Get("下单时间"), 发货时间 = Get("发货时间"),
                退款状态 = Get("退款状态"), 订单状态 = Get("订单状态")
            };
        }
    }

    private static string Text(object? value) => value is DateTime date
        ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
