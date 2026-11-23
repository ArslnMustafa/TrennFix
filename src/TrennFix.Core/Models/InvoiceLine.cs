namespace TrennFix.Core.Models;

/// <summary>One position (Artikel / Leistung) of an invoice.</summary>
public sealed class InvoiceLine
{
    public string? Position { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Ean { get; set; }
    public string Description { get; set; } = "";
    public decimal? Quantity { get; set; }
    /// <summary>Normalised unit ("Stk", "Std", "kg" …) or null.</summary>
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }

    /// <summary>
    /// Key that identifies the same product of the same supplier across invoices:
    /// EAN, else article number, else the normalised description.
    /// </summary>
    public string ItemKey => Ean is { Length: > 0 } ean ? "ean:" + ean
        : ArticleNumber is { Length: > 0 } art ? "art:" + art.ToUpperInvariant()
        : "txt:" + NormalizeDescription(Description);

    /// <summary>Usable for price comparison: has a unit price and a real description (not a discount / freight line).</summary>
    public bool IsPriceRelevant => UnitPrice is > 0 && Description.Count(char.IsLetter) >= 3;

    public static string NormalizeDescription(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>A purchase of one item, with the invoice it came from – used for the price list.</summary>
public sealed record PriceHistoryEntry(
    long InvoiceId,
    long SupplierId,
    string SupplierName,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    DateTimeOffset MailReceived,
    InvoiceLine Line,
    bool Verified)
{
    public DateOnly EffectiveDate => InvoiceDate ?? DateOnly.FromDateTime(MailReceived.LocalDateTime);
}
