using System.Globalization;
using TrennFix.Core.Models;
using TrennFix.Core.Pricing;

namespace TrennFix.Core.Scanning;

public sealed record PriceIncrease(string Description, decimal OldPrice, decimal NewPrice, DateOnly OldDate)
{
    public decimal Percent => OldPrice == 0 ? 0 : (NewPrice - OldPrice) / OldPrice * 100m;
}

public sealed record CheaperElsewhere(string Description, decimal NewPrice, decimal OtherPrice, string OtherSupplier, DateOnly OtherDate)
{
    public decimal Percent => NewPrice == 0 ? 0 : (NewPrice - OtherPrice) / NewPrice * 100m;
}

/// <summary>
/// Compares the positions of a new invoice with earlier purchases of the same product
/// (recognised by <see cref="ProductCatalog"/>, also when the name is written differently).
/// </summary>
public static class PriceCheck
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Only purchases from the last year count as "cheaper elsewhere".</summary>
    private static readonly int CompareDays = 365;

    public static (List<PriceIncrease> Increases, List<CheaperElsewhere> Cheaper) Check(
        ProductCatalog catalog, long supplierId, InvoiceData data, DateTimeOffset received, decimal thresholdPercent)
    {
        var increases = new List<PriceIncrease>();
        var cheaper = new List<CheaperElsewhere>();
        // Only positions that were read reliably may raise an alarm.
        if (!data.LinesVerified) return (increases, cheaper);
        var date = data.InvoiceDate ?? DateOnly.FromDateTime(received.LocalDateTime);
        var factor = 1 + thresholdPercent / 100m;

        foreach (var line in data.Lines.Where(l => l.IsPriceRelevant).DistinctBy(l => (l.ItemKey, l.Unit)))
        {
            var earlier = catalog.FindPurchases(supplierId, line).Where(p => p.EffectiveDate <= date).ToList();
            if (earlier.Count == 0) continue;
            var price = line.UnitPrice!.Value;

            var lastHere = earlier.LastOrDefault(p => p.SupplierId == supplierId);
            if (lastHere?.Line.UnitPrice is > 0 and var old && price > old * factor)
                increases.Add(new PriceIncrease(line.Description, old, price, lastHere.EffectiveDate));

            var bestOther = earlier
                .Where(p => p.SupplierId != supplierId && p.EffectiveDate >= date.AddDays(-CompareDays))
                .OrderBy(p => p.Line.UnitPrice).ThenByDescending(p => p.EffectiveDate)
                .FirstOrDefault();
            if (bestOther?.Line.UnitPrice is > 0 and var other && price > other * factor)
                cheaper.Add(new CheaperElsewhere(line.Description, price, other, bestOther.SupplierName, bestOther.EffectiveDate));
        }
        return (increases.OrderByDescending(p => p.Percent).ToList(), cheaper.OrderByDescending(c => c.Percent).ToList());
    }

    public static string DescribeIncreases(List<PriceIncrease> increases)
    {
        string One(PriceIncrease p) =>
            $"{p.Description}: {p.OldPrice.ToString("N2", De)} → {p.NewPrice.ToString("N2", De)} € (+{p.Percent.ToString("0.0", De)} %) seit {p.OldDate:dd.MM.yyyy}";
        return "Preiserhöhung: " + Join(increases.Select(One).ToList());
    }

    public static string DescribeCheaper(List<CheaperElsewhere> cheaper)
    {
        string One(CheaperElsewhere c) =>
            $"{c.Description}: bei {c.OtherSupplier} am {c.OtherDate:dd.MM.yyyy} {c.OtherPrice.ToString("N2", De)} € statt {c.NewPrice.ToString("N2", De)} € (−{c.Percent.ToString("0.0", De)} %)";
        return "Anderswo günstiger: " + Join(cheaper.Select(One).ToList());
    }

    private static string Join(List<string> parts) =>
        string.Join("; ", parts.Take(3)) + (parts.Count > 3 ? $" und {parts.Count - 3} weitere" : "");
}
