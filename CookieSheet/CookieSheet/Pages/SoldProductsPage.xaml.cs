using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CookieSheet.Helpers;
using CookieSheet.Refunds;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace CookieSheet;

public sealed partial class SoldProductsPage : Page
{
    private List<SoldOrderSnapshot> _orders = [];
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _updatingFilters = true;

    public SoldProductsPage()
    {
        InitializeComponent();
        _updatingFilters = false;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadProductsAsync();
    }

    private async Task LoadProductsAsync(bool refresh = false)
    {
        if (_isLoading) return;
        _isLoading = true;
        FilterPanel.IsEnabled = RefreshButton.IsEnabled = false;
        LoadingIndicator.Visibility = Visibility.Visible;
        LoadingIndicator.IsActive = true;
        EmptyStateText.Visibility = Visibility.Collapsed;
        StatusMessage.IsOpen = false;
        ResultsSummary.Text = "正在加载商品…";
        try
        {
            if (refresh) await ApiService.RefreshCacheAsync();
            var orders = await ApiService.GetCachedOrdersAsync();
            var refunds = await ApiService.GetCachedRefundsAsync();
            var snapshots = orders.Select(o => new SoldOrderSnapshot(o.orderId, o.tag, o.ProductRefunds.ToArray())).ToList();
            var productsByOrder = orders.ToDictionary(o => o.orderId,
                o => (IReadOnlyList<OrderedProduct>)o.GetOrderedProducts());
            var unmatchedCount = RefundMatching.FindUnmatched(refunds, productsByOrder).Count;

            _orders = snapshots;
            _hasLoaded = true;
            UnmatchedMessage.Message = $"全部数据中有 {unmatchedCount:N0} 条待匹配退款，尚未计入已确认退款件数。请到“待匹配退款”查看。";
            UnmatchedMessage.IsOpen = unmatchedCount > 0;
            UpdateTags();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            if (_hasLoaded) ApplyFilters();
            else ResultsSummary.Text = "商品加载失败";
            StatusMessage.Message = _hasLoaded
                ? "刷新失败，仍显示上次加载的结果。请检查网络后点击“刷新数据”重试。"
                : "无法加载商品，请检查网络后点击“刷新数据”重试。";
            StatusMessage.IsOpen = true;
        }
        finally
        {
            _isLoading = false;
            FilterPanel.IsEnabled = RefreshButton.IsEnabled = true;
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTags()
    {
        var previousTag = (TagFilter.SelectedItem as SoldProductTagFilter)?.Value;
        var tags = new List<SoldProductTagFilter> { new("全部批次", null) };
        if (_orders.Any(o => SoldProductsQuery.NormalizeTag(o.Tag).Length == 0))
            tags.Add(new("无标签", ""));
        tags.AddRange(_orders.Select(o => SoldProductsQuery.NormalizeTag(o.Tag))
            .Where(tag => tag.Length > 0).Distinct().OrderBy(tag => tag)
            .Select(tag => new SoldProductTagFilter(tag == "无标签" ? "无标签（批次）" : tag, tag)));
        _updatingFilters = true;
        try
        {
            TagFilter.ItemsSource = tags;
            TagFilter.SelectedItem = tags.FirstOrDefault(tag => tag.Value == previousTag) ?? tags[0];
        }
        finally { _updatingFilters = false; }
    }

    private void ApplyFilters()
    {
        if (!_hasLoaded || _updatingFilters) return;
        var result = SoldProductsQuery.Apply(_orders, SearchBox.Text,
            (TagFilter.SelectedItem as SoldProductTagFilter)?.Value, IncludeGifts.IsChecked == true);
        var groups = result.Products.GroupBy(p => p.Tag)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SoldProductGroup(group.Key, group)).ToList();
        GroupedProducts.Source = groups;
        var net = result.NetQuantity is long quantity ? $"净售出 {quantity:N0} 件" : "净售出待核对";
        ResultsSummary.Text = $"{groups.Count:N0} 个批次 · {result.Products.Count:N0} 项批次商品 · {net}";
        EmptyStateText.Text = _orders.Count == 0
            ? "暂无订单，请先到“数据导入”导入订单。"
            : "没有符合条件的商品。请调整搜索、批次或赠品选项。";
        EmptyStateText.Visibility = result.Products.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadProductsAsync(true);
    private void OnSearchClick(object sender, RoutedEventArgs e) => ApplyFilters();
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void OnGiftsChanged(object sender, RoutedEventArgs e) => ApplyFilters();
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ApplyFilters();
        e.Handled = true;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try
        {
            SearchBox.Text = "";
            TagFilter.SelectedIndex = 0;
            IncludeGifts.IsChecked = true;
        }
        finally { _updatingFilters = false; }
        ApplyFilters();
    }
}

public sealed record SoldProductTagFilter(string Label, string? Value);
