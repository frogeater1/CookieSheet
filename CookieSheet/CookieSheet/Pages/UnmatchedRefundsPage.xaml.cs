using System;
using System.Collections.Generic;
using System.ComponentModel;
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

public sealed partial class UnmatchedRefundsPage : Page
{
    private List<UnmatchedRefundViewModel> _allRefunds = [];
    private List<UnmatchedRefundViewModel> _visibleRefunds = [];
    private bool _updatingSelection;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isDeleting;

    public UnmatchedRefundsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadRefundsAsync();
    }

    private async Task LoadRefundsAsync(bool refresh = false)
    {
        if (_isLoading || _isDeleting) return;
        _isLoading = true;
        FilterPanel.IsEnabled = RefreshButton.IsEnabled = RefundsList.IsEnabled = false;
        UpdateSelection();
        LoadingIndicator.Visibility = Visibility.Visible;
        LoadingIndicator.IsActive = true;
        EmptyStateText.Visibility = Visibility.Collapsed;
        StatusMessage.IsOpen = false;
        StatusMessage.Severity = InfoBarSeverity.Error;
        ResultsSummary.Text = "正在加载退款…";
        try
        {
            if (refresh) await ApiService.RefreshCacheAsync();
            await ReadCachedRefundsAsync();
            _hasLoaded = true;
            ApplySearch();
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            if (_hasLoaded) ApplySearch();
            else ResultsSummary.Text = "退款加载失败";
            StatusMessage.Message = _hasLoaded
                ? "刷新失败，仍显示上次加载的结果。请检查网络后重试。"
                : "无法加载退款，请检查网络后点击“刷新数据”重试。";
            StatusMessage.IsOpen = true;
        }
        finally
        {
            _isLoading = false;
            FilterPanel.IsEnabled = RefreshButton.IsEnabled = RefundsList.IsEnabled = true;
            UpdateSelection();
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ReadCachedRefundsAsync()
    {
        var orders = await ApiService.GetCachedOrdersAsync();
        var refunds = await ApiService.GetCachedRefundsAsync();
        var productsByOrder = orders.ToDictionary(o => o.orderId,
            o => (IReadOnlyList<OrderedProduct>)o.GetOrderedProducts());
        foreach (var item in _allRefunds) item.PropertyChanged -= OnSelectionChanged;
        _allRefunds = RefundMatching.FindUnmatched(refunds, productsByOrder)
            .Select(r => new UnmatchedRefundViewModel(r))
            .OrderByDescending(r => r.RefundedAt).ThenBy(r => r.RefundId, StringComparer.Ordinal).ToList();
        foreach (var item in _allRefunds) item.PropertyChanged += OnSelectionChanged;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isDeleting || sender is not Button { Tag: UnmatchedRefundViewModel item }) return;
        await DeleteRefundsAsync([item]);
    }

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isDeleting) return;
        await DeleteRefundsAsync(_visibleRefunds.Where(r => r.IsSelected).ToList());
    }

    private async Task DeleteRefundsAsync(IReadOnlyList<UnmatchedRefundViewModel> items)
    {
        if (items.Count == 0) return;
        _isDeleting = true;
        FilterPanel.IsEnabled = RefreshButton.IsEnabled = RefundsList.IsEnabled = false;
        UpdateSelection();
        var deleted = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = items.Count == 1 ? "删除退款记录" : $"批量删除 {items.Count} 条退款记录",
                Content = new TextBlock
                {
                    Text = (items.Count == 1
                        ? $"退款编号：{items[0].RefundId}\n订单编号：{items[0].OrderId}\n商品：{items[0].ProductName} / {items[0].ProductModel}\n退款金额：{items[0].AmountText}"
                        : $"已选择 {items.Count} 条退款，涉及 {items.Select(r => r.OrderId).Distinct().Count()} 笔订单。")
                        + "\n\n确定删除所选退款记录吗？原订单不会删除。删除后不再显示或参与退款计算，再次导入同一退款编号也不会恢复。",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            StatusMessage.IsOpen = false;
            var ids = items.Select(r => r.RefundId).ToHashSet(StringComparer.Ordinal);
            await ApiService.DeleteRefundsAsync(ids.ToList());
            deleted = true;
            foreach (var item in _allRefunds.Where(r => ids.Contains(r.RefundId))) item.PropertyChanged -= OnSelectionChanged;
            _allRefunds.RemoveAll(r => ids.Contains(r.RefundId));
            ApplySearch();
            await ApiService.RefreshCacheAsync();
            await ReadCachedRefundsAsync();
            ApplySearch();
            StatusMessage.Severity = InfoBarSeverity.Success;
            StatusMessage.Message = $"已删除 {ids.Count} 条退款记录，再次导入同一退款编号将自动跳过。";
            StatusMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            StatusMessage.Severity = InfoBarSeverity.Error;
            StatusMessage.Message = deleted
                ? "退款记录已删除，但列表更新失败。请点击“刷新数据”重试。"
                : $"未能确认删除结果：{ex.Message} 请刷新数据核实后再重试。";
            StatusMessage.IsOpen = true;
        }
        finally
        {
            _isDeleting = false;
            FilterPanel.IsEnabled = RefreshButton.IsEnabled = RefundsList.IsEnabled = true;
            UpdateSelection();
        }
    }

    private void ApplySearch()
    {
        if (!_hasLoaded) return;
        SetSelection(false);
        var keyword = SearchBox.Text?.Trim() ?? "";
        var results = _allRefunds.Where(r => keyword.Length == 0
            || r.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        _visibleRefunds = results;
        RefundsList.ItemsSource = results;
        UpdateSelection();
        ResultsSummary.Text = $"显示 {results.Count} / {_allRefunds.Count} 条待匹配退款 · 涉及 {results.Select(r => r.OrderId).Distinct().Count()} 笔订单";
        EmptyStateText.Text = _allRefunds.Count == 0 ? "暂无待匹配退款。" : "没有符合条件的退款。";
        EmptyStateText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSelection) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = _visibleRefunds.Count(r => r.IsSelected);
        SelectionText.Text = $"已选 {count} 条";
        SelectAllButton.IsEnabled = !_isLoading && !_isDeleting && _visibleRefunds.Count > 0;
        ClearSelectionButton.IsEnabled = DeleteSelectedButton.IsEnabled = !_isLoading && !_isDeleting && count > 0;
    }

    private void SetSelection(bool selected)
    {
        _updatingSelection = true;
        try { foreach (var item in _visibleRefunds) item.IsSelected = selected; }
        finally { _updatingSelection = false; }
        UpdateSelection();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetSelection(true);
    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => SetSelection(false);

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadRefundsAsync(true);
    private void OnSearchClick(object sender, RoutedEventArgs e) => ApplySearch();
    private void OnResetClick(object sender, RoutedEventArgs e) { SearchBox.Text = ""; ApplySearch(); }
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ApplySearch();
        e.Handled = true;
    }
}
