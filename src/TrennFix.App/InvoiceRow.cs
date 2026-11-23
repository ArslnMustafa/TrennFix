using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrennFix.Core.Export;
using TrennFix.Core.Extraction;
using TrennFix.Core.Models;

namespace TrennFix.App;

/// <summary>One line in the invoice list.</summary>
public sealed class InvoiceRow(InvoiceRecord record) : INotifyPropertyChanged
{
    public InvoiceRecord Record { get; private set; } = record;

    public void Update(InvoiceRecord r)
    {
        Record = r;
        OnPropertyChanged(string.Empty);
    }

    private InvoiceData D => Record.Data;

    public string Status => ExcelExporter.StatusText(Record.Status);
    public bool NeedsCheck => Record.Status is InvoiceStatus.Pruefen;
    public bool IsDuplicate => Record.Status is InvoiceStatus.Duplikat;
    public string Supplier => Record.SupplierName;
    public string Kind => D.Kind switch
    {
        DocumentKind.Rechnung => "Rechnung",
        DocumentKind.Gutschrift => "Gutschrift",
        DocumentKind.Mahnung => "Mahnung",
        DocumentKind.Sonstiges => "Sonstiges",
        _ => "?",
    };
    public string InvoiceNumber => D.InvoiceNumber ?? "";
    public DateTime? InvoiceDate => D.InvoiceDate?.ToDateTime(TimeOnly.MinValue);
    public DateTime? DueDate => D.DueDate?.ToDateTime(TimeOnly.MinValue);
    public bool IsOverdue => D.DueDate is { } due && due < DateOnly.FromDateTime(DateTime.Today)
                             && D.PaymentMethod == PaymentMethod.Ueberweisung && Record.Status != InvoiceStatus.Bezahlt;
    public decimal? Net => D.NetAmount;
    public decimal? Gross => D.GrossAmount;
    public string Currency => D.Currency;
    public string Payment => ExcelExporter.PaymentText(D.PaymentMethod);
    public string Iban => D.Iban is null ? "" : GermanFormats.FormatIban(D.Iban);
    public string Source => ExcelExporter.SourceText(D.Source);
    public string Format => D.FormatName ?? "";
    public string Printed => Record.Printed ? "✓" : "";
    public string Saved => Record.IsSaved ? "✓" : "–";
    public bool IsSavedRow => Record.IsSaved;
    /// <summary>"5 ✓" = 5 positions, sum matches the invoice total; "3 ?" = read, but please check.</summary>
    public string LinesText => D.Lines.Count == 0 ? "" : $"{D.Lines.Count} {(D.LinesVerified ? "✓" : "?")}";
    public IReadOnlyList<InvoiceLine> Lines => D.Lines;
    public DateTime Received => Record.MailReceived.LocalDateTime;
    public string Warnings => string.Join("  |  ", Record.Warnings);
    public string FileName => Path.GetFileName(Record.FilePath);
    public string ToolTipText =>
        $"{Record.MailSubject}\nVon: {Record.MailFrom}\nDatei: {(Record.IsSaved ? Record.FilePath : "noch nicht gespeichert (" + Record.OriginalFileName + ")")}" +
        (D.MandateReference is null ? "" : $"\nMandatsreferenz: {D.MandateReference}") +
        (D.DiscountTerms is null ? "" : $"\nSkonto: {D.DiscountTerms}") +
        (D.PaymentReference is null ? "" : $"\nVerwendungszweck: {D.PaymentReference}") +
        (D.SellerVatId is null ? "" : $"\nUSt-IdNr.: {D.SellerVatId}");

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var term = search.Trim();
        return new[] { Supplier, InvoiceNumber, Iban, Record.MailSubject, Record.MailFrom, Gross?.ToString("N2") ?? "", FileName }
                   .Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase))
               // also find invoices by product name / article number
               || D.Lines.Any(l => l.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || (l.ArticleNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
