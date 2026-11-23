using System.Globalization;
using TrennFix.Core.Models;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Scanning;

public sealed record PriceIncrease(string Description, string? Unit, decimal OldPrice, decimal NewPrice, DateOnly? OldDate, string? OldInvoice)
{
    public decimal Percent => OldPrice == 0 ? 0 : (NewPrice - OldPrice) / OldPrice * 100m;
}

/// <summary>Compares the positions of a new invoice with the last purchase of the same item at the same supplier.</summary>
public static class PriceCheck
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static List<PriceIncrease> FindIncreases(InvoiceDatabase db, long supplierId, InvoiceData data, DateTimeOffset received, decimal thresholdPercent)
    {
        var result = new List<PriceIncrease>();
        // Only positions that were read reliably may raise an alarm.
        if (!data.LinesVerified) return result;
        var date = data.InvoiceDate ?? DateOnly.FromDateTime(received.LocalDateTime);

        foreach (var line in data.Lines.Where(l => l.IsPriceRelevant).DistinctBy(l => (l.ItemKey, l.Unit)))
        {
            if (db.FindLastPrice(supplierId, line.ItemKey, line.Unit, date, excludeInvoiceId: -1) is not { } last) continue;
            var newPrice = line.UnitPrice!.Value;
            if (last.Price > 0 && newPrice > last.Price * (1 + thresholdPercent / 100m))
                result.Add(new PriceIncrease(line.Description, line.Unit, last.Price, newPrice, last.Date, last.InvoiceNumber));
        }
        return result.OrderByDescending(p => p.Percent).ToList();
    }

    public static string Describe(List<PriceIncrease> increases)
    {
        string One(PriceIncrease p) =>
            $"{p.Description}: {p.OldPrice.ToString("N2", De)} → {p.NewPrice.ToString("N2", De)} € (+{p.Percent.ToString("0.0", De)} %)" +
            (p.OldDate is { } d ? $" seit {d:dd.MM.yyyy}" : "");
        var text = "Preiserhöhung: " + string.Join("; ", increases.Take(3).Select(One));
        if (increases.Count > 3) text += $" und {increases.Count - 3} weitere";
        return text;
    }
}
