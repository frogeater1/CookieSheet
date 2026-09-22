using System.Collections.Generic;
using System.Linq;
using CookieSheet.Refunds;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace CookieSheet.Helpers;

// Runs preserve per-product colors inside the existing single-line, ellipsized TextBlock.
public static class ProductRefundText
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.RegisterAttached(
        "Items", typeof(object), typeof(ProductRefundText), new PropertyMetadata(null, OnItemsChanged));

    public static object GetItems(DependencyObject target) => target.GetValue(ItemsProperty);
    public static void SetItems(DependencyObject target, object value) => target.SetValue(ItemsProperty, value);

    private static void OnItemsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) return;
        text.Inlines.Clear();
        ToolTipService.SetToolTip(text, null);
        if (args.NewValue is not IEnumerable<ProductRefundResult> source) return;
        var products = source.ToList();
        var toolTip = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
        for (var i = 0; i < products.Count; i++)
        {
            var product = products[i];
            if (i > 0) text.Inlines.Add(new Run { Text = "；" });
            AppendProduct(text, product);
            if (i > 0) toolTip.Inlines.Add(new LineBreak());
            AppendProduct(toolTip, product);
            if (product.Explanation.Length > 0)
            {
                toolTip.Inlines.Add(new LineBreak());
                toolTip.Inlines.Add(new Run { Text = product.Explanation });
            }
        }
        if (products.Count > 0) ToolTipService.SetToolTip(text, toolTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, string.Join("；", products.Select(p => p.DisplayText)));
    }

    private static void AppendProduct(TextBlock text, ProductRefundResult product)
    {
        var run = new Run { Text = $"{product.Name} × {product.Product.Quantity}" };
        if (product.Status == ProductRefundStatus.Full)
            run.Foreground = new SolidColorBrush(Colors.IndianRed);
        else if (product.Status == ProductRefundStatus.Partial)
            run.Foreground = new SolidColorBrush(Colors.DarkOrange);
        text.Inlines.Add(run);
        if (product.Annotation.Length > 0)
            text.Inlines.Add(new Run
            {
                Text = product.Annotation,
                Foreground = new SolidColorBrush(product.Status == ProductRefundStatus.Full ? Colors.IndianRed : Colors.DarkOrange)
            });
    }
}
