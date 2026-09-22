using System.Globalization;

namespace CookieSheet.Refunds;

public sealed record RefundImportResult(List<RefundRecord> Records, int SourceRows, int DuplicateRows, int SkippedRows);

public static class RefundImportReader
{
    public static RefundImportResult Read(Stream stream, string sourceFile)
    {
        using var reader = ExcelImportReader.Open(stream, sourceFile);
        var records = new List<RefundRecord>();
        int sourceRows = 0, skippedRows = 0;
        var foundSheet = false;
        do
        {
            if (!reader.Read()) continue;
            var headers = Enumerable.Range(0, reader.FieldCount)
                .Select(i => (Name: Text(reader.GetValue(i)), Index: i)).Where(x => x.Name.Length > 0)
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);
            string[] required = ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态"];
            if (!required.All(headers.ContainsKey)) continue;
            foundSheet = true;
            var row = 1;
            while (reader.Read())
            {
                row++;
                object? Value(string key) => headers.TryGetValue(key, out var i) ? reader.GetValue(i) : null;
                string Get(string key) => Text(Value(key));
                if (Enumerable.Range(0, reader.FieldCount).All(i => string.IsNullOrWhiteSpace(Text(reader.GetValue(i))))) continue;
                sourceRows++;
                if (!RefundRules.IsSuccessfulStatus(Get("退款状态")))
                {
                    skippedRows++;
                    continue;
                }
                try
                {
                    var record = new RefundRecord
                    {
                        orderId = Identifier(Value("订单编号")), refundId = Identifier(Value("退款编号")),
                        productName = Get("商品名称"), productModel = Get("商品型号"), productId = Get("商品ID"),
                        productCode = Get("商品编码"), modelCode = Get("型号编码"),
                        productPaidAmount = Money(Get("商品实付金额")), shippingPaidAmount = Money(Get("运费实付金额"), true),
                        refundAmount = Money(Get("买家退款金额")), shippingRefundAmount = Money(Get("运费退款金额"), true),
                        status = Get("退款状态"), refundType = Get("退款类型"), saleStage = Get("售中退/售后退"),
                        initiator = Get("退款发起人"), reason = Get("买家退款原因"), shippingStatus = Get("商品发货状态"),
                        orderPaidAt = Date(Value("订单付款时间")), requestedAt = Date(Value("退款的申请时间")),
                        completedAt = Date(Value("退款完结时间")), deadlineAt = Date(Value("超时时间")),
                        sourceFile = Path.GetFileName(sourceFile), sourceRow = row
                    };
                    if (record.orderId.Length == 0 || record.refundId.Length == 0 || record.productName.Length == 0 || record.status.Length == 0)
                        throw new FormatException("订单编号、退款编号、商品名称和退款状态不能为空");
                    records.Add(record);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    throw new FormatException($"工作表“{reader.Name}”第 {row} 行：{ex.Message}", ex);
                }
            }
        } while (reader.NextResult());
        if (!foundSheet) throw new FormatException("没有找到退款表表头，请选择含订单编号、退款编号、商品名称、商品型号、商品实付金额、买家退款金额和退款状态的文件。");
        if (sourceRows == 0) throw new FormatException("退款表中没有记录。");
        foreach (var group in records.GroupBy(r => r.refundId))
            if (group.Select(r => (r.orderId, r.productName, r.productModel)).Distinct().Count() > 1)
                throw new FormatException($"退款编号 {group.Key} 对应多个订单或商品，请核对原表。");
        var unique = RefundRules.LatestById(records);
        return new(unique, sourceRows, records.Count - unique.Count, skippedRows);
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static string Identifier(object? value)
    {
        if (value is double number)
        {
            // Excel numeric cells retain at most 15 significant digits. Refund IDs are usually longer.
            if (number >= 1e15 || number < 0 || number != Math.Truncate(number))
                throw new FormatException("编号以数值格式保存，可能已丢失精度，请从平台重新导出文本编号");
            return number.ToString("0", CultureInfo.InvariantCulture);
        }
        return Text(value);
    }

    private static decimal Money(string text, bool allowEmpty = false)
    {
        if (text.Length == 0 && allowEmpty) return 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new FormatException($"金额无效：{text}");
        return value;
    }

    private static DateTimeOffset? Date(object? value)
    {
        if (value is null or DBNull || Text(value).Length == 0) return null;
        var date = value is DateTime time ? time : DateTime.Parse(Text(value), CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
    }
}
