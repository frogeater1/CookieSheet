using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CookieSheet.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace CookieSheet;

public sealed partial class OrderListPage : Page
{
    private List<OrderListItemViewModel> _allOrders = [];
    private bool _isLoading;
    private bool _updatingFilters = true;
    private bool _hasLoaded;

    public OrderListPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        _updatingFilters = false;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadOrdersAsync();
    }

    private async Task LoadOrdersAsync(bool refresh = false)
    {
        if (_isLoading) return;

        _isLoading = true;
        FilterPanel.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        LoadingIndicator.Visibility = Visibility.Visible;
        LoadingIndicator.IsActive = true;
        EmptyStateText.Visibility = Visibility.Collapsed;
        StatusMessage.IsOpen = false;
        ResultsSummary.Text = "正在加载订单…";

        try
        {
            if (refresh) await ApiService.RefreshCacheAsync();
            var orders = await ApiService.GetCachedOrdersAsync();
            _allOrders = orders.Select(order => new OrderListItemViewModel(order)).ToList();
            _hasLoaded = true;
            UpdateTags();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            if (_hasLoaded) ApplyFilters();
            else ResultsSummary.Text = "订单加载失败";
            StatusMessage.Severity = InfoBarSeverity.Error;
            StatusMessage.Message = _hasLoaded
                ? "刷新失败，仍显示上次加载的订单。请检查网络后点击“刷新数据”重试。"
                : "无法加载订单，请检查网络后点击“刷新数据”重试。";
            StatusMessage.IsOpen = true;
        }
        finally
        {
            _isLoading = false;
            FilterPanel.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTags()
    {
        var previousTag = (TagFilter.SelectedItem as OrderTagFilter)?.Value;
        var filters = new List<OrderTagFilter> { new("全部标签", null) };
        if (_allOrders.Any(item => item.Tag.Length == 0))
            filters.Add(new OrderTagFilter("无标签", ""));
        filters.AddRange(_allOrders.Select(item => item.Tag).Where(tag => tag.Length > 0)
            .Distinct().OrderBy(tag => tag)
            .Select(tag => new OrderTagFilter(tag == "无标签" ? "无标签（标签）" : tag, tag)));

        _updatingFilters = true;
        try
        {
            TagFilter.ItemsSource = filters;
            TagFilter.SelectedItem = filters.FirstOrDefault(filter => filter.Value == previousTag) ?? filters[0];
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private void ApplyFilters()
    {
        if (_updatingFilters || !_hasLoaded) return;

        try
        {
            Enum.TryParse<OrderRefundFilter>((RefundFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var refundFilter);
            var results = OrderListQuery.Apply(_allOrders, SearchBox.Text,
                StartDatePicker.Date?.Date, EndDatePicker.Date?.Date,
                (TagFilter.SelectedItem as OrderTagFilter)?.Value, SortOrder.SelectedIndex != 1, refundFilter);

            OrdersList.ItemsSource = results;
            var sortLabel = SortOrder.SelectedIndex == 1 ? "最早优先" : "最新优先";
            ResultsSummary.Text = $"显示 {results.Count} / {_allOrders.Count} 笔订单 · {sortLabel}";
            StatusMessage.IsOpen = false;
            EmptyStateText.Text = _allOrders.Count == 0
                ? "暂无订单，请先到“数据导入”导入订单。"
                : "没有符合条件的订单。请调整筛选条件或点击“重置”。";
            EmptyStateText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (ArgumentException ex)
        {
            OrdersList.ItemsSource = null;
            EmptyStateText.Visibility = Visibility.Collapsed;
            ResultsSummary.Text = "请调整日期范围";
            StatusMessage.Severity = InfoBarSeverity.Warning;
            StatusMessage.Message = ex.Message;
            StatusMessage.IsOpen = true;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadOrdersAsync(true);

    private void OnSearchClick(object sender, RoutedEventArgs e) => ApplyFilters();

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ApplyFilters();
        e.Handled = true;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void OnDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) => ApplyFilters();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try
        {
            SearchBox.Text = "";
            StartDatePicker.Date = null;
            EndDatePicker.Date = null;
            TagFilter.SelectedIndex = 0;
            RefundFilter.SelectedIndex = 0;
            SortOrder.SelectedIndex = 0;
        }
        finally
        {
            _updatingFilters = false;
        }
        ApplyFilters();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Wrap filter groups when the navigation pane leaves limited room for the page.
        var compact = e.NewSize.Width < 1100;
        Grid.SetColumnSpan(DateFilters, compact ? 2 : 1);
        Grid.SetRow(OtherFilters, compact ? 1 : 0);
        Grid.SetColumn(OtherFilters, compact ? 0 : 1);
        Grid.SetColumnSpan(OtherFilters, compact ? 2 : 1);
    }
}

public sealed record OrderTagFilter(string Label, string? Value);
