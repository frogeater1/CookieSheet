using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MiniExcelLibs;
using System.Collections.Generic;
using System.Threading.Tasks;
using CookieSheet.Data;
using CookieSheet.Helpers;
using MiniExcelLibs.Attributes;

namespace CookieSheet
{
    public class ImportRecord
    {
        [ExcelColumnName("下单模板信息")] public string 下单模板信息 { get; set; } = "";
        [ExcelColumnName("收货人/提货人手机号")] public string 手机号 { get; set; } = "";
        [ExcelColumnName("收货人/提货人姓名")] public string 收货人 { get; set; } = "";
        [ExcelColumnName("下单账号")] public string 下单账号 { get; set; } = "";
        [ExcelColumnName("收货/提货详细地址")] public string 详细地址 { get; set; } = "";
        [ExcelColumnName("商品名称")] public string 商品名称 { get; set; } = "";
        [ExcelColumnName("商品型号")] public string 商品型号 { get; set; } = "";
        [ExcelColumnName("商品件数")] public string 商品件数 { get; set; } = "";
        [ExcelColumnName("实付金额")] public string 实付金额 { get; set; } = "";
        [ExcelColumnName("订单编号")] public string 订单编号 { get; set; } = "";
        [ExcelColumnName("下单时间")] public string 下单时间 { get; set; } = "";
        [ExcelColumnName("发货时间")] public string 发货时间 { get; set; } = "";
        [ExcelColumnName("退款状态")] public string 退款状态 { get; set; } = "";
        [ExcelColumnName("订单状态")] public string 订单状态 { get; set; } = "";
    }

    public sealed partial class ImportPage : Page
    {
        public ImportPage()
        {
            this.InitializeComponent();
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusTextBlock.Text = "正在选择文件...";
                var picker = new Windows.Storage.Pickers.FileOpenPicker();

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
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

                    foreach (var r in stream.Query<ImportRecord>())
                    {
                        if (IsEmptyRecord(r))
                        {
                            continue;
                        }

                        if (r.退款状态 == "退款完成" || r.订单状态 == "已关闭")
                        {
                            // In a real client-server app, we might need a DELETE endpoint.
                            // But for now, we just skip these.
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
                            if (r.商品名称 == "补邮")
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

                        if (r.商品名称 == "补邮")
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

                for (int i = nameList.Count - 1; i >= 0; i--)
                {
                    if ((i < nameList.Count && nameList[i] == targetName) || (i < modelList.Count && modelList[i] == targetName))
                    {
                        if (i < nameList.Count) nameList.RemoveAt(i);
                        if (i < modelList.Count) modelList.RemoveAt(i);
                        if (i < countList.Count) countList.RemoveAt(i);
                        changed = true;
                    }
                }

                order.productName = nameList.ToArray();
                order.productModel = modelList.ToArray();
                order.productCount = countList.ToArray();
            }
            return changed;
        }

        private void AddProduct(order order, string name, string model, int count)
        {
            var nameList = order.productName.ToList();
            var modelList = order.productModel.ToList();
            var countList = order.productCount.ToList();

            nameList.Add(name);
            modelList.Add(model);
            countList.Add(count);

            order.productName = nameList.ToArray();
            order.productModel = modelList.ToArray();
            order.productCount = countList.ToArray();
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
