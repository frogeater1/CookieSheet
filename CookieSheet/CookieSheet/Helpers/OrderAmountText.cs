using CookieSheet.Refunds;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CookieSheet.Helpers;

public static class OrderAmountText
{
    public static readonly DependencyProperty RefundStatusProperty = DependencyProperty.RegisterAttached(
        "RefundStatus", typeof(string), typeof(OrderAmountText), new PropertyMetadata(null, OnStatusChanged));

    public static string GetRefundStatus(DependencyObject target) => (string)target.GetValue(RefundStatusProperty);
    public static void SetRefundStatus(DependencyObject target, string value) => target.SetValue(RefundStatusProperty, value);

    private static void OnStatusChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) return;
        switch (OrderAmountRules.Classify(args.NewValue as string))
        {
            case OrderAmountRefundStatus.Full:
                text.Foreground = new SolidColorBrush(Colors.IndianRed);
                break;
            case OrderAmountRefundStatus.Partial:
                text.Foreground = new SolidColorBrush(Colors.DarkOrange);
                break;
            default:
                // Clear the local color when a virtualized row is reused for an unrefunded order.
                text.ClearValue(TextBlock.ForegroundProperty);
                break;
        }
    }
}
