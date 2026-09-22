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

public sealed partial class UnmatchedRefundsPage : Page
{
    private List<UnmatchedRefundViewModel> _allRefunds = [];
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
        _allRefunds = RefundMatching.FindUnmatched(refunds, productsByOrder)
            .Select(r => new UnmatchedRefundViewModel(r))
            .OrderByDescending(r => r.RefundedAt).ThenBy(r => r.RefundId, StringComparer.Ordinal).ToList();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isDeleting || sender is not Button { Tag: UnmatchedRefundViewModel item }) return;
        _isDeleting = true;
        FilterPanel.IsEnabled = RefreshButton.IsEnabled = RefundsList.IsEnabled = false;
        var deleted = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "删除退款记录",
                Content = new TextBlock
                {
                    Text = $"退款编号：{item.RefundId}\n订单编号：{item.OrderId}\n商品：{item.ProductName} / {item.ProductModel}\n退款金额：{item.AmountText}\n\n确定删除这条退款记录吗？原订单不会删除。此操作无法撤销；再次导入包含该记录的退款表会重新添加。",
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
            await ApiService.DeleteRefundAsync(item.RefundId);
            deleted = true;
            _allRefunds.RemoveAll(r => r.RefundId == item.RefundId);
            ApplySearch();
            await ReadCachedRefundsAsync();
            ApplySearch();
            StatusMessage.Severity = InfoBarSeverity.Success;
            StatusMessage.Message = $"退款记录 {item.RefundId} 已删除。";
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
        }
    }

    private void ApplySearch()
    {
        if (!_hasLoaded) return;
        var keyword = SearchBox.Text?.Trim() ?? "";
        var results = _allRefunds.Where(r => keyword.Length == 0
            || r.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        RefundsList.ItemsSource = results;
        ResultsSummary.Text = $"显示 {results.Count} / {_allRefunds.Count} 条待匹配退款 · 涉及 {results.Select(r => r.OrderId).Distinct().Count()} 笔订单";
        EmptyStateText.Text = _allRefunds.Count == 0 ? "暂无待匹配退款。" : "没有符合条件的退款。";
        EmptyStateText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
