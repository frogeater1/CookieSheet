using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CookieSheet.Data;
using CookieSheet.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Shapes;
using MiniExcelLibs;
using Windows.Storage.Pickers;
using Path = System.IO.Path;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.Csv;

namespace CookieSheet
{
    public class ExportRecord
    {
        public static List<ExportRecord> GetExportRecordList(user user, List<order> orders)
        {
            var list = new List<ExportRecord>();
            foreach (var order in orders)
            {
                for (var i = 0; i < order.productCount.Length; i++)
                {
                    var r = new ExportRecord
                    {
                        收货人 = user.receiver ?? "",
                        cn = user.cn ?? "",
                        手机号 = user.tel ?? "",
                        qq = user.qq ?? "",
                        下单账号 = user.account ?? "",
                        详细地址 = user.address ?? "",
                        总实付金额 = orders.Sum(x => x.price).ToString("0.00"),
                        订单编号 = order.orderId ?? "",
                        下单时间 = order.orderedAt.ToString("s"),
                        实付金额 = order.price.ToString("0.00"),
                        是否补邮 = order.sended ? "是" : "否",
                        商品型号 = order.productModel[i],
                        数量 = order.productCount[i].ToString(),
                    };
                    list.Add(r);
                }
            }

            return list;
        }

        public static void ProcessMerge(List<ExportRecord> list)
        {
            if (list.Count == 0) return;

            string lastAccount = "___INITIAL___"; // Use a distinct init value
            string lastOrderId = "___INITIAL___";

            foreach (var record in list)
            {
                // Check User Equality (using composite key of Receiver + Phone + Account)
                // This ensures we group by the actual person/account, even if one field is missing.
                string currentKey = $"{record.收货人}|{record.手机号}|{record.下单账号}";
                string currentOrder = record.订单编号;

                if (currentKey == lastAccount)
                {
                    // Same User -> Clear User Fields
                    record.收货人 = null;
                    record.cn = null;
                    record.手机号 = null;
                    record.qq = null;
                    record.下单账号 = null;
                    record.详细地址 = null;
                    record.总实付金额 = null;

                    // Check Order Equality (Only if user is same)
                    // Note: OrderIDs might be recycled or similar across users? unlikely, but checking user context is safer.
                    if (currentOrder == lastOrderId)
                    {
                        // Same Order -> Clear Order Fields
                        record.订单编号 = null;
                        record.下单时间 = null;
                        record.是否补邮 = null;
                        record.实付金额 = null;
                    }
                    else
                    {
                        // New Order for same User
                        lastOrderId = currentOrder;
                    }
                }
                else
                {
                    // New User
                    lastAccount = currentKey;
                    lastOrderId = currentOrder;
                }
            }
        }

        public string 收货人 { get; set; } = "";
        public string cn { get; set; } = "";
        public string 手机号 { get; set; } = "";
        public string qq { get; set; } = "";
        public string 下单账号 { get; set; } = "";
        public string 详细地址 { get; set; } = "";
        public string 总实付金额 { get; set; } = "";

        public string 订单编号 { get; set; } = "";
        public string 下单时间 { get; set; } = "";
        public string 商品型号 { get; set; } = "";
        public string 数量 { get; set; } = "";
        public string 实付金额 { get; set; } = "";
        public string 是否补邮 { get; set; } = "";
    }


    public sealed partial class QueryPage : Page
    {
        public ObservableCollection<UserViewModel> Results { get; } = new();

        public QueryPage()
        {
            this.InitializeComponent();
            // ResultsList.ItemsSource = Results; // Use x:Bind or set in XAML
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadInitialData(false);
        }

        private async void LoadInitialData(bool onlyUnsent = false) => await LoadInitialDataAsync(onlyUnsent);

        private async Task LoadInitialDataAsync(bool onlyUnsent = false)
        {
            try
            {
                Results.Clear();

                // 1. Fetch all users from API
                var users = await ApiService.GetCachedUsersAsync();

                // 4. Create ViewModels
                var viewModels = new List<UserViewModel>();

                foreach (var u in users)
                {
                    var orders = u.orders.OrderBy(x => x.orderedAt).ToList();
                    if (onlyUnsent)
                    {
                        orders = orders.Where(o => !o.sended).ToList();

                        if (orders.Count == 0) continue;
                    }

                    viewModels.Add(new UserViewModel
                    {
                        User = u,
                        Orders = orders
                    });
                }

                var sortedViewModels = viewModels
                    .OrderBy(vm => vm.User.userId)
                    .ToList();

                foreach (var vm in sortedViewModels)
                {
                    Results.Add(vm);
                }

                if (Results.Count == 0)
                {
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "数据库中暂无数据。请先去导入数据。";
                    ResultsTitle.Text = "结果展示";
                }
                else
                {
                    EmptyStateText.Visibility = Visibility.Collapsed;
                    ResultsTitle.Text = onlyUnsent ? $"未补邮用户 (共 {Results.Count} 位)" : $"全部用户 (共 {Results.Count} 位)";
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
            }
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (!RefreshButton.IsEnabled) return;

            RefreshButton.IsEnabled = false;
            RefreshButton.Content = "刷新中…";
            try
            {
                await ApiService.RefreshCacheAsync();
                SearchBox.Text = "";
                await LoadInitialDataAsync();
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                if (XamlRoot != null)
                {
                    await DialogHelper.ShowErrorDialogAsync(XamlRoot,
                        "刷新失败，仍保留原有结果。请检查网络后重试。\n" + ex.Message);
                }
            }
            finally
            {
                RefreshButton.Content = "刷新数据";
                RefreshButton.IsEnabled = true;
            }
        }

        private void OnAllClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            LoadInitialData(false);
        }

        private void OnUnpaidPostageClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            LoadInitialData(true);
        }

        private void OnSearchBoxKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                OnSearchClick(sender, e);
            }
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e)
        {
            // Implementation for search can be added later or filtering the ObservableCollection
            var keyword = SearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                LoadInitialData(false);
                return;
            }

            try
            {
                Results.Clear();

                // Simple client-side search for now, could be improved with a server endpoint
                var allUsers = await ApiService.GetCachedUsersAsync();
                var users = allUsers.Where(u => 
                    (u.qq != null && u.qq == keyword) || 
                    (u.cn != null && u.cn.Contains(keyword)) || 
                    (u.receiver != null && u.receiver.Contains(keyword)) || 
                    (u.account != null && u.account == keyword)
                ).ToList();

                foreach (var u in users)
                {
                    Results.Add(new UserViewModel
                    {
                        User = u,
                        Orders = u.orders
                    });
                }

                ResultsTitle.Text = $"搜索结果: '{keyword}' (找到 {Results.Count} 位用户)";
                EmptyStateText.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
            }
        }

        private async void OnGiftCheckClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var allOrders = await ApiService.GetCachedOrdersAsync();

                var rawTags = allOrders.Select(o => o.tag).Distinct().ToList();
                var tags = rawTags.Select(t => string.IsNullOrWhiteSpace(t) ? "无标签" : t).Distinct().OrderBy(t => t).ToList();

                var comboBox = new ComboBox
                {
                    Header = "选择统计批次",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = tags
                };
                if (tags.Count > 0) comboBox.SelectedIndex = 0;

                var dialog = new ContentDialog
                {
                    Title = "统计满赠人数",
                    Content = comboBox,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary || comboBox.SelectedItem == null)
                {
                    return;
                }

                var selectedTag = comboBox.SelectedItem as string;

                Results.Clear();

                // 1. Fetch all users from API
                var users = await ApiService.GetCachedUsersAsync();

                var qualifiedUsers = new List<UserViewModel>();
                var 档位统计 = new Dictionary<int, int>(); // <Count, NumUsers>

                foreach (var u in users)
                {
                    var totalCount = 0;
                    foreach (var order in u.orders.Where(x => x.tag == selectedTag))
                    {
                        if (order.productModel.Contains("99满赠"))
                        {
                            var index = Array.IndexOf(order.productModel, "99满赠");
                            if (index >= 0 && index < order.productCount.Length)
                            {
                                totalCount += order.productCount[index];
                            }
                        }
                    }

                    if (totalCount > 0)
                    {
                        qualifiedUsers.Add(new UserViewModel
                        {
                            User = u,
                            Orders = u.orders.OrderBy(x => x.orderedAt).ToList()
                        });

                        档位统计.TryAdd(totalCount, 0);
                        档位统计[totalCount]++;
                    }
                }
// ... title update logic remains same ...

                // 3. Update ResultsTitle with Statistics
                var statsList = 档位统计.OrderBy(x => x.Key)
                    .Select(x => $"99x{x.Key}: {x.Value}人")
                    .ToList();

                var title = $"满赠统计【{selectedTag}】";

                if (statsList.Count > 0)
                {
                    ResultsTitle.Text = $"{title} (共 {qualifiedUsers.Count} 人): " + string.Join("; ", statsList);
                }
                else
                {
                    ResultsTitle.Text = $"{title}: 无符合条件用户";
                }


                // 4. Update List
                if (qualifiedUsers.Count == 0)
                {
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "暂无满足满赠条件的用户。";
                }
                else
                {
                    EmptyStateText.Visibility = Visibility.Collapsed;
                    foreach (var vm in qualifiedUsers)
                    {
                        Results.Add(vm);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                ShowPlaceholder("错误", $"统计失败: {ex.Message}");
            }
        }

        private async void OnSetGiftStatsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var allOrders = await ApiService.GetCachedOrdersAsync();

                // 1. Get Distinct Tags
                var tags = allOrders.Select(o => o.tag).Distinct().Where(t => t != null && t != "").ToList();

                var comboBox = new ComboBox
                {
                    Header = "选择统计批次",
                    ItemsSource = tags,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SelectedIndex = tags.Count > 0 ? 0 : -1
                };

                var dialog = new ContentDialog
                {
                    Title = "统计Set赠人数",
                    Content = comboBox,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var selectedTag = comboBox.SelectedItem as string;
                if (string.IsNullOrEmpty(selectedTag))
                {
                    return;
                }

                Results.Clear();

                var users = (await ApiService.GetCachedUsersAsync())
                    .Where(u => u.orders.Any(o => o.tag == selectedTag))
                    .ToList();

                var qualifiedUsers = new List<UserViewModel>();
                var 档位统计 = new Dictionary<int, int>();

                foreach (var u in users)
                {
                    var validOrders = u.orders.Where(o => o.tag == selectedTag).ToList();
                    int totalCount = 0;

                    foreach (var order in validOrders)
                    {
                        for (int i = 0; i < order.productCount.Length; i++)
                        {
                            if (order.productModel[i] == "Set赠")
                            {
                                totalCount += order.productCount[i];
                            }
                        }
                    }

                    if (totalCount > 0)
                    {
                        qualifiedUsers.Add(new UserViewModel
                        {
                            User = u,
                            Orders = u.orders.OrderBy(x => x.orderedAt).ToList()
                        });

                        档位统计.TryAdd(totalCount, 0);
                        档位统计[totalCount]++;
                    }
                }
// ... title update logic remains same ...

                // 3. Update ResultsTitle with Statistics
                var statsList = 档位统计.OrderBy(x => x.Key)
                    .Select(x => $"Set赠x{x.Key}: {x.Value}人")
                    .ToList();

                var title = $"Set赠统计【{selectedTag}】";

                if (statsList.Count > 0)
                {
                    ResultsTitle.Text = $"{title} (共 {qualifiedUsers.Count} 人): " + string.Join("; ", statsList);
                }
                else
                {
                    ResultsTitle.Text = $"{title}: 无符合条件用户";
                }

                // 4. Update List
                if (qualifiedUsers.Count == 0)
                {
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "暂无满足Set赠条件的用户。";
                }
                else
                {
                    EmptyStateText.Visibility = Visibility.Collapsed;
                    foreach (var vm in qualifiedUsers)
                    {
                        Results.Add(vm);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                ShowPlaceholder("错误", $"统计失败: {ex.Message}");
            }
        }

        private async void OnClearDataClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (await DialogHelper.ShowConfirmationDialogAsync(XamlRoot, "危险操作确认", "确定要彻底清空所有数据吗？\n此操作将删除所有用户和订单数据，且无法恢复！"))
                {
                    await ApiService.ClearDataAsync();

                    Results.Clear();
                    ResultsTitle.Text = "数据已清空";
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "数据库已重置。";

                    await DialogHelper.ShowErrorDialogAsync(XamlRoot, "数据已成功清空。"); // Re-using ErrorDialog for simple message
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                await DialogHelper.ShowErrorDialogAsync(XamlRoot, "清空失败: " + ex.Message);
            }
        }

        private async void OnExportExcelClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Ask User: Export Current Results or All Data?
                var dialog = new ContentDialog
                {
                    Title = "导出 Excel",
                    Content = "请选择导出范围：",
                    PrimaryButtonText = "导出当前查询结果",
                    SecondaryButtonText = "导出所有数据",
                    CloseButtonText = "取消",
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.None) return;

                bool exportAll = result == ContentDialogResult.Secondary;

                // 2. Prepare Data
                List<ExportRecord> dataToExport = new();

                if (exportAll)
                {
                    var users = await ApiService.GetCachedUsersAsync();

                    foreach (var user in users)
                    {
                        dataToExport.AddRange(ExportRecord.GetExportRecordList(user, user.orders));
                    }
                }
                else
                {
                    // Export Current Results (Results Collection)
                    if (Results.Count == 0)
                    {
                        await DialogHelper.ShowErrorDialogAsync(XamlRoot, "当前没有查询结果可导出。");
                        return;
                    }

                    foreach (var vm in Results)
                    {
                        dataToExport.AddRange(ExportRecord.GetExportRecordList(vm.User, vm.Orders));
                    }
                }

                if (dataToExport.Count == 0)
                {
                    await DialogHelper.ShowMessageDialogAsync(XamlRoot, "提示", "没有数据可导出。");
                    return;
                }

                // Merge Cells (Visual) - Enable for Direct Export
                ExportRecord.ProcessMerge(dataToExport);

                // 3. Save File Picker
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.Desktop;
                picker.FileTypeChoices.Add("Excel Workbook", new List<string> { ".xlsx" });
                picker.SuggestedFileName = $"导出数据_{DateTime.Now:yyyyMMdd_HHmmss}";

                // WinUI 3 requires Window Handle for Picker
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    // 1. Generate to a standard Temp file first (avoids WinUI stream issues)
                    string tempFile = Path.GetTempFileName() + ".xlsx";

                    // No Template - Direct Export as requested
                    // This exports 'dataToExport' directly. Visual Merging (nulls) is preserved.
                    await MiniExcel.SaveAsAsync(tempFile, dataToExport);

                    // 2. Copy Temp file to User Selected File
                    using (var sourceStream = File.OpenRead(tempFile))
                    using (var targetStream = await file.OpenStreamForWriteAsync())
                    {
                        targetStream.SetLength(0); // Truncate existing content
                        await sourceStream.CopyToAsync(targetStream);
                    }

                    // 3. Cleanup
                    if (File.Exists(tempFile)) File.Delete(tempFile);

                    await DialogHelper.ShowMessageDialogAsync(XamlRoot, "成功", "导出成功！");
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                await DialogHelper.ShowErrorDialogAsync(XamlRoot, "导出失败: " + ex.Message);
            }
        }


        private async void OnBotCheckClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Get Start Time from User
                var datePicker = new CalendarDatePicker
                {
                    Header = "开拍日期",
                    Date = DateTimeOffset.Now,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var timePicker = new TimePicker
                {
                    Header = "开拍时间",
                    Time = DateTime.Now.TimeOfDay,
                    ClockIdentifier = "24HourClock"
                };

                var stackPanel = new StackPanel
                {
                    Spacing = 12,
                    Children = { datePicker, timePicker }
                };

                var dialog = new ContentDialog
                {
                    Title = "设置开拍时间",
                    Content = stackPanel,
                    PrimaryButtonText = "开始筛查",
                    CloseButtonText = "取消",
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                // Combine Date and Time
                if (datePicker.Date == null)
                {
                    await DialogHelper.ShowErrorDialogAsync(this.XamlRoot, "请选择日期。");
                    return;
                }

                var selectedDate = datePicker.Date.Value.Date;
                var selectedTime = timePicker.Time;
                var startTime = selectedDate + selectedTime;

                var endTime = startTime.AddSeconds(3);

                var users = await ApiService.GetCachedUsersAsync();
                
                // 3. Filter Logic: Orders between startTime and startTime + 3s
                var botOrders = new List<UserViewModel>();

                foreach (var u in users)
                {
                    // Filter orders for this user
                    var suspiciousOrders = u.orders
                        .Where(o => o.orderedAt >= startTime && o.orderedAt <= endTime)
                        .ToList();

                    if (suspiciousOrders.Count > 0)
                    {
                        botOrders.Add(new UserViewModel
                        {
                            User = u,
                            Orders = suspiciousOrders
                        });
                    }
                }

                // 4. Update ResultsTitle
                if (botOrders.Count > 0)
                {
                    ResultsTitle.Text = $"机器人筛查 ({startTime:HH:mm:ss} - {endTime:HH:mm:ss}, 发现 {botOrders.Count} 位疑似用户)";
                }
                else
                {
                    ResultsTitle.Text = $"机器人筛查: 未发现符合条件 ({startTime:HH:mm:ss} - {endTime:HH:mm:ss}) 的订单";
                }

                // 5. Update List
                if (botOrders.Count == 0)
                {
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "未发现在此时间段内的订单。";
                }
                else
                {
                    EmptyStateText.Visibility = Visibility.Collapsed;
                    foreach (var vm in botOrders)
                    {
                        Results.Add(vm);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
            }
        }


        private void ShowPlaceholder(string title, string status)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            ResultsTitle.Text = title;
            Results.Clear();
        }

        private async void OnSinglePostageClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is order order)
            {
                try
                {
                    order.sended = true;
                    await ApiService.SaveOrderAsync(order);

                    // Reload data to reflect changes
                    // Check if we are currently filtering by unsent
                    bool isUnsentFilter = ResultsTitle.Text.Contains("未补邮");
                    LoadInitialData(isUnsentFilter);
                }
                catch (Exception ex)
                {
                    DebugConsole.Log(ex);
                    await DialogHelper.ShowErrorDialogAsync(this.XamlRoot, "更新状态失败: " + ex.Message);
                }
            }
        }

        private async void OnOverLimitCheckClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var allOrders = await ApiService.GetCachedOrdersAsync();

                // 1. Get Distinct Product Models (Client-side evaluation)
                var products = allOrders
                    .SelectMany(o => o.productName)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToList();

                // 2. Build Dialog UI
                // Start Time
                var startDatePicker = new CalendarDatePicker
                {
                    Header = "开始日期",
                    Date = DateTimeOffset.Now.AddDays(-7),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var startTimePicker = new TimePicker
                {
                    Header = "开始时间",
                    Time = new TimeSpan(0, 0, 0),
                    ClockIdentifier = "24HourClock"
                };

                // End Time
                var endDatePicker = new CalendarDatePicker
                {
                    Header = "结束日期",
                    Date = DateTimeOffset.Now,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var endTimePicker = new TimePicker
                {
                    Header = "结束时间",
                    Time = new TimeSpan(23, 59, 59),
                    ClockIdentifier = "24HourClock"
                };

                // Product Selection
                var productComboBox = new ComboBox
                {
                    Header = "选择商品",
                    ItemsSource = products,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsEditable = true
                };

                // Limit Quantity
                var limitBox = new NumberBox
                {
                    Header = "限制数量 (大于此数量)",
                    Value = 1,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Minimum = 0,
                    SmallChange = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var stackPanel = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "时间范围", FontWeight = FontWeights.Bold },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { startDatePicker, startTimePicker } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { endDatePicker, endTimePicker } },
                        new Rectangle { Height = 1, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray) },
                        productComboBox,
                        limitBox
                    }
                };

                var dialog = new ContentDialog
                {
                    Title = "超限查询",
                    Content = stackPanel,
                    PrimaryButtonText = "查询",
                    CloseButtonText = "取消",
                    XamlRoot = XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                // Validation
                if (startDatePicker.Date == null || endDatePicker.Date == null)
                {
                    await DialogHelper.ShowErrorDialogAsync(XamlRoot, "请选择有效的时间段。");
                    return;
                }

                if (productComboBox.SelectedItem == null && string.IsNullOrWhiteSpace(productComboBox.Text))
                {
                    await DialogHelper.ShowErrorDialogAsync(XamlRoot, "请选择或输入商品名称。");
                    return;
                }

                var start = startDatePicker.Date.Value.Date + startTimePicker.Time;
                var end = endDatePicker.Date.Value.Date + endTimePicker.Time;
                var targetProduct = productComboBox.SelectedItem as string ?? productComboBox.Text;
                var limit = (int)limitBox.Value;

                Results.Clear();

                var users = await ApiService.GetCachedUsersAsync();

                var qualifiedUsers = new List<UserViewModel>();

                foreach (var u in users)
                {
                    // Filter orders within time match
                    var relevantOrders = u.orders
                        .Where(o => o.orderedAt >= start && o.orderedAt <= end)
                        .ToList();

                    if (relevantOrders.Count == 0) continue;

                    var totalCount = 0;
                    foreach (var order in relevantOrders)
                    {
                        for (var i = 0; i < order.productCount.Length; i++)
                        {
                            if (order.productName[i] == targetProduct)
                            {
                                totalCount += order.productCount[i];
                            }
                        }
                    }

                    if (totalCount > limit)
                    {
                        qualifiedUsers.Add(new UserViewModel
                        {
                            User = u,
                            Orders = relevantOrders // showing only relevant orders? Or all? Usually context helps, but maybe just relevant ones for verification
                        });
                    }
                }

                // 4. Update UI
                var title = $"超限查询: {targetProduct} > {limit}";
                if (qualifiedUsers.Count > 0)
                {
                    ResultsTitle.Text = $"{title} (共 {qualifiedUsers.Count} 人)";
                    EmptyStateText.Visibility = Visibility.Collapsed;
                    foreach (var vm in qualifiedUsers)
                    {
                        Results.Add(vm);
                    }
                }
                else
                {
                    ResultsTitle.Text = $"{title}: 无符合条件用户";
                    EmptyStateText.Visibility = Visibility.Visible;
                    EmptyStateText.Text = "未发现符合超限条件的用户。";
                }
            }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                await DialogHelper.ShowErrorDialogAsync(this.XamlRoot, "查询失败: " + ex.Message);
            }
        }
    }
}
