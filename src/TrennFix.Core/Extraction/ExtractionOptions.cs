namespace TrennFix.Core.Extraction;

/// <summary>
/// Data about our own company. Needed so the parser does not mistake our own
/// name / IBAN / USt-IdNr (which appear on every invoice) for the supplier's.
/// </summary>
public sealed class ExtractionOptions
{
    public IReadOnlyList<string> OwnCompanyNames { get; init; } = [];
    public IReadOnlyList<string> OwnIbans { get; init; } = [];
    public IReadOnlyList<string> OwnVatIds { get; init; } = [];

    /// <summary>If the text layer of a PDF has fewer letters than this, OCR is used.</summary>
    public int MinTextLengthBeforeOcr { get; init; } = 80;

    public bool IsOwnIban(string iban) =>
        OwnIbans.Any(o => GermanFormats.NormalizeIban(o) == GermanFormats.NormalizeIban(iban));

    public bool IsOwnVatId(string vat) =>
        OwnVatIds.Any(o => Normalize(o) == Normalize(vat));

    public bool IsOwnCompany(string text) =>
        OwnCompanyNames.Any(n => !string.IsNullOrWhiteSpace(n) && text.Contains(n.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
