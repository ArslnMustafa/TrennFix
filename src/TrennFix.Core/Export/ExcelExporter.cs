using ClosedXML.Excel;
using TrennFix.Core.Models;

namespace TrennFix.Core.Export;

public static class ExcelExporter
{
    public static string PaymentText(PaymentMethod m) => m switch
    {
        PaymentMethod.Ueberweisung => "Überweisung",
        PaymentMethod.Lastschrift => "Lastschrift",
        PaymentMethod.BereitsBezahlt => "Bereits bezahlt",
        PaymentMethod.Kreditkarte => "Kreditkarte",
        PaymentMethod.Bar => "Bar",
        _ => "?",
    };

    public static string StatusText(InvoiceStatus s) => s switch
    {
        InvoiceStatus.Ok => "OK",
        InvoiceStatus.Pruefen => "Prüfen",
        InvoiceStatus.Duplikat => "Duplikat",
        InvoiceStatus.Bezahlt => "Bezahlt",
        _ => s.ToString(),
    };

    public static string SourceText(ExtractionSource s) => s switch
    {
        ExtractionSource.EInvoiceXml => "E-Rechnung",
        ExtractionSource.PdfText => "PDF",
        ExtractionSource.Ocr => "OCR",
        _ => "–",
    };

    public static void Export(IEnumerable<InvoiceRecord> records, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Rechnungen");
        string[] headers =
        [
            "Status", "Lieferant", "Dokument", "Rechnungsnr.", "Rechnungsdatum", "Fällig am", "Netto", "MwSt", "Brutto", "Währung",
            "Zahlungsart", "IBAN", "BIC", "Verwendungszweck", "Mandatsreferenz", "Skonto", "USt-IdNr.", "Quelle", "Format",
            "Mail erhalten", "Absender", "Betreff", "Datei", "Hinweise",
        ];
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

        var row = 2;
        foreach (var r in records)
        {
            var d = r.Data;
            var c = 1;
            ws.Cell(row, c++).Value = StatusText(r.Status);
            ws.Cell(row, c++).Value = r.SupplierName;
            ws.Cell(row, c++).Value = d.Kind.ToString();
            ws.Cell(row, c++).Value = d.InvoiceNumber ?? "";
            SetDate(ws.Cell(row, c++), d.InvoiceDate);
            SetDate(ws.Cell(row, c++), d.DueDate);
            SetMoney(ws.Cell(row, c++), d.NetAmount);
            SetMoney(ws.Cell(row, c++), d.TaxAmount);
            SetMoney(ws.Cell(row, c++), d.GrossAmount);
            ws.Cell(row, c++).Value = d.Currency;
            ws.Cell(row, c++).Value = PaymentText(d.PaymentMethod);
            ws.Cell(row, c++).Value = d.Iban is null ? "" : Extraction.GermanFormats.FormatIban(d.Iban);
            ws.Cell(row, c++).Value = d.Bic ?? "";
            ws.Cell(row, c++).Value = d.PaymentReference ?? "";
            ws.Cell(row, c++).Value = d.MandateReference ?? "";
            ws.Cell(row, c++).Value = d.DiscountTerms ?? "";
            ws.Cell(row, c++).Value = d.SellerVatId ?? "";
            ws.Cell(row, c++).Value = SourceText(d.Source);
            ws.Cell(row, c++).Value = d.FormatName ?? "";
            ws.Cell(row, c).Value = r.MailReceived.LocalDateTime;
            ws.Cell(row, c++).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
            ws.Cell(row, c++).Value = r.MailFrom;
            ws.Cell(row, c++).Value = r.MailSubject;
            var fileCell = ws.Cell(row, c++);
            fileCell.Value = Path.GetFileName(r.FilePath);
            if (File.Exists(r.FilePath)) fileCell.SetHyperlink(new XLHyperlink(new Uri(r.FilePath).AbsoluteUri));
            ws.Cell(row, c++).Value = string.Join(" | ", r.Warnings);

            if (r.Status is InvoiceStatus.Pruefen or InvoiceStatus.Duplikat)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
            row++;
        }

        var table = ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length).CreateTable("Rechnungen");
        table.Theme = XLTableTheme.TableStyleLight9;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(row, 200), 8, 60);
        wb.SaveAs(path);
    }

    private static void SetDate(IXLCell cell, DateOnly? d)
    {
        if (d is null) return;
        cell.Value = d.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "dd.MM.yyyy";
    }

    private static void SetMoney(IXLCell cell, decimal? m)
    {
        if (m is null) return;
        cell.Value = m.Value;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }
}
