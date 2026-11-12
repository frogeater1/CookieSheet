using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CookieSheet.Helpers;

public static class OrderDeletionDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root, string orderId)
    {
        var saved = false;
        try
        {
            var selected = (await ApiService.GetCachedOrdersAsync()).FirstOrDefault(o => o.orderId == orderId);
            if (selected == null)
            {
                await DialogHelper.ShowMessageDialogAsync(root, "订单已不存在", "请刷新数据。");
                return false;
            }
            var owner = selected.user;
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = "删除这笔订单？", PrimaryButtonText = "删除订单", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    MaxWidth = 440, TextWrapping = TextWrapping.Wrap,
                    Text = $"订单：{selected.orderId}\n批次：{selected.tag}\n用户：{owner?.cn ?? ""}（QQ：{owner?.qq ?? "未填写"}）\n金额：{selected.price:C}"
                        + "\n\n将删除这笔订单，并按该批次已选择的规则重新计算赠品。\n退款记录保留，改正 QQ 后重新导入可再次匹配。"
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            ApiService.OrderDeleteResult result;
            try { result = await ApiService.DeleteOrderAsync(selected); }
            catch (ApiService.GiftChoiceRequiredException)
            {
                var full = new ComboBox { Header = "是否计算满赠", PlaceholderText = "请选择", ItemsSource = new[] { "计算", "不计算" }, HorizontalAlignment = HorizontalAlignment.Stretch };
                var set = new ComboBox { Header = "是否计算 Set赠", PlaceholderText = "请选择", ItemsSource = new[] { "计算", "不计算" }, HorizontalAlignment = HorizontalAlignment.Stretch };
                var choices = new ContentDialog
                {
                    XamlRoot = root, Title = "选择本批次赠品选项", PrimaryButtonText = "确认并删除", CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
                    Content = new StackPanel { Spacing = 12, MinWidth = 300, Children =
                    {
                        new TextBlock { Text = $"批次：{selected.tag}\n尚未保存赠品选项。请选择后继续删除；选项将保存并用于本批次赠品重算。", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                        full, set
                    } }
                };
                void Validate(object sender, SelectionChangedEventArgs e) => choices.IsPrimaryButtonEnabled = full.SelectedIndex >= 0 && set.SelectedIndex >= 0;
                full.SelectionChanged += Validate;
                set.SelectionChanged += Validate;
                if (await choices.ShowAsync() != ContentDialogResult.Primary) return false;
                result = await ApiService.DeleteOrderAsync(selected, full.SelectedIndex == 0, set.SelectedIndex == 0);
            }
            saved = true;
            await ApiService.RefreshCacheAsync();
            if (result.Gifts.PendingSetGroups > 0)
                await DialogHelper.ShowMessageDialogAsync(root, "订单已删除",
                    $"相关赠品已重新计算，其中 {result.Gifts.PendingSetGroups} 个用户批次的退款件数不明，暂不生成 Set赠。");
            return true;
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorDialogAsync(root, saved
                ? $"订单已删除，但刷新失败。赠品已在服务器重算，请点击“刷新数据”获取最新结果。\n{ex.Message}"
                : $"删除未确认完成：{ex.Message}\n如遇网络中断，请先刷新确认结果。");
            return saved;
        }
    }
}
