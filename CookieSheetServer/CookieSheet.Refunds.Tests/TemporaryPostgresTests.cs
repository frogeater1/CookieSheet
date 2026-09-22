using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CookieSheetServer.Data.Entities;
using Npgsql;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public sealed class TemporaryPostgresFactAttribute : FactAttribute
{
    public TemporaryPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COOKIESHEET_TEST_DATABASE")))
            Skip = "Requires the isolated PostgreSQL database started by scripts/Start-RefundTest.ps1.";
    }
}

public class TemporaryPostgresTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TemporaryPostgresFact]
    public async Task RealApiPersistsRefundsBeforeOrdersAndReconcilesQuantities()
    {
        var connectionString = Environment.GetEnvironmentVariable("COOKIESHEET_TEST_DATABASE")!;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        // Never run these write tests against an arbitrary database or API.
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(55439, connection.Port);
        Assert.Equal("cookiesheet_refund_test", connection.Database);
        var apiUrl = Environment.GetEnvironmentVariable("COOKIESHEET_TEST_API");
        Assert.Equal("http://127.0.0.1:51873", apiUrl);
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl!), Timeout = TimeSpan.FromSeconds(45) };
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        Assert.Equal("cookiesheet_refund_test", await Scalar<string>(db, "select current_database()"));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from \"__EFMigrationsHistory\" where \"MigrationId\" like '%AddIndependentRefunds'"));
        Assert.Equal(0L, await Scalar<long>(db, "select count(*) from pg_constraint where conrelid = 'refunds'::regclass and contype = 'f'"));

        var samplePath = Environment.GetEnvironmentVariable("COOKIESHEET_REFUND_SAMPLE")!;
        using var sample = File.OpenRead(samplePath);
        var imported = RefundImportReader.Read(sample, Path.GetFileName(samplePath));
        Assert.Equal(253, imported.SourceRows);
        Assert.Equal(18, imported.SkippedRows);
        Assert.Equal(235, imported.Records.Count);
        var sampleIds = imported.Records.Select(r => r.refundId).ToHashSet();
        var beforeOrders = await Get<order>(http, "/api/orders");
        var matchedBefore = imported.Records.Select(r => r.orderId).Distinct().Intersect(beforeOrders.Select(o => o.orderId)).Count();
        await Post(http, "/api/refunds/batch", imported.Records);
        var firstRead = (await Get<RefundRecord>(http, "/api/refunds")).Where(r => sampleIds.Contains(r.refundId)).ToList();
        Assert.Equal(235, firstRead.Count);
        Assert.Equal(235, firstRead.Count(RefundRules.IsSuccessful));
        Assert.Contains(firstRead, r => r.refundId == "144115526827130368" && r.refundAmount == 7);
        await Post(http, "/api/refunds/batch", imported.Records);
        Assert.Equal(235, (await Get<RefundRecord>(http, "/api/refunds")).Count(r => sampleIds.Contains(r.refundId)));

        var owner = await AddUser(http, "原订单副本（测试）");
        var ordersPath = Environment.GetEnvironmentVariable("COOKIESHEET_TEST_ORDERS")!;
        var originalOrders = JsonSerializer.Deserialize<List<order>>(await File.ReadAllTextAsync(ordersPath), Json)!;
        foreach (var order in originalOrders) { order.userId = owner.userId; order.tel = ""; }
        foreach (var chunk in originalOrders.Chunk(200)) await Post(http, "/api/orders/batch", chunk);
        var savedOrders = await Get<order>(http, "/api/orders");
        var originalIds = originalOrders.Select(o => o.orderId).ToHashSet();
        var restoredOriginals = savedOrders.Where(o => originalIds.Contains(o.orderId)).ToList();
        Assert.Equal(originalOrders.Count, restoredOriginals.Count);
        var matchedAfter = imported.Records.Select(r => r.orderId).Distinct().Intersect(originalIds).Count();
        var sampleMatches = restoredOriginals.SelectMany(o => Calculate(o, firstRead)).Where(p => p.Status != ProductRefundStatus.None).ToList();

        var testOwner = await AddUser(http, "退款功能验证");
        var prefix = "TEST-" + Guid.NewGuid().ToString("N")[..10];
        var now = DateTimeOffset.UtcNow;
        RefundRecord Refund(string id, string orderId, decimal amount, string status = "退款成功") => new()
        {
            refundId = prefix + "-" + id, orderId = orderId, productName = "临时测试企划", productModel = "明信片",
            productId = "demo-postcard", productPaidAmount = 35, refundAmount = amount, status = status,
            requestedAt = now.AddMinutes(-5), completedAt = status == "退款成功" ? now : null,
            sourceFile = "temporary-integration-test.xlsx"
        };
        order Order(string suffix, decimal unitPrice = 7) => new()
        {
            orderId = prefix + "-" + suffix, userId = testOwner.userId, tel = "", orderedAt = now,
            productName = ["临时测试企划"], productModel = ["明信片"], productCount = [5],
            productUnitPrice = [unitPrice], productId = ["demo-postcard"], price = 35, tag = "退款测试-" + suffix
        };

        var partial = Order("买5退3");
        var partialRefund = Refund("partial", partial.orderId, 21);
        await Post(http, "/api/refunds/batch", new[] { partialRefund });
        Assert.DoesNotContain(await Get<order>(http, "/api/orders"), o => o.orderId == partial.orderId);
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from refunds where \"refundId\" = @id", partialRefund.refundId));
        await Post(http, "/api/orders", partial);
        Assert.Equal(3, (await Result(http, partial.orderId)).RefundedQuantity);

        await Post(http, "/api/refunds/batch", new[] { partialRefund, partialRefund });
        Assert.Equal(3, (await Result(http, partial.orderId)).RefundedQuantity);
        var older = Refund("partial", partial.orderId, 21, "商家发起退款，待买家处理");
        await Post(http, "/api/refunds/batch", new[] { older });
        Assert.Equal("退款成功", (await Get<RefundRecord>(http, "/api/refunds")).Single(r => r.refundId == partialRefund.refundId).status);

        var full = Order("分两次全退");
        var legacy = Order("旧订单无单价", 0);
        var review = Order("非整件金额");
        var excess = Order("超额退款");
        var pending = Order("待处理");
        var cancelled = Order("已取消");
        await Post(http, "/api/orders/batch", new[] { full, legacy, review, excess, pending, cancelled });
        await Post(http, "/api/refunds/batch", new[]
        {
            Refund("full1", full.orderId, 7), Refund("full2", full.orderId, 28), Refund("refused", full.orderId, 28, "买家拒绝退款，交易恢复正常"),
            Refund("legacy", legacy.orderId, 21), Refund("review", review.orderId, 20), Refund("excess", excess.orderId, 40),
            Refund("pending", pending.orderId, 21, "商家发起退款，待买家处理"), Refund("cancelled", cancelled.orderId, 21, "买家取消退款，交易恢复正常")
        });
        Assert.Equal(ProductRefundStatus.Full, (await Result(http, full.orderId)).Status);
        Assert.Equal(5, (await Result(http, full.orderId)).RefundedQuantity);
        Assert.Equal(3, (await Result(http, legacy.orderId)).RefundedQuantity);
        Assert.Equal(ProductRefundStatus.NeedsReview, (await Result(http, review.orderId)).Status);
        Assert.Equal(ProductRefundStatus.NeedsReview, (await Result(http, excess.orderId)).Status);
        Assert.Equal(ProductRefundStatus.None, (await Result(http, pending.orderId)).Status);
        Assert.Equal(ProductRefundStatus.None, (await Result(http, cancelled.orderId)).Status);
        Assert.DoesNotContain(await Get<RefundRecord>(http, "/api/refunds"), r => r.refundId == prefix + "-pending" || r.refundId == prefix + "-cancelled");
        // A pending attempt is skipped; importing it after success creates the record.
        var statusOrder = Order("状态更新成功");
        await Post(http, "/api/orders", statusOrder);
        await Post(http, "/api/refunds/batch", new[] { Refund("status", statusOrder.orderId, 21, "商家发起退款，待买家处理") });
        Assert.DoesNotContain(await Get<RefundRecord>(http, "/api/refunds"), r => r.refundId == prefix + "-status");
        await Post(http, "/api/refunds/batch", new[] { Refund("status", statusOrder.orderId, 21) });
        Assert.Equal(3, (await Result(http, statusOrder.orderId)).RefundedQuantity);

        var conflict = Refund("partial", prefix + "-wrong-order", 21);
        using var bad = await http.PostAsJsonAsync("/api/refunds/batch", new[] { Refund("must-not-save", partial.orderId, 1), conflict });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(0L, await Scalar<long>(db, "select count(*) from refunds where \"refundId\" = @id", prefix + "-must-not-save"));
        Assert.Equal(21m, await Scalar<decimal>(db, "select \"refundAmount\" from refunds where \"refundId\" = @id", partialRefund.refundId));

        // Verify arrays, decimals, dates and records through a fresh database connection, not the API cache.
        await using var reopened = new NpgsqlConnection(connectionString);
        await reopened.OpenAsync();
        Assert.Equal(5, await Scalar<int>(reopened, "select \"productCount\"[1] from \"order\" where \"orderId\" = @id", partial.orderId));
        Assert.Equal(7m, await Scalar<decimal>(reopened, "select \"productUnitPrice\"[1] from \"order\" where \"orderId\" = @id", partial.orderId));
        var report = new
        {
            testedAt = DateTimeOffset.Now, database = connection.Database, api = apiUrl,
            sampleRefundRecords = imported.Records.Count, sampleOrders = imported.Records.Select(r => r.orderId).Distinct().Count(),
            matchedOrdersBeforeOrderImport = matchedBefore, originalOrdersCopied = originalOrders.Count, matchedOrdersAfterOrderImport = matchedAfter,
            matchedSuccessfulRecords = firstRead.Count(r => RefundRules.IsSuccessful(r) && originalIds.Contains(r.orderId)),
            sampleProductStates = sampleMatches.GroupBy(p => p.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            checks = new[] { "PostgreSQL migrations", "Independent refunds without foreign key", "253-row XLSX sample", "Refund first / order later", "Idempotent reimport", "Old status cannot overwrite success", "5 bought / 3 refunded", "Multiple successful refunds / full refund", "Legacy price fallback", "Fractional amount review", "Excess amount review", "Pending/cancelled ignored", "Pending to success", "Invalid batch is atomic", "Database readback of decimal and array fields" },
            syntheticOrderPrefix = prefix, totalOrders = await Scalar<long>(db, "select count(*) from \"order\""),
            totalRefunds = await Scalar<long>(db, "select count(*) from refunds")
        };
        var reportPath = Environment.GetEnvironmentVariable("COOKIESHEET_TEST_REPORT")!;
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }

    private static async Task<List<T>> Get<T>(HttpClient client, string route) => await client.GetFromJsonAsync<List<T>>(route) ?? [];
    private static async Task Post<T>(HttpClient client, string route, T payload)
    {
        using var response = await client.PostAsJsonAsync(route, payload);
        Assert.True(response.IsSuccessStatusCode, $"{route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
    private static async Task<user> AddUser(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/users", new user { userId = 0, cn = name, qq = "", tel = "", address = "", receiver = "测试", account = "local-test" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<user>())!;
    }
    private static List<ProductRefundResult> Calculate(order order, List<RefundRecord> refunds) =>
        RefundCalculator.Calculate(order.orderId,
            order.productCount.Select((count, i) => new OrderedProduct(order.productName.ElementAtOrDefault(i) ?? "", order.productModel.ElementAtOrDefault(i) ?? "",
                count, order.productUnitPrice.ElementAtOrDefault(i), order.productId.ElementAtOrDefault(i) ?? "")).ToList(), refunds);
    private static async Task<ProductRefundResult> Result(HttpClient client, string orderId)
    {
        var order = (await Get<order>(client, "/api/orders")).Single(o => o.orderId == orderId);
        return Assert.Single(Calculate(order, await Get<RefundRecord>(client, "/api/refunds")));
    }
    private static async Task<T> Scalar<T>(NpgsqlConnection db, string sql, string? id = null)
    {
        await using var command = new NpgsqlCommand(sql, db);
        if (id != null) command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
