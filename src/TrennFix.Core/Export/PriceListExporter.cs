using ClosedXML.Excel;
using TrennFix.Core.Models;

namespace TrennFix.Core.Export;

/// <summary>One product of one supplier with its price development.</summary>
public sealed record PriceSummary(
    string Supplier,
    string? ArticleNumber,
    string? Ean,
    string Description,
    string? Unit,
    decimal LastPrice,
    DateOnly LastDate,
    string? LastInvoice,
    decimal? PreviousPrice,
    DateOnly? PreviousDate,
    decimal MinPrice,
    decimal MaxPrice,
    decimal AveragePrice,
    int Purchases,
    decimal TotalQuantity,
    decimal TotalAmount,
    bool Uncertain)
{
    public decimal? ChangePercent => PreviousPrice is > 0 ? (LastPrice - PreviousPrice.Value) / PreviousPrice.Value * 100m : null;
}

/// <summary>
/// Excel price list built from all invoice positions:
/// "Preisvergleich" (one row per product, last vs. previous price), "Preiserhöhungen" and "Preisverlauf" (every purchase).
/// </summary>
public static class PriceListExporter
{
    public static List<PriceSummary> Summarize(IEnumerable<PriceHistoryEntry> history) =>
        history
            .Where(h => h.Line.IsPriceRelevant)
            .GroupBy(h => (h.SupplierId, h.Line.ItemKey, h.Line.Unit))
            .Select(g =>
            {
                var ordered = g.OrderBy(h => h.EffectiveDate).ThenBy(h => h.InvoiceId).ToList();
                var last = ordered[^1];
                // previous purchase = last one before the latest date with a (possibly) different price
                var previous = ordered.Take(ordered.Count - 1).LastOrDefault();
                var prices = ordered.Select(h => h.Line.UnitPrice!.Value).ToList();
                return new PriceSummary(
                    last.SupplierName,
                    ordered.Select(h => h.Line.ArticleNumber).LastOrDefault(a => a is not null),
                    ordered.Select(h => h.Line.Ean).LastOrDefault(e => e is not null),
                    last.Line.Description,
                    last.Line.Unit,
                    last.Line.UnitPrice!.Value,
                    last.EffectiveDate,
                    last.InvoiceNumber,
                    previous?.Line.UnitPrice,
                    previous?.EffectiveDate,
                    prices.Min(),
                    prices.Max(),
                    Math.Round(prices.Average(), 4),
                    ordered.Count,
                    ordered.Sum(h => h.Line.Quantity ?? 0),
                    ordered.Sum(h => h.Line.LineTotal ?? 0),
                    ordered.Any(h => !h.Verified));
            })
            .OrderBy(s => s.Description, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Supplier, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static void Export(IReadOnlyCollection<PriceHistoryEntry> history, string path, decimal alertPercent)
    {
        var summaries = Summarize(history);
        using var wb = new XLWorkbook();

        // ---------------------------------------------------------------- comparison
        var ws = wb.AddWorksheet("Preisvergleich");
        string[] cmpHeaders =
        [
            "Bezeichnung", "Lieferant", "Art.-Nr.", "EAN", "Einheit", "Letzter Preis", "Datum", "Rechnung",
            "Vorheriger Preis", "Datum vorher", "Änderung %", "Min", "Max", "Ø Preis", "Käufe", "Menge gesamt", "Betrag gesamt", "Unsicher",
        ];
        WriteHeader(ws, cmpHeaders);
        var row = 2;
        foreach (var s in summaries)
        {
            var c = 1;
            ws.Cell(row, c++).Value = s.Description;
            ws.Cell(row, c++).Value = s.Supplier;
            ws.Cell(row, c++).Value = s.ArticleNumber ?? "";
            ws.Cell(row, c++).Value = s.Ean ?? "";
            ws.Cell(row, c++).Value = s.Unit ?? "";
            Money(ws.Cell(row, c++), s.LastPrice);
            Date(ws.Cell(row, c++), s.LastDate);
            ws.Cell(row, c++).Value = s.LastInvoice ?? "";
            Money(ws.Cell(row, c++), s.PreviousPrice);
            Date(ws.Cell(row, c++), s.PreviousDate);
            var changeCell = ws.Cell(row, c++);
            if (s.ChangePercent is { } pct)
            {
                changeCell.Value = Math.Round(pct, 1);
                changeCell.Style.NumberFormat.Format = "+0.0;-0.0;0.0";
                if (pct > alertPercent) changeCell.Style.Font.FontColor = XLColor.FromHtml("#B3261E");
                else if (pct < 0) changeCell.Style.Font.FontColor = XLColor.FromHtml("#1B6E20");
            }
            Money(ws.Cell(row, c++), s.MinPrice);
            Money(ws.Cell(row, c++), s.MaxPrice);
            Money(ws.Cell(row, c++), s.AveragePrice);
            ws.Cell(row, c++).Value = s.Purchases;
            ws.Cell(row, c++).Value = s.TotalQuantity;
            Money(ws.Cell(row, c++), s.TotalAmount);
            ws.Cell(row, c++).Value = s.Uncertain ? "ja" : "";
            row++;
        }
        Finish(ws, row, cmpHeaders.Length, "Preisvergleich");

        // ---------------------------------------------------------------- increases
        var inc = wb.AddWorksheet("Preiserhöhungen");
        string[] incHeaders = ["Bezeichnung", "Lieferant", "Einheit", "Vorher", "Datum vorher", "Jetzt", "Datum", "Rechnung", "Änderung %"];
        WriteHeader(inc, incHeaders);
        row = 2;
        foreach (var s in summaries.Where(s => s.ChangePercent > alertPercent).OrderByDescending(s => s.ChangePercent))
        {
            var c = 1;
            inc.Cell(row, c++).Value = s.Description;
            inc.Cell(row, c++).Value = s.Supplier;
            inc.Cell(row, c++).Value = s.Unit ?? "";
            Money(inc.Cell(row, c++), s.PreviousPrice);
            Date(inc.Cell(row, c++), s.PreviousDate);
            Money(inc.Cell(row, c++), s.LastPrice);
            Date(inc.Cell(row, c++), s.LastDate);
            inc.Cell(row, c++).Value = s.LastInvoice ?? "";
            inc.Cell(row, c).Value = Math.Round(s.ChangePercent!.Value, 1);
            inc.Cell(row, c).Style.NumberFormat.Format = "+0.0";
            inc.Cell(row, c).Style.Font.FontColor = XLColor.FromHtml("#B3261E");
            row++;
        }
        Finish(inc, row, incHeaders.Length, "Preiserhoehungen");

        // ---------------------------------------------------------------- history
        var hist = wb.AddWorksheet("Preisverlauf");
        string[] histHeaders =
            ["Datum", "Lieferant", "Rechnung", "Pos.", "Art.-Nr.", "EAN", "Bezeichnung", "Menge", "Einheit", "Einzelpreis", "Gesamt", "Positionen geprüft"];
        WriteHeader(hist, histHeaders);
        row = 2;
        foreach (var h in history.OrderByDescending(h => h.EffectiveDate).ThenBy(h => h.SupplierName).ThenBy(h => h.InvoiceId))
        {
            var c = 1;
            Date(hist.Cell(row, c++), h.EffectiveDate);
            hist.Cell(row, c++).Value = h.SupplierName;
            hist.Cell(row, c++).Value = h.InvoiceNumber ?? "";
            hist.Cell(row, c++).Value = h.Line.Position ?? "";
            hist.Cell(row, c++).Value = h.Line.ArticleNumber ?? "";
            hist.Cell(row, c++).Value = h.Line.Ean ?? "";
            hist.Cell(row, c++).Value = h.Line.Description;
            if (h.Line.Quantity is { } q) hist.Cell(row, c).Value = q;
            c++;
            hist.Cell(row, c++).Value = h.Line.Unit ?? "";
            Money(hist.Cell(row, c++), h.Line.UnitPrice);
            Money(hist.Cell(row, c++), h.Line.LineTotal);
            hist.Cell(row, c++).Value = h.Verified ? "ja" : "nein";
            row++;
        }
        Finish(hist, row, histHeaders.Length, "Preisverlauf");

        wb.SaveAs(path);
    }

    private static void WriteHeader(IXLWorksheet ws, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
    }

    private static void Finish(IXLWorksheet ws, int nextRow, int columns, string tableName)
    {
        var table = ws.Range(1, 1, Math.Max(nextRow - 1, 2), columns).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleLight9;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(nextRow, 300), 6, 60);
    }

    private static void Money(IXLCell cell, decimal? value)
    {
        if (value is null) return;
        cell.Value = value.Value;
        // up to 4 decimals for small unit prices (0,6120 €/m)
        cell.Style.NumberFormat.Format = value.Value != Math.Round(value.Value, 2) ? "#,##0.00##" : "#,##0.00";
    }

    private static void Date(IXLCell cell, DateOnly? d)
    {
        if (d is null) return;
        cell.Value = d.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "dd.MM.yyyy";
    }
}
