using System.Text;
using ExcelDataReader;

namespace CookieSheet.Refunds;

internal static class ExcelImportReader
{
    public static IExcelDataReader Open(Stream stream, string sourceFile)
    {
        var extension = Path.GetExtension(sourceFile);
        if (!string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("导入仅支持 .xls 和 .xlsx 文件，请另存为 Excel 工作簿后再导入。");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { LeaveOpen = true });
    }
}
