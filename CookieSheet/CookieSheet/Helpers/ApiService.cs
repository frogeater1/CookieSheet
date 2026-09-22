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

    public sealed record RefundSaveResult(int Added, int Updated, int Unchanged, int Skipped);

    public static async Task<RefundSaveResult> SaveRefundsBatchAsync(List<RefundRecord> records)
    {
        using var response = await _httpClient.PostAsJsonAsync("/api/refunds/batch", records);
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
        target.productUnitPrice = source.productUnitPrice;
        target.productId = source.productId;
        target.ApplyRefunds(_cachedRefunds);
        target.price = source.price;
        target.sended = source.sended;
        target.tag = source.tag;
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
