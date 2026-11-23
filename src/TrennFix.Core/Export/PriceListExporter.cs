using ClosedXML.Excel;
using TrennFix.Core.Pricing;

namespace TrennFix.Core.Export;

/// <summary>
/// Excel price comparison. Only products that were bought more than once (and recognised as the
/// same product across invoices / suppliers) are listed:
/// "Preisvergleich" (one row per product), "Preiserhöhungen" and "Einkäufe" (the purchases behind each row).
/// </summary>
public static class PriceListExporter
{
    private const string Red = "#B3261E";
    private const string Green = "#1B6E20";

    public static void Export(IReadOnlyCollection<ProductGroup> groups, string path, decimal alertPercent)
    {
        var compared = groups.Where(g => g.PurchaseCount >= 2).ToList();
        using var wb = new XLWorkbook();

        // ---------------------------------------------------------------- comparison
        var ws = wb.AddWorksheet("Preisvergleich");
        string[] headers =
        [
            "Produkt", "Einheit", "Käufe", "Lieferanten",
            "Letzter Preis", "Lieferant (letzter)", "Datum (letzter)",
            "Preis vor letzter Änderung", "Lieferant (vorher)", "Datum (vorher)", "Änderung %",
            "Günstigster Preis", "Lieferant (günstigster)", "Datum (günstigster)", "Letzter über günstigstem %",
            "Höchster Preis", "Menge gesamt", "Betrag gesamt", "Unsicher",
        ];
        WriteHeader(ws, headers);
        var row = 2;
        foreach (var g in compared)
        {
            var c = 1;
            ws.Cell(row, c++).Value = g.Name;
            ws.Cell(row, c++).Value = g.Unit ?? "";
            ws.Cell(row, c++).Value = g.PurchaseCount;
            ws.Cell(row, c++).Value = g.SupplierCount;
            Money(ws.Cell(row, c++), g.LastPrice);
            ws.Cell(row, c++).Value = g.Last.SupplierName;
            Date(ws.Cell(row, c++), g.Last.EffectiveDate);
            Money(ws.Cell(row, c++), g.Previous?.Line.UnitPrice);
            ws.Cell(row, c++).Value = g.Previous?.SupplierName ?? "";
            Date(ws.Cell(row, c++), g.Previous?.EffectiveDate);
            Percent(ws.Cell(row, c++), g.ChangePercent, alertPercent);
            Money(ws.Cell(row, c++), g.Cheapest.Line.UnitPrice);
            ws.Cell(row, c++).Value = g.Cheapest.SupplierName;
            Date(ws.Cell(row, c++), g.Cheapest.EffectiveDate);
            Percent(ws.Cell(row, c++), g.AboveCheapestPercent, alertPercent);
            Money(ws.Cell(row, c++), g.MostExpensive.Line.UnitPrice);
            ws.Cell(row, c++).Value = g.TotalQuantity;
            Money(ws.Cell(row, c++), g.TotalAmount);
            ws.Cell(row, c++).Value = g.Uncertain ? "ja" : "";
            row++;
        }
        Finish(ws, row, headers.Length, "Preisvergleich");

        // ---------------------------------------------------------------- increases
        var inc = wb.AddWorksheet("Preiserhöhungen");
        string[] incHeaders =
        [
            "Produkt", "Einheit", "Vorher", "Lieferant (vorher)", "Datum (vorher)", "Jetzt", "Lieferant (jetzt)", "Datum (jetzt)",
            "Änderung %", "Günstigster Preis", "Lieferant (günstigster)",
        ];
        WriteHeader(inc, incHeaders);
        row = 2;
        foreach (var g in compared.Where(g => g.ChangePercent > alertPercent).OrderByDescending(g => g.ChangePercent))
        {
            var c = 1;
            inc.Cell(row, c++).Value = g.Name;
            inc.Cell(row, c++).Value = g.Unit ?? "";
            Money(inc.Cell(row, c++), g.Previous!.Line.UnitPrice);
            inc.Cell(row, c++).Value = g.Previous.SupplierName;
            Date(inc.Cell(row, c++), g.Previous.EffectiveDate);
            Money(inc.Cell(row, c++), g.LastPrice);
            inc.Cell(row, c++).Value = g.Last.SupplierName;
            Date(inc.Cell(row, c++), g.Last.EffectiveDate);
            Percent(inc.Cell(row, c++), g.ChangePercent, alertPercent);
            Money(inc.Cell(row, c++), g.Cheapest.Line.UnitPrice);
            inc.Cell(row, c++).Value = g.Cheapest.SupplierName;
            row++;
        }
        Finish(inc, row, incHeaders.Length, "Preiserhoehungen");

        // ---------------------------------------------------------------- purchases behind the comparison
        var det = wb.AddWorksheet("Einkäufe");
        string[] detHeaders = ["Produkt", "Datum", "Lieferant", "Rechnung", "Bezeichnung auf der Rechnung", "Art.-Nr.", "Menge", "Einheit", "Einzelpreis", "Gesamt"];
        WriteHeader(det, detHeaders);
        row = 2;
        foreach (var g in compared)
        {
            foreach (var p in g.Purchases.Reverse())
            {
                var c = 1;
                det.Cell(row, c++).Value = g.Name;
                Date(det.Cell(row, c++), p.EffectiveDate);
                det.Cell(row, c++).Value = p.SupplierName;
                det.Cell(row, c++).Value = p.InvoiceNumber ?? "";
                det.Cell(row, c++).Value = p.Line.Description;
                det.Cell(row, c++).Value = p.Line.ArticleNumber ?? "";
                if (p.Line.Quantity is { } q) det.Cell(row, c).Value = q;
                c++;
                det.Cell(row, c++).Value = p.Line.Unit ?? "";
                Money(det.Cell(row, c++), p.Line.UnitPrice);
                Money(det.Cell(row, c++), p.Line.LineTotal);
                row++;
            }
        }
        Finish(det, row, detHeaders.Length, "Einkaeufe");

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

    private static void Percent(IXLCell cell, decimal? pct, decimal alertPercent)
    {
        if (pct is not { } p) return;
        cell.Value = Math.Round(p, 1);
        cell.Style.NumberFormat.Format = "+0.0;-0.0;0.0";
        if (p > alertPercent) cell.Style.Font.FontColor = XLColor.FromHtml(Red);
        else if (p < 0) cell.Style.Font.FontColor = XLColor.FromHtml(Green);
    }

    private static void Money(IXLCell cell, decimal? value)
    {
        if (value is null) return;
        cell.Value = value.Value;
        cell.Style.NumberFormat.Format = value.Value != Math.Round(value.Value, 2) ? "#,##0.00##" : "#,##0.00";
    }

    private static void Date(IXLCell cell, DateOnly? d)
    {
        if (d is null) return;
        cell.Value = d.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "dd.MM.yyyy";
    }
}
