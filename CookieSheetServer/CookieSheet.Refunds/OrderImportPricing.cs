using System.Globalization;

namespace CookieSheet.Refunds;

public static class OrderImportPricing
{
    public static decimal[] Read(string? rawUnitPrices, string? rawShipping, decimal orderPaid, int[] quantities)
    {
        if (!string.IsNullOrWhiteSpace(rawUnitPrices))
        {
            var values = rawUnitPrices.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != quantities.Length) throw new FormatException("商品单价与商品件数的数量不一致。");
            var prices = values.Select(v => decimal.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (prices.Any(p => p < 0)) throw new FormatException("商品单价不能为负数。");
            return prices;
        }
        // Never divide an entire multi-product order's amount by a single product's quantity.
        if (quantities.Length == 1 && quantities[0] > 0
            && decimal.TryParse(rawShipping, CultureInfo.InvariantCulture, out var shipping)
            && shipping >= 0 && orderPaid >= shipping)
            return [(orderPaid - shipping) / quantities[0]];
        return new decimal[quantities.Length];
    }
}
