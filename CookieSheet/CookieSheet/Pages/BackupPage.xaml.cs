using System;
using System.Threading.Tasks;
using CookieSheet.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CookieSheet;

public sealed partial class BackupPage : Page
{
    private ApiService.BackupInfo? _latest;
    private bool _busy;

    public BackupPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SetBusy(true);
        try { await ReadLatestAsync(); }
        catch (Exception ex)
        {
            BackupDetails.Text = "暂时无法读取最后备份。重新进入“备份与恢复”可重试。";
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private static string Describe(ApiService.BackupInfo info) =>
        $"{info.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 用户 {info.Users:N0} 条 · 订单 {info.Orders:N0} 条 · 退款 {info.Refunds:N0} 条";

    private async Task ReadLatestAsync()
    {
        _latest = await ApiService.GetLatestBackupAsync();
        BackupDetails.Text = _latest == null ? "暂无备份。请点击“备份全部数据”。" : $"最后备份：{Describe(_latest)}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BackupButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && _latest != null;
        ClearButton.IsEnabled = !busy;
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage.Message = message;
        StatusMessage.Severity = severity;
        StatusMessage.IsOpen = true;
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        StatusMessage.IsOpen = false;
        try
        {
            _latest = await ApiService.CreateBackupAsync();
            BackupDetails.Text = $"最后备份：{Describe(_latest)}";
            ShowStatus("全部数据已备份。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            ShowStatus($"未能确认备份结果：{ex.Message} 请重新进入“备份与恢复”查看最后备份信息。", InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        StatusMessage.IsOpen = false;
        try
        {
            await ReadLatestAsync();
            if (_latest == null) return;
            var backup = _latest;
            var dialog = new ContentDialog
            {
                Title = "恢复最后备份",
                Content = new TextBlock
                {
                    Text = $"将恢复到：\n{Describe(backup)}\n\n当前全部用户、订单和退款会被覆盖，备份之后的新增、修改和删除将被撤销。此操作不会备份当前数据。确定恢复吗？",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "确认恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ApiService.RestoreBackupAsync(backup.Id);
            ShowStatus("全部数据已恢复到最后备份。", InfoBarSeverity.Success);
            try { await ApiService.RefreshCacheAsync(); }
            catch (Exception ex)
            {
                DebugConsole.Log(ex);
                ShowStatus("数据已恢复，但客户端刷新失败。请重新打开客户端加载最新数据。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            ShowStatus($"未能确认恢复结果：{ex.Message} 如连接中断，请刷新查询页面核实数据。", InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private async void OnClearDataClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        StatusMessage.IsOpen = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "清空全部数据",
                Content = new TextBlock
                {
                    Text = "确定清空当前服务器的全部数据吗？\n\n包括全部用户、所有批次订单、退款记录（含待匹配退款和删除标记）以及赠品计算选择。清空后，同编号退款可以再次导入。\n\n最后一份手动备份会保留；本次操作不会自动备份当前数据。",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "确认清空全部数据",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ApiService.ClearDataAsync();
            ShowStatus("全部用户、订单、退款及删除标记、赠品计算选择已清空，客户端缓存已清除。最后一份手动备份仍保留。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            DebugConsole.Log(ex);
            ShowStatus($"未能确认清空结果：{ex.Message} 如连接中断，请刷新查询页面核实数据。", InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }
}
