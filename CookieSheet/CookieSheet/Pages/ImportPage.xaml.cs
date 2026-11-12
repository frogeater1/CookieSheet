using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
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
                    await ApiService.RefreshCacheAsync();
                    // Work on a detached snapshot so cancelled/failed imports cannot alter the visible cache.
                    var allUsers = JsonSerializer.Deserialize<List<user>>(JsonSerializer.Serialize(await ApiService.GetCachedUsersAsync()))!;
                    var existingOrders = JsonSerializer.Deserialize<List<order>>(JsonSerializer.Serialize(await ApiService.GetCachedOrdersAsync()))!
                        .ToDictionary(o => o.orderId);
                    var usersById = allUsers.ToDictionary(u => u.userId);
                    foreach (var owner in allUsers) owner.orders.Clear();
                    foreach (var o in existingOrders.Values)
                        if (usersById.TryGetValue(o.userId, out var owner)) { o.user = owner; owner.orders.Add(o); }
                    var records = OrderImportReader.LatestByOrderId(OrderImportReader.Read(stream, file.Name));
                    var tag = Path.GetFileNameWithoutExtension(file.Name);
                    var closedOrderIds = records.Where(r => r.IsClosed && !RefundRules.IsPostageProduct(r.商品名称, r.商品型号))
                        .Select(r => r.订单编号).ToList();
                    var closedBatches = closedOrderIds.Where(existingOrders.ContainsKey).Select(id => existingOrders[id].tag).ToArray();
                    var repeatedIds = records.Where(r => !r.IsClosed && !RefundRules.IsPostageProduct(r.商品名称, r.商品型号))
                        .Select(r => r.订单编号).Where(existingOrders.ContainsKey).ToHashSet();
                    var movedCount = repeatedIds.Count(id => existingOrders[id].tag != tag);
                    var previousBatches = repeatedIds.Select(id => existingOrders[id].tag).ToArray();
                    var userAdded = 0;
                    var orderAdded = 0;

                    StatusTextBlock.Text = "正在解析并同步...";

                    var tempError = new List<string>();
                    var usersToUpdate = new List<user>();
                    var ordersToUpdate = new List<order>();
                    var postageOrdersToUpdate = new List<order>();

                    foreach (var r in records)
                    {
                        if (IsEmptyRecord(r) || r.IsClosed)
                        {
                            continue;
                        }

                        var isPostage = RefundRules.IsPostageProduct(r.商品名称, r.商品型号);
                        // Match globally by order ID before examining potentially changed user information.
                        if (!isPostage && existingOrders.TryGetValue(r.订单编号, out var existingOrder))
                        {
                            existingOrder.tag = tag;
                            existingOrder.price = decimal.Parse(r.实付金额);
                            existingOrder.receivableAmount = OrderAmountRules.ReadReceivable(r.应收金额);
                            existingOrder.productRefundAmount = OrderAmountRules.ReadProductRefund(r.商品已退款金额);
                            existingOrder.orderRefundStatus = r.退款状态.Trim();
                            if (!ordersToUpdate.Contains(existingOrder)) ordersToUpdate.Add(existingOrder);
                            continue;
                        }

                        if (isPostage && r.退款状态 == "退款完成")
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

                        qq = OrderImportReader.ResolveQq(r, qq);

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
                            foreach (var o in user.orders.Where(x => x.orderedAt < sendedTime && !repeatedIds.Contains(x.orderId)))
                            {
                                o.sended = true;
                                if (!postageOrdersToUpdate.Contains(o)) postageOrdersToUpdate.Add(o);
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
                                    price = decimal.Parse(r.实付金额),
                                    receivableAmount = OrderAmountRules.ReadReceivable(r.应收金额),
                                    productRefundAmount = OrderAmountRules.ReadProductRefund(r.商品已退款金额),
                                    orderRefundStatus = r.退款状态.Trim(),
                                    sended = false,
                                    tel = r.手机号,
                                    tag = Path.GetFileNameWithoutExtension(file.Name),
                                    user = user,
                                    userId = user.userId
                                };
                                user.orders.Add(order);
                                // Later rows in this file update this same order, even if
                                // the repeated row contains different contact information.
                                existingOrders.Add(order.orderId, order);
                                ordersToUpdate.Add(order);
                                orderAdded++;
                            }
                            else
                            {
                                order.price = decimal.Parse(r.实付金额);
                                order.receivableAmount = OrderAmountRules.ReadReceivable(r.应收金额);
                                order.productRefundAmount = OrderAmountRules.ReadProductRefund(r.商品已退款金额);
                                order.orderRefundStatus = r.退款状态.Trim();
                                if (!ordersToUpdate.Contains(order)) ordersToUpdate.Add(order);
                            }
                        }
                    }

                    StatusTextBlock.Text = "正在提交变更到服务器...";
                    
                    var gifts = new ApiService.GiftRecalculationResult(0, 0, 0);
                    if (ordersToUpdate.Count > 0 || postageOrdersToUpdate.Count > 0 || closedOrderIds.Count > 0)
                    {
                        var batches = previousBatches.Concat(closedBatches).Concat(postageOrdersToUpdate.Select(o => o.tag))
                            .Concat(ordersToUpdate.Count > 0 ? new[] { tag } : Array.Empty<string>());
                        var choices = await AskGiftChoicesAsync(batches);
                        gifts = await ApiService.ImportOrdersAsync(tag, ordersToUpdate, usersToUpdate, postageOrdersToUpdate, choices, closedOrderIds);
                    }
                    await ApiService.RefreshCacheAsync();


                    StatusTextBlock.Text = $"导入成功！新增用户: {userAdded}，新增订单: {orderAdded}，更新已有订单: {repeatedIds.Count} 笔，其中 {movedCount} 笔已移入批次「{tag}」。"
                        + $"\n已跳过 {closedOrderIds.Count} 笔关闭商品订单；库中对应旧订单已删除，金额、商品数量和赠品已重新计算。"
                        + "\n" + GiftSummary(gifts);

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
                await ApiService.RefreshCacheAsync();
                var refundOrderIds = result.Records.Select(r => r.orderId).ToHashSet(StringComparer.Ordinal);
                var batches = (await ApiService.GetCachedOrdersAsync()).Where(o => refundOrderIds.Contains(o.orderId)).Select(o => o.tag);
                var choices = await AskGiftChoicesAsync(batches);
                StatusTextBlock.Text = $"正在保存 {result.Records.Count} 条退款记录…";
                var summary = await ApiService.SaveRefundsBatchAsync(result.Records, choices);
                saved = true;
                await ApiService.RefreshCacheAsync();
                var orders = (await ApiService.GetCachedOrdersAsync()).ToDictionary(o => o.orderId);
                var activeIds = (await ApiService.GetCachedRefundsAsync()).Select(r => r.refundId).ToHashSet();
                var activeRecords = result.Records.Where(r => activeIds.Contains(r.refundId)).ToList();
                var matchable = activeRecords.Where(r => !RefundRules.IsPostageProduct(r.productName, r.productModel)).ToList();
                var postage = activeRecords.Count - matchable.Count;
                var unmatched = matchable.Count(r => !orders.ContainsKey(r.orderId));
                var unmatchedProduct = matchable.Count(r => orders.TryGetValue(r.orderId, out var order)
                    && !order.GetOrderedProducts().Any(p => RefundCalculator.Matches(p, r)));
                var affected = matchable.Select(r => r.orderId).ToHashSet();
                var review = orders.Values.Where(o => affected.Contains(o.orderId))
                    .Sum(o => o.ProductRefunds.Count(p => p.Status == ProductRefundStatus.NeedsReview));
                var awaiting = orders.Values.Where(o => affected.Contains(o.orderId))
                    .Sum(o => o.ProductRefunds.Count(p => p.Status == ProductRefundStatus.AwaitingDetails));
                var invalid = orders.Values.Where(o => affected.Contains(o.orderId))
                    .Sum(o => o.ProductRefunds.Count(p => p.Status == ProductRefundStatus.InvalidDetails));
                StatusTextBlock.Text = $"退款导入完成：新增 {summary.Added} 条，覆盖 {summary.Updated} 条。\n"
                    + $"跳过已删除退款 {summary.DeletedSkipped} 条（按退款编号识别，不会恢复）。\n"
                    + (summary.Unchanged > 0 ? $"服务器保留旧记录 {summary.Unchanged} 条；请升级服务器以支持顺序覆盖。\n" : "")
                    + $"跳过非成功退款 {result.SkippedRows + summary.Skipped} 条；文件内重复退款编号合并 {result.DuplicateRows} 条；补邮退款 {postage} 条不参与匹配。\n"
                    + $"等待原订单 {unmatched} 条，未匹配商品 {unmatchedProduct} 条，数量待核对 {review} 项，待导入退款明细 {awaiting} 项，退款明细有误 {invalid} 项（按未退款保留原件数发货）。\n"
                    + "成功退款记录已独立保存，已匹配商品的退款信息和已售出件数已更新；订单金额及其颜色仍以订单表为准。\n"
                    + GiftSummary(summary.Gifts);
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = saved ? $"退款已保存，但刷新失败：{ex.Message}。可点击查询页的“刷新数据”重试。" : $"退款导入失败：{ex.Message}";
                DebugConsole.Log(ex.ToString());
            }
            finally { SetImportEnabled(true); }
        }

        private async Task<List<BatchGiftChoice>> AskGiftChoicesAsync(IEnumerable<string> batches)
        {
            var choices = new List<BatchGiftChoice>();
            foreach (var batch in batches.Distinct(StringComparer.Ordinal).OrderBy(b => b, StringComparer.Ordinal))
            {
                StatusTextBlock.Text = $"请确认批次「{batch}」本次是否计算赠品。";
                var full = await DialogHelper.ShowConfirmationDialogAsync(XamlRoot, "是否计算满赠",
                    $"批次：{batch}\n是否重新计算满赠？\n选“是”：按当前实付总额重新计算。\n选“否”：清除该批次旧满赠。\n本次选择会覆盖此前选择。");
                var set = await DialogHelper.ShowConfirmationDialogAsync(XamlRoot, "是否计算 Set赠",
                    $"批次：{batch}\n是否重新计算 Set赠？\n选“是”：按扣除退款后的件数重新计算；退款件数不明时暂不生成，并提示待核对。\n选“否”：清除该批次旧 Set赠。\n本次选择会覆盖此前选择。");
                choices.Add(new() { Batch = batch, FullGift = full, SetGift = set });
            }
            return choices;
        }

        private static string GiftSummary(ApiService.GiftRecalculationResult? gifts)
        {
            if (gifts == null || gifts.Orders == 0) return "没有需要重算赠品的关联订单。";
            return $"已按本次选择更新 {gifts.Batches} 个相关批次、{gifts.Orders} 笔订单的赠品；未选择计算的赠品已清除。"
                + (gifts.PendingSetGroups > 0
                    ? $"\n其中 {gifts.PendingSetGroups} 个用户批次的退款件数待核对，已清除旧 Set赠并暂不生成；补齐退款明细后再次导入并选择计算。"
                    : "");
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

    }
}
