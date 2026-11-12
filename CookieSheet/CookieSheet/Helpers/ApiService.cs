using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CookieSheet.Data;
using CookieSheet.Refunds;
using System.Net;

namespace CookieSheet.Helpers;

public class ApiService
{
    private static readonly JsonSerializerOptions _jsonLogOptions = new();
    private static readonly HttpClient _httpClient = new HttpClient
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("COOKIESHEET_API_URL") ?? "http://yangtze-v4.moenext.com:41011")
    };

    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static Task? _cacheWarmupTask;
    private static List<user> _cachedUsers = [];
    private static List<order> _cachedOrders = [];
    private static List<RefundRecord> _cachedRefunds = [];

    public static Task WarmUpCacheAsync()
    {
        DebugConsole.Log($"ApiService.WarmUpCacheAsync invoked. HasWarmupTask={_cacheWarmupTask != null}");
        return _cacheWarmupTask ??= RefreshCacheAsync();
    }

    public static async Task RefreshCacheAsync()
    {
        DebugConsole.Log("ApiService.RefreshCacheAsync started.");
        await _cacheLock.WaitAsync();
        try
        {
            var usersTask = FetchUsersAsync();
            var ordersTask = FetchOrdersAsync();
            var refundsTask = FetchRefundsAsync();

            await Task.WhenAll(usersTask, ordersTask, refundsTask);

            var refundsByOrder = refundsTask.Result.ToLookup(r => r.orderId);
            foreach (var order in ordersTask.Result) order.ApplyRefunds(refundsByOrder[order.orderId]);
            (_cachedUsers, _cachedOrders) = BuildCacheGraph(usersTask.Result, ordersTask.Result);
            _cachedRefunds = refundsTask.Result;
            _cacheWarmupTask = Task.CompletedTask;
            DebugConsole.Log($"ApiService.RefreshCacheAsync completed. Users={_cachedUsers.Count}, Orders={_cachedOrders.Count}");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public static async Task<List<user>> GetCachedUsersAsync()
    {
        await WarmUpCacheAsync();
        DebugConsole.Log($"ApiService.GetCachedUsersAsync returning {_cachedUsers.Count} users.");
        return _cachedUsers;
    }

    public static async Task<List<order>> GetCachedOrdersAsync()
    {
        await WarmUpCacheAsync();
        DebugConsole.Log($"ApiService.GetCachedOrdersAsync returning {_cachedOrders.Count} orders.");
        return _cachedOrders;
    }

    public static async Task<List<user>> GetUsersAsync()
    {
        return await FetchUsersAsync();
    }

    public static async Task<List<RefundRecord>> GetCachedRefundsAsync()
    {
        await WarmUpCacheAsync();
        return _cachedRefunds;
    }

    public sealed record GiftRecalculationResult(int Batches, int Orders, int PendingSetGroups);
    public sealed record OrderDeleteResult(bool Deleted, GiftRecalculationResult Gifts);
    public sealed class GiftChoiceRequiredException() : InvalidOperationException("请先选择本批次的赠品选项。");

    public static async Task<OrderDeleteResult> DeleteOrderAsync(order selected, bool? fullGift = null, bool? setGift = null)
    {
        using var response = await _httpClient.PostAsJsonAsync("/api/orders/delete", new
        {
            OrderId = selected.orderId, ExpectedUserId = selected.userId, ExpectedBatch = selected.tag,
            FullGift = fullGift, SetGift = setGift
        });
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new InvalidOperationException("服务器尚未支持删除单笔订单，请更新后端。");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("code", out var code) && code.GetString() == "gift_choice_required")
                    throw new GiftChoiceRequiredException();
                if (json.RootElement.TryGetProperty("message", out var value)) message = value.GetString();
            }
            catch (JsonException) { }
            throw new InvalidOperationException(message ?? $"删除失败（HTTP {(int)response.StatusCode}）。");
        }
        var result = JsonSerializer.Deserialize<OrderDeleteResult>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("服务器响应异常，请刷新确认订单是否已删除。");
        await _cacheLock.WaitAsync();
        try
        {
            _cachedOrders.RemoveAll(o => o.orderId == selected.orderId);
            foreach (var user in _cachedUsers) user.orders.RemoveAll(o => o.orderId == selected.orderId);
        }
        finally { _cacheLock.Release(); }
        return result;
    }
    public sealed record RefundSaveResult(int Added, int Updated, int Unchanged, int Skipped, int DeletedSkipped = 0,
        GiftRecalculationResult? Gifts = null);

    public sealed record BackupInfo(Guid Id, DateTimeOffset CreatedAt, int Users, int Orders, int Refunds);

    public static async Task<BackupInfo?> GetLatestBackupAsync()
    {
        using var response = await _httpClient.GetAsync("/api/backups/latest");
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await ReadBackupResponseAsync(response);
    }

    public static async Task<BackupInfo> CreateBackupAsync()
    {
        using var response = await _httpClient.PostAsync("/api/backups", null);
        return await ReadBackupResponseAsync(response);
    }

    public static async Task<BackupInfo> RestoreBackupAsync(Guid backupId)
    {
        await _cacheLock.WaitAsync();
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/backups/restore", new { backupId });
            return await ReadBackupResponseAsync(response);
        }
        finally
        {
            // A lost response can still mean restore committed; never reuse stale data.
            _cachedUsers = [];
            _cachedOrders = [];
            _cachedRefunds = [];
            _cacheWarmupTask = null;
            _cacheLock.Release();
        }
    }

    private static async Task<BackupInfo> ReadBackupResponseAsync(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new InvalidOperationException("服务器尚未支持备份和恢复，请先更新 CookieSheetServer。");
        if (!response.IsSuccessStatusCode)
        {
            var message = $"操作失败（HTTP {(int)response.StatusCode}）。";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("message", out var value)) message = value.GetString() ?? message;
            }
            catch (JsonException) { }
            throw new InvalidOperationException(message);
        }
        return await response.Content.ReadFromJsonAsync<BackupInfo>()
            ?? throw new InvalidOperationException("服务器未返回备份信息。");
    }

    public static Task DeleteRefundAsync(string refundId) => DeleteRefundsAsync([refundId]);

    public static async Task DeleteRefundsAsync(IReadOnlyList<string> refundIds)
    {
        if (refundIds.Count == 0 || refundIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("请选择有效的退款编号。", nameof(refundIds));
        var ids = refundIds.ToHashSet(StringComparer.Ordinal);
        await _cacheLock.WaitAsync();
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/refunds/delete-batch", new { RefundIds = ids });
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new InvalidOperationException("服务器尚未支持批量软删除，请先更新 CookieSheetServer。");
            response.EnsureSuccessStatusCode();

            var affectedOrders = _cachedRefunds.Where(r => ids.Contains(r.refundId)).Select(r => r.orderId).ToHashSet();
            _cachedRefunds.RemoveAll(r => ids.Contains(r.refundId));
            var remaining = _cachedRefunds.ToLookup(r => r.orderId);
            foreach (var order in _cachedOrders.Where(o => affectedOrders.Contains(o.orderId)))
                order.ApplyRefunds(remaining[order.orderId]);
        }
        finally { _cacheLock.Release(); }
    }

    public static async Task<RefundSaveResult> SaveRefundsBatchAsync(List<RefundRecord> records, List<BatchGiftChoice> giftChoices)
    {
        using var response = await _httpClient.PostAsJsonAsync("/api/refunds/import-with-gifts", new { Records = records, GiftChoices = giftChoices });
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("服务器尚未支持退款导入，请先更新 CookieSheetServer 并执行数据库迁移。");
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"退款未保存（HTTP {(int)response.StatusCode}）：{message}");
        }
        return await response.Content.ReadFromJsonAsync<RefundSaveResult>()
            ?? throw new InvalidOperationException("服务器未返回退款保存结果。");
    }

    private static async Task<List<RefundRecord>> FetchRefundsAsync()
    {
        using var response = await _httpClient.GetAsync("/api/refunds");
        // Keep order queries usable while an older server is awaiting deployment.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RefundRecord>>() ?? [];
    }

    public static async Task SaveUserAsync(user user)
    {
        using var response = await PostAsJsonWithLoggingAsync("/api/users", user);
        await TryUpsertCachedUserAsync(user);
    }

    public static async Task<List<order>> GetOrdersAsync()
    {
        return await FetchOrdersAsync();
    }

    public static async Task SaveOrderAsync(order order)
    {
        using var response = await PostAsJsonWithLoggingAsync("/api/orders", order);
        await TryUpsertCachedOrderAsync(order);
    }

    public static async Task<List<user>> SaveUsersBatchAsync(List<user> users)
    {
        using var response = await PostAsJsonWithLoggingAsync("/api/users/batch", users);
        var savedUsers = await response.Content.ReadFromJsonAsync<List<user>>() ?? [];
        return savedUsers;
    }

    public static async Task SaveOrdersBatchAsync(List<order> orders)
    {
        using var response = await PostAsJsonWithLoggingAsync("/api/orders/batch", orders);
    }

    public static async Task<GiftRecalculationResult> ImportOrdersAsync(string batch, List<order> orders, List<user> newUsers, List<order> postageOrders, List<BatchGiftChoice> giftChoices, List<string> closedOrderIds)
    {
        var assignments = orders.Where(o => o.user != null && newUsers.Contains(o.user))
            .ToDictionary(o => o.orderId, o => newUsers.IndexOf(o.user));
        using var response = await _httpClient.PostAsJsonAsync("/api/orders/import-excluding-closed", new
        {
            Batch = batch, Orders = orders, NewUsers = newUsers, NewUserAssignments = assignments, GiftChoices = giftChoices,
            ClosedOrderIds = closedOrderIds,
            PostageOrderIds = postageOrders.Select(o => o.orderId).Distinct().ToList()
        });
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new InvalidOperationException("服务器尚未支持排除关闭订单，请先更新 CookieSheetServer。");
        if (!response.IsSuccessStatusCode)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("code", out var code) && code.GetString() == "order_batch_conflict")
                throw new InvalidOperationException("服务器仍在使用旧的跨批次限制，请升级服务器后重新导入。");
            throw new InvalidOperationException(json.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() : $"导入失败（HTTP {(int)response.StatusCode}）。");
        }
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("订单已保存，但服务器尚未支持自动重算赠品，请升级服务器后重新导入。");
        return JsonSerializer.Deserialize<GiftRecalculationResult>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("服务器未返回赠品重算结果。");
    }

    public static async Task ClearDataAsync()
    {
        using var response = await PostWithLoggingAsync("/api/clear");
        await ClearCacheAsync();
    }

    private static async Task<List<user>> FetchUsersAsync()
    {
        try
        {
            LogHttpRequest(HttpMethod.Get, "/api/users");
            var users = await _httpClient.GetFromJsonAsync<List<user>>("/api/users");
            LogHttpSuccess(HttpMethod.Get, "/api/users", null);
            return users ?? [];
        }
        catch (Exception ex)
        {
            LogHttpFailure(HttpMethod.Get, "/api/users", ex);
            throw;
        }
    }

    private static async Task<List<order>> FetchOrdersAsync()
    {
        try
        {
            LogHttpRequest(HttpMethod.Get, "/api/orders");
            var orders = await _httpClient.GetFromJsonAsync<List<order>>("/api/orders");
            LogHttpSuccess(HttpMethod.Get, "/api/orders", null);
            return orders ?? [];
        }
        catch (Exception ex)
        {
            LogHttpFailure(HttpMethod.Get, "/api/orders", ex);
            throw;
        }
    }

    private static async Task ClearCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            _cachedUsers = [];
            _cachedOrders = [];
            _cachedRefunds = [];
            _cacheWarmupTask = Task.CompletedTask;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static (List<user> Users, List<order> Orders) BuildCacheGraph(List<user> users, List<order> orders)
    {
        var normalizedUsers = users ?? [];
        var normalizedOrders = orders ?? [];

        var userMap = new Dictionary<int, user>();
        foreach (var currentUser in normalizedUsers)
        {
            currentUser.orders = [];
            userMap[currentUser.userId] = currentUser;
        }

        foreach (var currentOrder in normalizedOrders)
        {
            if (userMap.TryGetValue(currentOrder.userId, out var owner))
            {
                currentOrder.user = owner;
                owner.orders.Add(currentOrder);
            }
        }

        var usersWithOrders = normalizedUsers.Count(u => u.orders.Count > 0);
        DebugConsole.Log($"ApiService.BuildCacheGraph linked {normalizedOrders.Count} orders to {usersWithOrders} users.");

        return (normalizedUsers, normalizedOrders);
    }

    private static async Task TryUpsertCachedUserAsync(user incomingUser)
    {
        await WarmUpCacheAsync();

        await _cacheLock.WaitAsync();
        try
        {
            var existingUser = _cachedUsers.Find(u => u.userId == incomingUser.userId);
            if (existingUser == null)
            {
                _cachedUsers.Add(incomingUser);
                return;
            }

            existingUser.tel = incomingUser.tel;
            existingUser.qq = incomingUser.qq;
            existingUser.cn = incomingUser.cn;
            existingUser.receiver = incomingUser.receiver;
            existingUser.account = incomingUser.account;
            existingUser.price = incomingUser.price;
            existingUser.address = incomingUser.address;
            existingUser.orders = incomingUser.orders ?? [];
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static async Task TryUpsertCachedOrderAsync(order incomingOrder)
    {
        var shouldRefresh = false;

        await WarmUpCacheAsync();
        incomingOrder.ApplyRefunds(_cachedRefunds);

        await _cacheLock.WaitAsync();
        try
        {
            var existingOrder = _cachedOrders.Find(o => o.orderId == incomingOrder.orderId);
            if (existingOrder == null)
            {
                _cachedOrders.Add(incomingOrder);
                shouldRefresh = !UpsertOrderOnCachedUser(incomingOrder);
            }
            else
            {
                CopyOrderValues(existingOrder, incomingOrder);
                shouldRefresh = !UpsertOrderOnCachedUser(incomingOrder);
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        if (shouldRefresh)
        {
            await RefreshCacheAsync();
        }
    }

    private static bool UpsertOrderOnCachedUser(order incomingOrder)
    {
        var cachedUser = _cachedUsers.Find(u => u.userId == incomingOrder.userId);
        if (cachedUser == null)
        {
            return false;
        }

        var existingOrder = cachedUser.orders.Find(o => o.orderId == incomingOrder.orderId);
        if (existingOrder == null)
        {
            cachedUser.orders.Add(incomingOrder);
            return true;
        }

        CopyOrderValues(existingOrder, incomingOrder);
        return true;
    }

    private static void CopyOrderValues(order target, order source)
    {
        target.orderId = source.orderId;
        target.user = source.user;
        target.userId = source.userId;
        target.tel = source.tel;
        target.orderedAt = source.orderedAt;
        target.productName = source.productName;
        target.productModel = source.productModel;
        target.productCount = source.productCount;
        target.price = source.price;
        target.receivableAmount = source.receivableAmount;
        target.productRefundAmount = source.productRefundAmount;
        target.orderRefundStatus = source.orderRefundStatus;
        target.sended = source.sended;
        target.tag = source.tag;
        target.ApplyRefunds(_cachedRefunds);
    }

    private static async Task<HttpResponseMessage> PostAsJsonWithLoggingAsync<T>(string path, T payload)
    {
        LogHttpRequest(HttpMethod.Post, path, payload);
        var response = await _httpClient.PostAsJsonAsync(path, payload);
        LogHttpSuccess(HttpMethod.Post, path, response);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<HttpResponseMessage> PostWithLoggingAsync(string path)
    {
        LogHttpRequest(HttpMethod.Post, path);
        var response = await _httpClient.PostAsync(path, null);
        LogHttpSuccess(HttpMethod.Post, path, response);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static void LogHttpRequest(HttpMethod method, string path, object? payload = null)
    {
        var fullUrl = new Uri(_httpClient.BaseAddress!, path);
        var bodyText = payload == null ? string.Empty : $" | body={SafeSerialize(payload)}";
        DebugConsole.Log($"HTTP {method} -> {fullUrl}{bodyText}");
    }

    private static void LogHttpSuccess(HttpMethod method, string path, HttpResponseMessage? response)
    {
        var fullUrl = new Uri(_httpClient.BaseAddress!, path);
        var statusText = response == null ? "OK" : $"{(int)response.StatusCode} {response.StatusCode}";
        DebugConsole.Log($"HTTP {method} <- {fullUrl} | status={statusText}");
    }

    private static void LogHttpFailure(HttpMethod method, string path, Exception ex)
    {
        var fullUrl = new Uri(_httpClient.BaseAddress!, path);
        DebugConsole.Log($"HTTP {method} xx {fullUrl} | error={ex.Message}");
    }

    private static string SafeSerialize(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, _jsonLogOptions);
        }
        catch (Exception ex)
        {
            return $"<serialize failed: {ex.Message}>";
        }
    }
}
