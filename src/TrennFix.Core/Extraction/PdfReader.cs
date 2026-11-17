using UglyToad.PdfPig;

namespace TrennFix.Core.Extraction;

public sealed record PdfContent(string Text, int PageCount, byte[]? EmbeddedXml, string? EmbeddedXmlName, string? Error);

/// <summary>Reads the text layer and embedded e-invoice XML (ZUGFeRD / Factur-X) from a PDF.</summary>
public static class PdfReader
{
    private static readonly string[] KnownXmlNames =
        ["factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml", "zugferd_invoice.xml", "ZUGFeRD-invoice.xml"];

    public static PdfContent Read(byte[] pdf)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            var pages = new List<string>();
            foreach (var page in doc.GetPages())
            {
                var height = page.Height;
                var words = page.GetWords()
                    .Select(w => new LayoutWord(
                        w.Text,
                        w.BoundingBox.Left,
                        height - w.BoundingBox.Top,
                        w.BoundingBox.Width,
                        Math.Max(1, w.BoundingBox.Height)))
                    .ToList();
                pages.Add(LayoutText.Build(words));
            }

            byte[]? xml = null;
            string? xmlName = null;
            if (doc.Advanced.TryGetEmbeddedFiles(out var files) && files is not null)
            {
                var xmlFiles = files.Where(f => f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
                var chosen = xmlFiles.FirstOrDefault(f => KnownXmlNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                             ?? xmlFiles.FirstOrDefault(f => EInvoiceParser.LooksLikeEInvoice(f.Bytes.ToArray()));
                if (chosen is not null)
                {
                    xml = chosen.Bytes.ToArray();
                    xmlName = chosen.Name;
                }
            }

            return new PdfContent(string.Join("\n\n", pages), pages.Count, xml, xmlName, null);
        }
        catch (Exception ex)
        {
            // Encrypted / broken PDFs: no text layer, OCR may still work.
            return new PdfContent("", 0, null, null, ex.Message);
        }
    }
}
