using TrennFix.Core.Models;

namespace TrennFix.Core.Extraction;

/// <summary>OCR for scanned PDFs. The Windows app uses the OCR built into Windows (Windows.Media.Ocr).</summary>
public interface IOcrEngine
{
    string Name { get; }

    /// <summary>Returns layout text (see <see cref="LayoutText"/>) for all pages of the PDF.</summary>
    Task<string> RecognizePdfAsync(byte[] pdf, CancellationToken ct);
}

/// <summary>
/// Decides how to read a document:
/// 1. XRechnung XML / ZUGFeRD XML embedded in the PDF  → exact values
/// 2. Text layer of the PDF                            → heuristics
/// 3. OCR (scanned PDF, or unusable text layer)        → heuristics
/// </summary>
public sealed class InvoiceExtractor(ExtractionOptions options, IOcrEngine? ocr)
{
    private readonly TextInvoiceParser _textParser = new(options);

    public InvoiceData? ExtractFromXml(byte[] xml, out string? error) => EInvoiceParser.TryParse(xml, options, out error);

    public async Task<InvoiceData> ExtractFromPdfAsync(byte[] pdf, CancellationToken ct)
    {
        var content = PdfReader.Read(pdf);

        if (content.EmbeddedXml is not null && EInvoiceParser.TryParse(content.EmbeddedXml, options, out _) is { } einvoice)
        {
            einvoice.RawText = content.Text;
            // Seller name is sometimes empty in minimal profiles – fill from text.
            if (string.IsNullOrWhiteSpace(einvoice.SellerName) && content.Text.Length > 0)
                einvoice.SellerName = _textParser.Parse(content.Text, ExtractionSource.PdfText).SellerName;
            return einvoice;
        }

        InvoiceData? fromText = null;
        var letters = content.Text.Count(char.IsLetter);
        if (letters > 0)
        {
            fromText = _textParser.Parse(content.Text, ExtractionSource.PdfText);
            // A good text layer → done. A short text layer (scan with a stamp/footer only) or one without
            // number AND amount (fonts without Unicode mapping) → try OCR as well.
            if (ocr is null) return fromText;
            if (letters >= options.MinTextLengthBeforeOcr && fromText.MissingFields().Count() <= 1) return fromText;
            if (fromText.Kind == DocumentKind.Sonstiges) return fromText;
        }

        string? ocrError = null;
        if (ocr is not null)
        {
            try
            {
                var ocrText = await ocr.RecognizePdfAsync(pdf, ct);
                var fromOcr = _textParser.Parse(ocrText, ExtractionSource.Ocr);
                if (fromText is null || fromOcr.MissingFields().Count() < fromText.MissingFields().Count())
                    return fromOcr;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ocrError = ex.Message;
            }
        }

        return fromText ?? new InvoiceData
        {
            Source = ExtractionSource.None,
            FormatName = ocrError is null ? "PDF (nicht lesbar)" : $"PDF (OCR-Fehler: {ocrError})",
            Kind = DocumentKind.Unbekannt,
        };
    }
}
