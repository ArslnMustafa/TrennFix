using TrennFix.Core.Extraction;
using TrennFix.Core.Models;

namespace TrennFix.Core.Tests;

public class TextInvoiceParserTests
{
    private static readonly ExtractionOptions Options = new()
    {
        OwnCompanyNames = [SampleInvoices.OwnCompany],
        OwnIbans = [SampleInvoices.OwnIban],
        OwnVatIds = [SampleInvoices.OwnVatId],
    };

    private static InvoiceData Parse(string text) => new TextInvoiceParser(Options).Parse(text, ExtractionSource.PdfText);

    [Fact]
    public void Ueberweisung_invoice()
    {
        var d = Parse(SampleInvoices.Ueberweisung);
        Assert.Equal(DocumentKind.Rechnung, d.Kind);
        Assert.Equal("RE-2026-10457", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 3), d.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 11, 17), d.DueDate);
        Assert.Equal(271.20m, d.GrossAmount);
        Assert.Equal(227.90m, d.NetAmount);
        Assert.Equal(43.30m, d.TaxAmount);
        Assert.Equal(PaymentMethod.Ueberweisung, d.PaymentMethod);
        Assert.Equal("DE90100400000123456789", d.Iban);
        Assert.Equal("COBADEFFXXX", d.Bic);
        Assert.Equal("DE987654321", d.SellerVatId);
        Assert.Equal("Bürobedarf Schneider GmbH", d.SellerName);
        Assert.Contains("2 % Skonto", d.DiscountTerms);
        Assert.Empty(d.MissingFields());
    }

    [Fact]
    public void Lastschrift_invoice_with_table_header()
    {
        var d = Parse(SampleInvoices.Lastschrift);
        Assert.Equal("7700123456", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 5), d.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 11, 12), d.DueDate);
        Assert.Equal(71.34m, d.GrossAmount);
        Assert.Equal(59.95m, d.NetAmount);
        Assert.Equal(PaymentMethod.Lastschrift, d.PaymentMethod);
        Assert.Null(d.Iban); // only our own IBAN is printed
        Assert.Equal("NF-55512345-01", d.MandateReference);
        Assert.Equal("DE98ZZZ09999999999", d.CreditorId);
        Assert.Equal("DE111222333", d.SellerVatId);
        Assert.Equal("NetzFunk Telekommunikation AG", d.SellerName);
    }

    [Fact]
    public void Already_paid_invoice()
    {
        var d = Parse(SampleInvoices.BereitsBezahlt);
        Assert.Equal("SP-998877", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 10, 28), d.InvoiceDate);
        Assert.Equal(119.00m, d.GrossAmount);
        Assert.Equal(PaymentMethod.BereitsBezahlt, d.PaymentMethod);
        Assert.Equal("ShopProfi24 GmbH & Co. KG", d.SellerName);
    }

    [Fact]
    public void Credit_note()
    {
        var d = Parse(SampleInvoices.Gutschrift);
        Assert.Equal(DocumentKind.Gutschrift, d.Kind);
        Assert.Equal("GS-2026-0042", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 6), d.InvoiceDate);
        Assert.Equal(-105.91m, d.GrossAmount);
    }

    [Fact]
    public void Dunning_letter()
    {
        var d = Parse(SampleInvoices.Mahnung);
        Assert.Equal(DocumentKind.Mahnung, d.Kind);
        Assert.Equal("2026-311", d.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 20), d.DueDate);
        Assert.Equal(PaymentMethod.Ueberweisung, d.PaymentMethod);
        Assert.Equal("DE44500105175407324931", d.Iban);
        Assert.Equal("Hausmeisterservice Yilmaz e.K.", d.SellerName);
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("-89,00", -89.00)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("12.50", 12.50)]
    public void Parses_amounts(string text, decimal expected) => Assert.Equal(expected, GermanFormats.ParseAmount(text));

    [Theory]
    [InlineData("03.11.2026", 2026, 11, 3)]
    [InlineData("3.11.26", 2026, 11, 3)]
    [InlineData("2026-11-03", 2026, 11, 3)]
    [InlineData("3. November 2026", 2026, 11, 3)]
    [InlineData("03. Nov. 2026", 2026, 11, 3)]
    [InlineData("1. März 2026", 2026, 3, 1)]
    public void Parses_dates(string text, int y, int m, int day) =>
        Assert.Equal(new DateOnly(y, m, day), GermanFormats.ParseDate(text));

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("DE89 3704 0044 0532 0130 01", false)]
    [InlineData("AT61 1904 3002 3457 3201", true)]
    public void Validates_ibans(string iban, bool valid) => Assert.Equal(valid, GermanFormats.IsValidIban(iban));
}
