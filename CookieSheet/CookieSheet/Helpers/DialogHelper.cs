using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CookieSheet.Helpers
{
    public static class DialogHelper
    {
        private static TextBox CreateSelectableTextBlock(string message)
        {
            var textBox = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

            return textBox;
        }

        public static async Task ShowErrorDialogAsync(XamlRoot xamlRoot, string message)
        {
            var dialog = new ContentDialog
            {
                Title = "错误",
                Content = CreateSelectableTextBlock(message),
                CloseButtonText = "确定",
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();
        }

        public static async Task<bool> ShowConfirmationDialogAsync(XamlRoot xamlRoot, string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = CreateSelectableTextBlock(message),
                PrimaryButtonText = "是",
                CloseButtonText = "否",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        public static async Task ShowMessageDialogAsync(XamlRoot xamlRoot, string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = CreateSelectableTextBlock(message),
                CloseButtonText = "确定",
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();
        }
    }
}