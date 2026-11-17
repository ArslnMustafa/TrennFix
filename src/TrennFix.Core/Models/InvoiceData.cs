namespace TrennFix.Core.Models;

/// <summary>How the invoice is expected to be paid.</summary>
public enum PaymentMethod
{
    Unbekannt = 0,
    /// <summary>We have to transfer the money ourselves (SEPA credit transfer / Überweisung).</summary>
    Ueberweisung = 1,
    /// <summary>The supplier collects the money (SEPA-Lastschrift) – nothing to do for us.</summary>
    Lastschrift = 2,
    /// <summary>Already paid (PayPal, Kreditkarte, bar, Vorkasse …).</summary>
    BereitsBezahlt = 3,
    Kreditkarte = 4,
    Bar = 5,
}

/// <summary>Where the extracted values came from – also a rough confidence indicator.</summary>
public enum ExtractionSource
{
    /// <summary>Structured XML (XRechnung / ZUGFeRD) – values are exact.</summary>
    EInvoiceXml = 0,
    /// <summary>Text layer of a normal (digital) PDF, values found with heuristics.</summary>
    PdfText = 1,
    /// <summary>Scanned PDF, text recognised via OCR, values found with heuristics.</summary>
    Ocr = 2,
    /// <summary>Nothing readable could be found.</summary>
    None = 3,
}

public enum DocumentKind
{
    Rechnung = 0,
    Gutschrift = 1,
    Unbekannt = 2,
    /// <summary>Mahnung / Zahlungserinnerung – refers to an existing invoice, do not pay twice.</summary>
    Mahnung = 3,
    /// <summary>Lieferschein, Auftragsbestätigung, AGB … – not an invoice.</summary>
    Sonstiges = 4,
}

/// <summary>
/// Everything we could read from one invoice document. All fields are optional because
/// heuristics (text / OCR) can miss values – <see cref="MissingFields"/> tells the UI what to check.
/// </summary>
public sealed class InvoiceData
{
    public string? InvoiceNumber { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }

    public string? SellerName { get; set; }
    public string? SellerVatId { get; set; }
    public string? SellerTaxNumber { get; set; }

    public decimal? NetAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? GrossAmount { get; set; }
    public string Currency { get; set; } = "EUR";

    public PaymentMethod PaymentMethod { get; set; }
    /// <summary>IBAN of the supplier (for Überweisung) – for Lastschrift usually our own IBAN is printed instead.</summary>
    public string? Iban { get; set; }
    public string? Bic { get; set; }
    public string? PaymentReference { get; set; }
    public string? MandateReference { get; set; }
    public string? CreditorId { get; set; }

    /// <summary>Skonto, e.g. "2 % bis 10.11.2026".</summary>
    public string? DiscountTerms { get; set; }

    public DocumentKind Kind { get; set; } = DocumentKind.Rechnung;
    public ExtractionSource Source { get; set; } = ExtractionSource.None;
    /// <summary>"XRechnung (CII)", "ZUGFeRD", "PDF" …</summary>
    public string? FormatName { get; set; }

    /// <summary>Raw text that was analysed (kept for full-text search / debugging).</summary>
    public string? RawText { get; set; }

    public IEnumerable<string> MissingFields()
    {
        if (string.IsNullOrWhiteSpace(InvoiceNumber)) yield return "Rechnungsnummer";
        if (InvoiceDate is null) yield return "Rechnungsdatum";
        if (GrossAmount is null) yield return "Betrag";
        if (PaymentMethod == PaymentMethod.Unbekannt) yield return "Zahlungsart";
    }
}
