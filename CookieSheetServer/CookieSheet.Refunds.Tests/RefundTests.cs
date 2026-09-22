using System.IO.Compression;
using System.Security;
using System.Text;
using CookieSheet.Refunds;
using CookieSheetServer.Data;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class RefundTests
{
    private static OrderedProduct Product(int quantity = 5, decimal unitPrice = 7) => new("企划", "明信片", quantity, unitPrice);
    private static RefundRecord Refund(string id = "r1", decimal amount = 21, string status = "退款成功") => new()
    {
        refundId = id, orderId = "o1", productName = "企划", productModel = "明信片", refundAmount = amount,
        productPaidAmount = 35, status = status, requestedAt = DateTimeOffset.Parse("2026-09-12T12:00:00+08:00"),
        completedAt = status == "退款成功" ? DateTimeOffset.Parse("2026-09-13T12:00:00+08:00") : null
    };

    [Fact] public void FiveBoughtThreeRefundedRetainsOriginalQuantity()
    {
        var result = Assert.Single(RefundCalculator.Calculate("o1", [Product()], [Refund()]));
        Assert.Equal(ProductRefundStatus.Partial, result.Status);
        Assert.Equal(3, result.RefundedQuantity);
        Assert.Equal(5, result.Product.Quantity);
        Assert.Contains("剩 2", result.Annotation);
    }

    [Fact] public void MultipleSuccessfulRefundsAccumulateAndRejectedAttemptDoesNot()
    {
        var result = Assert.Single(RefundCalculator.Calculate("o1", [Product()],
            [Refund("a", 7), Refund("b", 28), Refund("c", 28, "买家拒绝退款，交易恢复正常")]));
        Assert.Equal(ProductRefundStatus.Full, result.Status);
        Assert.Equal(5, result.RefundedQuantity);
        Assert.Equal(35, result.RefundedAmount);
    }

    [Theory]
    [InlineData("商家发起退款，待买家处理")]
    [InlineData("退款中-银行处理中")]
    [InlineData("买家取消退款，交易恢复正常")]
    [InlineData("买家拒绝退款，交易恢复正常")]
    [InlineData("退款完成")]
    public void OnlySuccessAffectsQuantity(string status)
    {
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [Product()], [Refund(status: status)])).Status);
    }

    [Fact] public void SameRefundIdIsNotCountedTwice()
    {
        var result = Assert.Single(RefundCalculator.Calculate("o1", [Product()], [Refund(), Refund()]));
        Assert.Equal(3, result.RefundedQuantity);
    }

    [Fact] public void LegacyOrdersUseLinePaidAmountAndOriginalQuantity()
    {
        var result = RefundCalculator.Calculate("o1", [Product(unitPrice: 0), new("企划", "色纸", 2, 30)], [Refund()]);
        Assert.Equal(3, result[0].RefundedQuantity);
        Assert.Equal(ProductRefundStatus.None, result[1].Status);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(36)]
    public void FractionalOrExcessRefundsRequireReview(decimal amount)
    {
        Assert.Equal(ProductRefundStatus.NeedsReview,
            Assert.Single(RefundCalculator.Calculate("o1", [Product()], [Refund(amount: amount)])).Status);
    }

    [Fact] public void ShippingRefundIsNotTreatedAsProductQuantity()
    {
        var refund = Refund(amount: 0); refund.shippingRefundAmount = 8;
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [Product()], [refund])).Status);
    }

    [Theory]
    [InlineData("补邮", "补邮")]
    [InlineData("补邮", "企划")]
    [InlineData("企划", " 补邮 ")]
    public void PostageNeverMatchesEvenWhenProductIdAndModelMatch(string orderName, string refundName)
    {
        var product = new OrderedProduct(orderName, "明信片", 5, 7, "same-product-id");
        var refund = Refund();
        refund.productName = refundName;
        refund.productId = "same-product-id";
        Assert.False(RefundCalculator.Matches(product, refund));
        var result = Assert.Single(RefundCalculator.Calculate("o1", [product], [refund]));
        Assert.Equal(ProductRefundStatus.None, result.Status);
        Assert.Equal(0, result.RefundedQuantity);
    }

    [Fact] public async Task SuccessfulPostageRefundIsStoredWithoutProductMatching()
    {
        using var db = new AppDbContext(Options());
        var refund = Refund();
        refund.productName = "补邮";
        Assert.Equal(1, (await RefundImportService.SaveAsync(db, [refund])).Added);
        Assert.Single(await db.refunds.ToListAsync());
    }

    [Fact] public void MissingAndConflictingPricesRequireReview()
    {
        var r = Refund(); r.productPaidAmount = 0;
        Assert.Equal(ProductRefundStatus.NeedsReview, Assert.Single(RefundCalculator.Calculate("o1", [Product(unitPrice: 0)], [r])).Status);
        r.productPaidAmount = 70;
        Assert.Equal(ProductRefundStatus.NeedsReview, Assert.Single(RefundCalculator.Calculate("o1", [Product()], [r])).Status);
    }

    [Fact] public void MatchingDoesNotLeakAcrossOrdersModelsOrProducts()
    {
        var r = Refund();
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("another", [Product()], [r])).Status);
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [new("别的企划", "明信片", 5, 7)], [r])).Status);
        Assert.Equal(ProductRefundStatus.None, Assert.Single(RefundCalculator.Calculate("o1", [new("企划", "色纸", 5, 7)], [r])).Status);
    }

    [Fact] public void DuplicateOrderLinesAreNotBothCountedAsRefunded()
    {
        Assert.All(RefundCalculator.Calculate("o1", [Product(), Product()], [Refund()]),
            r => Assert.Equal(ProductRefundStatus.NeedsReview, r.Status));
    }

    [Fact] public void EmptyModelMatchesNameFallback()
    {
        var r = Refund(); r.productName = "无型号商品"; r.productModel = "";
        Assert.Equal(3, Assert.Single(RefundCalculator.Calculate("o1", [new("无型号商品", "无型号商品", 5, 7)], [r])).RefundedQuantity);
    }

    [Fact] public void OneCentGoodsAndRepeatingDecimalUnitPricesRemainExact()
    {
        var r = Refund(amount: 0.01m); r.productPaidAmount = 0.01m;
        Assert.Equal(ProductRefundStatus.Full, Assert.Single(RefundCalculator.Calculate("o1", [Product(1, 0)], [r])).Status);
        r.refundAmount = 10; r.productPaidAmount = 10;
        Assert.Equal(3, Assert.Single(RefundCalculator.Calculate("o1", [Product(3, 0)], [r])).RefundedQuantity);
    }

    [Fact] public void OrderPriceImportSeparatesShippingAndNeverAllocatesMultiProductTotal()
    {
        Assert.Equal([7m], OrderImportPricing.Read(null, "8", 43, [5]));
        Assert.Equal([0m, 0m], OrderImportPricing.Read(null, "8", 43, [5, 2]));
        Assert.Equal([7m, 30m], OrderImportPricing.Read("7;30", "8", 103, [5, 2]));
    }

    private static DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Fact] public async Task RefundSurvivesReloadWithoutOrderAndMatchesLaterOrder()
    {
        var options = Options();
        using (var db = new AppDbContext(options))
        {
            var saved = await RefundImportService.SaveAsync(db, [Refund()]);
            Assert.Equal(1, saved.Added);
            Assert.Empty(await db.order.ToListAsync());
        }
        using (var db = new AppDbContext(options))
        {
            var records = await db.refunds.ToListAsync();
            Assert.Single(records);
            Assert.Empty(RefundCalculator.Calculate("o1", [], records));
            Assert.Equal(3, Assert.Single(RefundCalculator.Calculate("o1", [Product()], records)).RefundedQuantity);
        }
    }

    [Fact] public async Task PendingIsSkippedAndLaterSuccessIsSavedWithoutRegressing()
    {
        using var db = new AppDbContext(Options());
        Assert.Equal(1, (await RefundImportService.SaveAsync(db, [Refund(status: "商家发起退款，待买家处理")])).Skipped);
        Assert.Empty(await db.refunds.ToListAsync());
        Assert.Equal(1, (await RefundImportService.SaveAsync(db, [Refund()])).Added);
        await RefundImportService.SaveAsync(db, [Refund(), Refund()]);
        Assert.Equal(1, (await RefundImportService.SaveAsync(db, [Refund(status: "商家发起退款，待买家处理")])).Skipped);
        Assert.Single(await db.refunds.ToListAsync());
        Assert.Equal("退款成功", (await db.refunds.SingleAsync()).status);
    }

    [Fact] public async Task ServiceOnlyPersistsExactSuccessfulStatus()
    {
        using var db = new AppDbContext(Options());
        var result = await RefundImportService.SaveAsync(db,
            [Refund("success", status: " 退款成功 "), Refund("complete", status: "退款完成"),
             Refund("cancelled", status: "买家取消退款，交易恢复正常"),
             new RefundRecord { status = "退款中-银行处理中", refundAmount = -1 }]);
        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.Skipped);
        Assert.Equal("退款成功", (await db.refunds.SingleAsync()).status);
    }

    [Fact] public async Task RefundIdCannotBeReassignedToAnotherOrder()
    {
        using var db = new AppDbContext(Options());
        await RefundImportService.SaveAsync(db, [Refund()]);
        var invalid = Refund(); invalid.orderId = "other";
        await Assert.ThrowsAsync<ArgumentException>(() => RefundImportService.SaveAsync(db, [Refund("new"), invalid]));
        Assert.Single(await db.refunds.ToListAsync());
    }

    [Fact] public void RefundHasNoOrderForeignKey()
    {
        using var db = new AppDbContext(Options());
        Assert.Empty(db.Model.FindEntityType(typeof(RefundRecord))!.GetForeignKeys());
    }

    [Fact] public void ReaderPreservesLongIdsParsesUtc8AndDeduplicates()
    {
        using var file = Spreadsheet([
            ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态", "退款的申请时间"],
            ["855312470790656", "144115526827130368", "企划", "明信片", "35", "7", "退款成功", "2026-09-18 15:52:35"],
            ["855312470790656", "144115526827130368", "企划", "明信片", "35", "7", "退款成功", "2026-09-18 15:52:35"]]);
        var result = RefundImportReader.Read(file, "test.xlsx");
        var record = Assert.Single(result.Records);
        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal("144115526827130368", record.refundId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T07:52:35Z"), record.requestedAt);
    }

    [Fact] public void ReaderRejectsMissingHeaderAndMalformedAmountWithoutPartialImport()
    {
        using var missing = Spreadsheet([["订单编号"], ["abc"]]);
        Assert.Throws<FormatException>(() => RefundImportReader.Read(missing, "bad.xlsx"));
        using var invalid = Spreadsheet([
            ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态"],
            ["order", "refund", "name", "model", "35", "unknown", "退款成功"]]);
        Assert.Contains("第 2 行", Assert.Throws<FormatException>(() => RefundImportReader.Read(invalid, "bad.xlsx")).Message);
    }

    [Fact] public void ReaderRejectsLegacyXlsExtension()
    {
        using var stream = new MemoryStream();
        Assert.Contains("仅支持 .xlsx", Assert.Throws<FormatException>(() => RefundImportReader.Read(stream, "refund.xls")).Message);
    }

    [Fact] public void ReaderSkipsOtherStatusesBeforeParsingUnneededFields()
    {
        using var file = Spreadsheet([
            ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态"],
            ["o1", "r1", "企划", "明信片", "35", "7", "退款成功"],
            ["o1", "r1", "企划", "明信片", "35", "7", "退款成功"],
            ["o2", "r1", "其他商品", "型号", "无效", "无效", "退款完成"],
            ["", "", "", "", "", "", "商家发起退款，待买家处理"]]);
        var result = RefundImportReader.Read(file, "mixed.xlsx");
        Assert.Single(result.Records);
        Assert.Equal(4, result.SourceRows);
        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal(2, result.SkippedRows);
    }

    [Fact] public void ReaderReturnsEmptySuccessListWhenAllRowsAreSkipped()
    {
        using var file = Spreadsheet([
            ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态"],
            ["", "", "", "", "", "", "退款完成"]]);
        var result = RefundImportReader.Read(file, "no-success.xlsx");
        Assert.Empty(result.Records);
        Assert.Equal(1, result.SourceRows);
        Assert.Equal(1, result.SkippedRows);
    }

    [Fact] public void OriginalSampleXlsxWhenProvidedIsFullyRead()
    {
        var path = Environment.GetEnvironmentVariable("COOKIESHEET_REFUND_SAMPLE");
        if (string.IsNullOrEmpty(path)) return; // Optional private sample; never checked into the repository.
        using var stream = File.OpenRead(path);
        var result = RefundImportReader.Read(stream, Path.GetFileName(path));
        Assert.Equal(253, result.SourceRows);
        Assert.Equal(235, result.Records.Count);
        Assert.Equal(18, result.SkippedRows);
        Assert.Equal(157, result.Records.Select(r => r.orderId).Distinct().Count());
        Assert.Equal(235, result.Records.Count(RefundRules.IsSuccessful));
        Assert.Contains(result.Records, r => r.refundId == "144115526827130368" && r.refundAmount == 7);
    }

    private static MemoryStream Spreadsheet(string[][] rows)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Put(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(text); }
            Put("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
                """);
            Put("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Put("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="退款单" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Put("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            var data = string.Join("", rows.Select((cells, i) => $"<row r=\"{i + 1}\">" +
                string.Join("", cells.Select((v, j) => $"<c r=\"{(char)('A' + j)}{i + 1}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(v)}</t></is></c>")) + "</row>"));
            Put("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{data}</sheetData></worksheet>");
        }
        stream.Position = 0;
        return stream;
    }
}
