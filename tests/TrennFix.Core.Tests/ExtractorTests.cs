using s2industries.ZUGFeRD;
using TrennFix.Core.Extraction;
using TrennFix.Core.Models;

namespace TrennFix.Core.Tests;

public class ExtractorTests
{
    private static readonly ExtractionOptions Options = new()
    {
        OwnCompanyNames = [SampleInvoices.OwnCompany],
        OwnIbans = [SampleInvoices.OwnIban],
        OwnVatIds = [SampleInvoices.OwnVatId],
    };

    [Fact]
    public async Task Digital_pdf_is_read_from_text_layer()
    {
        var pdf = PdfTestHelper.CreatePdf(SampleInvoices.Lastschrift);
        var d = await new InvoiceExtractor(Options, ocr: null).ExtractFromPdfAsync(pdf, CancellationToken.None);

        Assert.Equal(ExtractionSource.PdfText, d.Source);
        Assert.Equal("7700123456", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 5), d.InvoiceDate);
        Assert.Equal(71.34m, d.GrossAmount);
        Assert.Equal(PaymentMethod.Lastschrift, d.PaymentMethod);
        Assert.Equal("NetzFunk Telekommunikation AG", d.SellerName);
        Assert.True(d.LinesVerified);
        Assert.Equal(["Business Flat L", "Zusatzoption Festnetz"], d.Lines.Select(l => l.Description));
    }

    [Fact]
    public async Task Digital_pdf_ueberweisung()
    {
        var pdf = PdfTestHelper.CreatePdf(SampleInvoices.Ueberweisung);
        var d = await new InvoiceExtractor(Options, ocr: null).ExtractFromPdfAsync(pdf, CancellationToken.None);

        Assert.Equal("RE-2026-10457", d.InvoiceNumber);
        Assert.Equal(271.20m, d.GrossAmount);
        Assert.Equal(PaymentMethod.Ueberweisung, d.PaymentMethod);
        Assert.Equal("DE90100400000123456789", d.Iban);
        Assert.Equal("Bürobedarf Schneider GmbH", d.SellerName);

        Assert.True(d.LinesVerified);
        Assert.Equal(["Kopierpapier A4, 500 Blatt", "Toner HP 305A"], d.Lines.Select(l => l.Description));
        Assert.Equal(4.99m, d.Lines[0].UnitPrice);
        Assert.Equal(10m, d.Lines[0].Quantity);
    }

    [Fact]
    public async Task Scanned_pdf_uses_ocr()
    {
        if (!PdfTestHelper.ToolAvailable("tesseract") || !PdfTestHelper.ToolAvailable("pdftoppm"))
            return; // OCR tools not installed on this machine

        // Simulate a scan: render the invoice to an image-only PDF (no text layer).
        var digital = PdfTestHelper.CreatePdf(SampleInvoices.Ueberweisung);
        var scanned = await ImageOnlyPdf(digital);
        Assert.True(PdfReader.Read(scanned).Text.Trim().Length == 0, "test PDF must not have a text layer");

        var d = await new InvoiceExtractor(Options, new TesseractCliOcrEngine()).ExtractFromPdfAsync(scanned, CancellationToken.None);

        Assert.Equal(ExtractionSource.Ocr, d.Source);
        Assert.Equal("RE-2026-10457", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 3), d.InvoiceDate);
        Assert.Equal(271.20m, d.GrossAmount);
        Assert.Equal(PaymentMethod.Ueberweisung, d.PaymentMethod);
        Assert.Equal("DE90100400000123456789", d.Iban);

        // positions also from OCR text
        Assert.True(d.LinesVerified, string.Join("\n", d.Lines.Select(l => $"{l.Description} | {l.Quantity} | {l.UnitPrice} | {l.LineTotal}")));
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal(89.00m, d.Lines[1].UnitPrice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XRechnung_xml_is_read_exactly(bool ubl)
    {
        var desc = InvoiceDescriptor.CreateInvoice("XR-555", new DateTime(2026, 11, 2), CurrencyCodes.EUR);
        desc.SetSeller("Stadtwerke Beispielstadt GmbH", "12345", "Beispielstadt", "Werkstr. 1", CountryCodes.DE);
        desc.AddSellerTaxRegistration("DE777888999", TaxRegistrationSchemeID.VA);
        desc.SetBuyer(SampleInvoices.OwnCompany, "80331", "München", "Industriestr. 5", CountryCodes.DE);
        desc.SetPaymentMeansSepaDirectDebit("DE98ZZZ09999999999", "MANDAT-77");
        desc.AddDebitorFinancialAccount(SampleInvoices.OwnIban.Replace(" ", ""), null);
        desc.AddTradePaymentTerms("Abbuchung am 16.11.2026", new DateTime(2026, 11, 16));
        desc.SetTotals(lineTotalAmount: 100m, taxBasisAmount: 100m, taxTotalAmount: 19m, grandTotalAmount: 119m, duePayableAmount: 119m);
        desc.AddApplicableTradeTax(100m, 19m, 19m, TaxTypes.VAT, TaxCategoryCodes.S);
        desc.ReferenceOrderNo = "991-01484-64";
        desc.AddTradeLineItem(name: "Strom Oktober", netUnitPrice: 100m, unitCode: QuantityCodes.H87, billedQuantity: 1, categoryCode: TaxCategoryCodes.S, taxPercent: 19m);

        using var ms = new MemoryStream();
        desc.Save(ms, ZUGFeRDVersion.Version23, Profile.XRechnung, ubl ? ZUGFeRDFormats.UBL : ZUGFeRDFormats.CII);

        var d = new InvoiceExtractor(Options, null).ExtractFromXml(ms.ToArray(), out var error);

        Assert.Null(error);
        Assert.NotNull(d);
        Assert.Equal(ExtractionSource.EInvoiceXml, d.Source);
        Assert.StartsWith("XRechnung", d.FormatName);
        Assert.Equal("XR-555", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 2), d.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 11, 16), d.DueDate);
        Assert.Equal(119m, d.GrossAmount);
        Assert.Equal(100m, d.NetAmount);
        Assert.Equal(PaymentMethod.Lastschrift, d.PaymentMethod);
        Assert.Equal("MANDAT-77", d.MandateReference);
        Assert.Equal("DE777888999", d.SellerVatId);
        Assert.Equal("Stadtwerke Beispielstadt GmbH", d.SellerName);
        Assert.Null(d.Iban);
        var line = Assert.Single(d.Lines);
        Assert.Equal("Strom Oktober", line.Description);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal("Stk", line.Unit);
        Assert.True(d.LinesVerified);
    }

    private static async Task<byte[]> ImageOnlyPdf(byte[] pdf)
    {
        var dir = Directory.CreateTempSubdirectory("trennfix-scan");
        try
        {
            var src = Path.Combine(dir.FullName, "src.pdf");
            await File.WriteAllBytesAsync(src, pdf);
            var psi = new System.Diagnostics.ProcessStartInfo("pdftoppm", $"-r 200 -gray -jpeg \"{src}\" \"{Path.Combine(dir.FullName, "p")}\"");
            using (var p = System.Diagnostics.Process.Start(psi)!) await p.WaitForExitAsync();
            var jpg = Directory.GetFiles(dir.FullName, "p*.jpg").Single();

            // Wrap the JPEG in a PDF page without any text.
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddJpeg(await File.ReadAllBytesAsync(jpg), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, page.PageSize.Width, page.PageSize.Height));
            return builder.Build();
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
