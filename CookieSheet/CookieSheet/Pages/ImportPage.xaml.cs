using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using CookieSheet.Data;
using CookieSheet.Helpers;
using CookieSheet.Refunds;

namespace CookieSheet
{
    public sealed partial class ImportPage : Page
    {
        public ImportPage()
        {
            this.InitializeComponent();
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            SetImportEnabled(false);
            try
            {
                StatusTextBlock.Text = "正在选择文件...";
                var picker = new Windows.Storage.Pickers.FileOpenPicker();

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".xls");
                picker.FileTypeFilter.Add(".xlsx");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    StatusTextBlock.Text = "正在读取文件...";
                    await using var stream = await file.OpenStreamForReadAsync();

                    // Fetch current state from server
                    var allUsers = await ApiService.GetCachedUsersAsync();
                    var userAdded = 0;
                    var orderAdded = 0;

                    StatusTextBlock.Text = "正在解析并同步...";

                    var tempError = new List<string>();
                    var usersToUpdate = new List<user>();
                    var ordersToUpdate = new List<order>();

                    foreach (var r in OrderImportReader.Read(stream, file.Name))
                    {
                        if (IsEmptyRecord(r))
                        {
                            continue;
                        }

                        var isPostage = RefundRules.IsPostageProduct(r.商品名称, r.商品型号);
                        if (isPostage && (r.退款状态 == "退款完成" || r.订单状态 == "已关闭"))
                        {
                            // Refunded regular orders must remain available for later refund reconciliation.
                            continue;
                        }

                        var qq = "";
                        var cn = "";
                        if (r.下单模板信息 != "")
                        {
                            var userInfo = r.下单模板信息.Split(';')
                                .Select(x => x.Split(':'))
                                .ToDictionary(x => x[0].Trim(), x => x[1].Trim());

                            qq = userInfo!.GetValueOrDefault("QQ", null) ?? userInfo.GetValueOrDefault("QQ号", "");
                            cn = userInfo!.GetValueOrDefault("cn", null) ?? userInfo.GetValueOrDefault("CN", "");
                        }

                        var user = qq != ""
                            ? allUsers.FirstOrDefault(x => x.qq == qq)
                            : allUsers.FirstOrDefault(x => x.tel == r.手机号);

                        if (user == null)
                        {
                            if (isPostage)
                            {
                                tempError.Add($"补邮订单{r.订单编号} 账号{r.下单账号} 填写的QQ{qq}不存在");
                                continue;
                            }

                            user = new user
                            {
                                qq = qq,
                                cn = cn,
                                receiver = r.收货人,
                                account = r.下单账号,
                                tel = r.手机号,
                                address = r.详细地址,
                                price = decimal.Parse(r.实付金额)
                            };
                            allUsers.Add(user);
                            usersToUpdate.Add(user);
                            userAdded++;
                        }

                        if (isPostage)
                        {
                            var sendedTime = ParseUtc8DateTime(r.发货时间);
                            foreach (var o in user.orders.Where(x => x.orderedAt < sendedTime))
                            {
                                o.sended = true;
                                if (!ordersToUpdate.Contains(o)) ordersToUpdate.Add(o);
                            }
                        }
                        else
                        {
                            var order = user.orders.FirstOrDefault(x => x.orderId == r.订单编号);
                            if (order == null)
                            {
                                order = new order
                                {
                                    orderId = r.订单编号,
                                    orderedAt = ParseUtc8DateTime(r.下单时间),
                                    productName = r.商品名称.Split(';'),
                                    productModel = (r.商品型号 == "" ? r.商品名称 : r.商品型号).Split(';'),
                                    productCount = ParseProductCounts(r.商品件数),
                                    productId = r.商品ID.Split(';'),
                                    productUnitPrice = OrderImportPricing.Read(r.商品单价, r.运费, decimal.Parse(r.实付金额), ParseProductCounts(r.商品件数)),
                                    price = decimal.Parse(r.实付金额),
                                    sended = false,
                                    tel = r.手机号,
                                    tag = Path.GetFileNameWithoutExtension(file.Name),
                                    user = user,
                                    userId = user.userId
                                };
                                user.orders.Add(order);
                                ordersToUpdate.Add(order);
                                orderAdded++;
                            }
                            else
                            {
                                order.orderedAt = ParseUtc8DateTime(r.下单时间);
                                order.productName = r.商品名称.Split(';');
                                order.productModel = (r.商品型号 == "" ? r.商品名称 : r.商品型号).Split(';');
                                order.productCount = ParseProductCounts(r.商品件数);
                                order.productId = r.商品ID.Split(';');
                                order.productUnitPrice = OrderImportPricing.Read(r.商品单价, r.运费, decimal.Parse(r.实付金额), order.productCount);
                                order.price = decimal.Parse(r.实付金额);
                                order.tel = r.手机号;
                                order.tag = Path.GetFileNameWithoutExtension(file.Name);
                                if (!ordersToUpdate.Contains(order)) ordersToUpdate.Add(order);
                            }
                        }
                    }

                    StatusTextBlock.Text = "正在提交变更到服务器...";
                    
                    if (usersToUpdate.Count > 0)
                    {
                        var newUsers = await ApiService.SaveUsersBatchAsync(usersToUpdate);
                        AssignImportedOrderUserIds(newUsers, ordersToUpdate);
                    }

                    if (ordersToUpdate.Count > 0) await ApiService.SaveOrdersBatchAsync(ordersToUpdate);

                    await ApiService.RefreshCacheAsync();

                    await Process满赠(Path.GetFileNameWithoutExtension(file.Name), await DialogHelper.ShowConfirmationDialogAsync(XamlRoot, "提示", "是否要计算满赠？"));
                    
                    await ProcessSet赠(Path.GetFileNameWithoutExtension(file.Name), await DialogHelper.ShowConfirmationDialogAsync(XamlRoot, "提示", "是否要计算Set赠？"));
                    await ApiService.RefreshCacheAsync();


                    StatusTextBlock.Text = $"导入成功！新增/更新用户: {userAdded}, 新增订单: {orderAdded}。";

                    if (tempError.Count > 0)
                    {
                        await DialogHelper.ShowErrorDialogAsync(XamlRoot, string.Join("; ", tempError));
                    }
                }
                else
                {
                    StatusTextBlock.Text = "操作已取消。";
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"错误: {ex.Message}";
                DebugConsole.Log(ex.ToString());
            }
            finally { SetImportEnabled(true); }
        }

        private void SetImportEnabled(bool enabled)
        {
            OrderImportButton.IsEnabled = enabled;
            RefundImportButton.IsEnabled = enabled;
        }

        private async void OnRefundImportClick(object sender, RoutedEventArgs e)
        {
            SetImportEnabled(false);
            var saved = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow));
                picker.FileTypeFilter.Add(".xls");
                picker.FileTypeFilter.Add(".xlsx");
                var file = await picker.PickSingleFileAsync();
                if (file == null) { StatusTextBlock.Text = "操作已取消。"; return; }
                StatusTextBlock.Text = "正在读取退款表…";
                await using var stream = await file.OpenStreamForReadAsync();
                var result = await Task.Run(() => RefundImportReader.Read(stream, file.Name));
                if (result.Records.Count == 0)
                {
                    StatusTextBlock.Text = $"没有可导入的成功退款记录，已跳过 {result.SkippedRows} 条非成功记录。";
                    return;
                }
                StatusTextBlock.Text = $"正在保存 {result.Records.Count} 条退款记录…";
                var summary = await ApiService.SaveRefundsBatchAsync(result.Records);
                saved = true;
                await ApiService.RefreshCacheAsync();
                var orders = (await ApiService.GetCachedOrdersAsync()).ToDictionary(o => o.orderId);
                var matchable = result.Records.Where(r => !RefundRules.IsPostageProduct(r.productName, r.productModel)).ToList();
                var postage = result.Records.Count - matchable.Count;
                var unmatched = matchable.Count(r => !orders.ContainsKey(r.orderId));
                var unmatchedProduct = matchable.Count(r => orders.TryGetValue(r.orderId, out var order)
                    && !order.GetOrderedProducts().Any(p => RefundCalculator.Matches(p, r)));
                var affected = matchable.Select(r => r.orderId).ToHashSet();
                var review = orders.Values.Where(o => affected.Contains(o.orderId))
                    .Sum(o => o.ProductRefunds.Count(p => p.Status == ProductRefundStatus.NeedsReview));
                StatusTextBlock.Text = $"退款导入完成：新增 {summary.Added} 条，更新 {summary.Updated} 条，保留较新记录 {summary.Unchanged} 条。\n"
                    + $"跳过非成功退款 {result.SkippedRows + summary.Skipped} 条；文件内重复退款编号合并 {result.DuplicateRows} 条；补邮退款 {postage} 条不参与匹配。\n"
                    + $"等待原订单 {unmatched} 条，未匹配商品 {unmatchedProduct} 条，数量待核对 {review} 项。\n"
                    + "成功退款记录已独立保存；后续导入原订单会自动匹配。";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = saved ? $"退款已保存，但刷新失败：{ex.Message}。可点击查询页的“刷新数据”重试。" : $"退款导入失败：{ex.Message}";
                DebugConsole.Log(ex.ToString());
            }
            finally { SetImportEnabled(true); }
        }

        private async Task Process满赠(string tag, bool process)
        {
            var users = await ApiService.GetCachedUsersAsync();
            var ordersToUpdate = new List<order>();

            foreach (var user in users)
            {
                var orders = user.orders.Where(x => x.tag == tag).OrderBy(x => x.orderedAt).ToList();
                decimal totalAmount = 0;

                foreach (var order in orders)
                {
                    totalAmount += order.price;
                    if (RemoveProduct(order, "99满赠"))
                    {
                        if (!ordersToUpdate.Contains(order)) ordersToUpdate.Add(order);
                    }
                }

                if (process)
                {
                    int bonusCount = (int)(totalAmount / 99);

                    if (bonusCount > 0 && orders.Count > 0)
                    {
                        var lastOrder = orders.Last();
                        AddProduct(lastOrder, "99满赠", "99满赠", bonusCount);
                        if (!ordersToUpdate.Contains(lastOrder)) ordersToUpdate.Add(lastOrder);
                    }
                }
            }

            if (ordersToUpdate.Count > 0) await ApiService.SaveOrdersBatchAsync(ordersToUpdate);
        }

        private bool RemoveProduct(order order, string targetName)
        {
            bool changed = false;
            if (order.productName.Contains(targetName) || order.productModel.Contains(targetName))
            {
                var nameList = order.productName.ToList();
                var modelList = order.productModel.ToList();
                var countList = order.productCount.ToList();
                var priceList = Enumerable.Range(0, nameList.Count).Select(i => order.productUnitPrice.ElementAtOrDefault(i)).ToList();
                var idList = Enumerable.Range(0, nameList.Count).Select(i => order.productId.ElementAtOrDefault(i) ?? "").ToList();

                for (int i = nameList.Count - 1; i >= 0; i--)
                {
                    if ((i < nameList.Count && nameList[i] == targetName) || (i < modelList.Count && modelList[i] == targetName))
                    {
                        if (i < nameList.Count) nameList.RemoveAt(i);
                        if (i < modelList.Count) modelList.RemoveAt(i);
                        if (i < countList.Count) countList.RemoveAt(i);
                        priceList.RemoveAt(i);
                        idList.RemoveAt(i);
                        changed = true;
                    }
                }

                order.productName = nameList.ToArray();
                order.productModel = modelList.ToArray();
                order.productCount = countList.ToArray();
                order.productUnitPrice = priceList.ToArray();
                order.productId = idList.ToArray();
            }
            return changed;
        }

        private void AddProduct(order order, string name, string model, int count)
        {
            var nameList = order.productName.ToList();
            var modelList = order.productModel.ToList();
            var countList = order.productCount.ToList();
            var priceList = Enumerable.Range(0, nameList.Count).Select(i => order.productUnitPrice.ElementAtOrDefault(i)).ToList();
            var idList = Enumerable.Range(0, nameList.Count).Select(i => order.productId.ElementAtOrDefault(i) ?? "").ToList();

            nameList.Add(name);
            modelList.Add(model);
            countList.Add(count);
            priceList.Add(0);
            idList.Add("");

            order.productName = nameList.ToArray();
            order.productModel = modelList.ToArray();
            order.productCount = countList.ToArray();
            order.productUnitPrice = priceList.ToArray();
            order.productId = idList.ToArray();
        }

        private async Task ProcessSet赠(string tag, bool process)
        {
            var allOrders = await ApiService.GetCachedOrdersAsync();
            var requiredProducts = allOrders
                .Where(o => o.tag == tag)
                .SelectMany(o => o.productModel)
                .Distinct()
                .Where(n => n != "99满赠" && n != "Set赠")
                .ToList();

            if (requiredProducts.Count == 0) return;

            var users = await ApiService.GetCachedUsersAsync();
            var ordersToUpdate = new List<order>();

            foreach (var user in users)
            {
                var userOrders = user.orders.Where(x => x.tag == tag).OrderBy(x => x.orderedAt).ToList();
                if (userOrders.Count == 0) continue;

                var productCounts = requiredProducts.ToDictionary(p => p, p => 0);

                foreach (var order in userOrders)
                {
                    if (RemoveProduct(order, "Set赠"))
                    {
                         if (!ordersToUpdate.Contains(order)) ordersToUpdate.Add(order);
                    }

                    for (int i = 0; i < order.productCount.Length; i++)
                    {
                        var name = order.productModel[i];
                        if (requiredProducts.Contains(name))
                        {
                            productCounts[name] += order.productCount[i];
                        }
                    }
                }

                if (process)
                {
                    var bonusCount = productCounts.Values.Min();
                    if (bonusCount > 0)
                    {
                        var lastOrder = userOrders.Last();
                        AddProduct(lastOrder, "Set赠", "Set赠", bonusCount);
                        if (!ordersToUpdate.Contains(lastOrder)) ordersToUpdate.Add(lastOrder);
                    }
                }
            }

            if (ordersToUpdate.Count > 0) await ApiService.SaveOrdersBatchAsync(ordersToUpdate);
        }

        private static int[] ParseProductCounts(string rawCounts)
        {
            if (string.IsNullOrWhiteSpace(rawCounts))
            {
                return [];
            }

            return rawCounts
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var count) ? count : 0)
                .ToArray();
        }

        private static DateTimeOffset ParseUtc8DateTime(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                throw new FormatException("日期不能为空。");
            }

            var utc8Offset = TimeSpan.FromHours(8);

            if (DateTimeOffset.TryParse(rawValue, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedOffset))
            {
                return parsedOffset.ToOffset(utc8Offset);
            }

            if (DateTime.TryParse(rawValue, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDateTime))
            {
                var unspecified = DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Unspecified);
                return new DateTimeOffset(unspecified, utc8Offset);
            }

            throw new FormatException($"无法解析日期: {rawValue}");
        }

        private static bool IsEmptyRecord(ImportRecord record)
        {
            return new[]
                {
                    record.订单编号,
                }
                .All(string.IsNullOrWhiteSpace);
        }

        private static void AssignImportedOrderUserIds(List<user> newUsers, List<order> ordersToUpdate)
        {
            if (newUsers.Count == 0 || ordersToUpdate.Count == 0)
            {
                return;
            }

            foreach (var importedOrder in ordersToUpdate.Where(order => order.user != null))
            {
                var newUser = !string.IsNullOrWhiteSpace(importedOrder.user.qq)
                    ? newUsers.FirstOrDefault(user => user.qq == importedOrder.user.qq)
                    : newUsers.FirstOrDefault(user => user.tel == importedOrder.user.tel);

                if (newUser != null)
                {
                    importedOrder.userId = newUser.userId;
                }
            }
        }
    }
}
