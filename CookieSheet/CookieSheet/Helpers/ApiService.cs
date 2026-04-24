using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CookieSheet.Data;

namespace CookieSheet.Helpers;

public class ApiService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        BaseAddress = new Uri("http://localhost:5011") // Default development URL, should be configurable
    };

    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static Task? _cacheWarmupTask;
    private static List<user> _cachedUsers = [];
    private static List<order> _cachedOrders = [];

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

            await Task.WhenAll(usersTask, ordersTask);

            (_cachedUsers, _cachedOrders) = BuildCacheGraph(usersTask.Result, ordersTask.Result);
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

    public static async Task SaveUserAsync(user user)
    {
        await _httpClient.PostAsJsonAsync("/api/users", user);
        await TryUpsertCachedUserAsync(user);
    }

    public static async Task<List<order>> GetOrdersAsync()
    {
        return await FetchOrdersAsync();
    }

    public static async Task SaveOrderAsync(order order)
    {
        await _httpClient.PostAsJsonAsync("/api/orders", order);
        await TryUpsertCachedOrderAsync(order);
    }

    public static async Task<List<user>> SaveUsersBatchAsync(List<user> users)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/users/batch", users);
        response.EnsureSuccessStatusCode();

        var savedUsers = await response.Content.ReadFromJsonAsync<List<user>>() ?? [];
        return savedUsers;
    }

    public static async Task SaveOrdersBatchAsync(List<order> orders)
    {
        await _httpClient.PostAsJsonAsync("/api/orders/batch", orders);
    }

    public static async Task ClearDataAsync()
    {
        await _httpClient.PostAsync("/api/clear", null);
        await ClearCacheAsync();
    }

    private static async Task<List<user>> FetchUsersAsync()
    {
        try
        {
            var users = await _httpClient.GetFromJsonAsync<List<user>>("/api/users");
            return users ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<order>> FetchOrdersAsync()
    {
        try
        {
            var orders = await _httpClient.GetFromJsonAsync<List<order>>("/api/orders");
            return orders ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task ClearCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            _cachedUsers = [];
            _cachedOrders = [];
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
        target.sended = source.sended;
        target.tag = source.tag;
    }
}
