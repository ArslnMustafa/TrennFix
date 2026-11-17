using System.Text;
using s2industries.ZUGFeRD;
using TrennFix.Core.Models;

namespace TrennFix.Core.Extraction;

/// <summary>
/// Reads structured e-invoices: XRechnung (CII and UBL) and the XML embedded in
/// ZUGFeRD / Factur-X PDFs. Values are exact, no heuristics needed.
/// </summary>
public static class EInvoiceParser
{
    public static bool LooksLikeEInvoice(byte[] xml)
    {
        var head = Encoding.UTF8.GetString(xml, 0, Math.Min(xml.Length, 4096));
        return head.Contains("CrossIndustryInvoice", StringComparison.Ordinal)
               || head.Contains("urn:oasis:names:specification:ubl:schema:xsd:Invoice-2", StringComparison.Ordinal)
               || head.Contains("urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2", StringComparison.Ordinal);
    }

    public static InvoiceData? TryParse(byte[] xml, ExtractionOptions options, out string? error)
    {
        error = null;
        if (!LooksLikeEInvoice(xml))
        {
            error = "Keine XRechnung/ZUGFeRD-XML";
            return null;
        }
        try
        {
            using var ms = new MemoryStream(xml);
            var d = InvoiceDescriptor.Load(ms);
            return Map(d, xml, options);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static InvoiceData Map(InvoiceDescriptor d, byte[] xml, ExtractionOptions options)
    {
        var head = Encoding.UTF8.GetString(xml, 0, Math.Min(xml.Length, 4096));
        var isUbl = head.Contains("urn:oasis:names:specification:ubl", StringComparison.Ordinal);
        var isXRechnung = head.Contains("xrechnung", StringComparison.OrdinalIgnoreCase) || d.Profile == Profile.XRechnung || d.Profile == Profile.XRechnung1;

        var data = new InvoiceData
        {
            InvoiceNumber = d.InvoiceNo,
            InvoiceDate = d.InvoiceDate is { } id ? DateOnly.FromDateTime(id) : null,
            SellerName = d.Seller?.Name,
            NetAmount = d.TaxBasisAmount ?? d.LineTotalAmount,
            TaxAmount = d.TaxTotalAmount,
            GrossAmount = d.DuePayableAmount is { } due && due != 0 ? due : d.GrandTotalAmount,
            Currency = d.Currency.ToString(),
            PaymentReference = d.PaymentReference,
            MandateReference = d.PaymentMeans?.SEPAMandateReference,
            CreditorId = d.PaymentMeans?.SEPACreditorIdentifier,
            Source = ExtractionSource.EInvoiceXml,
            FormatName = (isXRechnung ? "XRechnung" : "ZUGFeRD/Factur-X") + (isUbl ? " (UBL)" : " (CII)"),
        };

        var sellerTax = d.SellerTaxRegistration ?? [];
        data.SellerVatId = sellerTax.FirstOrDefault(t => t.SchemeID == TaxRegistrationSchemeID.VA)?.No?.Replace(" ", "");
        data.SellerTaxNumber = sellerTax.FirstOrDefault(t => t.SchemeID == TaxRegistrationSchemeID.FC)?.No;

        var creditor = (d.CreditorBankAccounts ?? []).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.IBAN));
        if (creditor is not null)
        {
            data.Iban = GermanFormats.NormalizeIban(creditor.IBAN);
            data.Bic = creditor.BIC;
        }

        var terms = d.PaymentTerms ?? [];
        data.DueDate = terms.Select(t => t.DueDate).FirstOrDefault(x => x.HasValue) is { } dd ? DateOnly.FromDateTime(dd) : null;
        var skonto = terms.FirstOrDefault(t => t.Percentage is > 0);
        if (skonto is not null)
            data.DiscountTerms = $"{skonto.Percentage:0.##} % Skonto" + (skonto.DueDays is { } days ? $" innerhalb {days} Tagen" : "");
        else if (terms.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Description) && t.Description.Contains("Skonto", StringComparison.OrdinalIgnoreCase)) is { } st)
            data.DiscountTerms = st.Description;

        data.PaymentMethod = MapPaymentMeans(d.PaymentMeans?.TypeCode is { } code ? (int)code : null);
        // For direct debit the XML carries the *debtor* account (ours) – never treat that as supplier IBAN.
        if (data.Iban is not null && options.IsOwnIban(data.Iban)) data.Iban = null;

        var typeName = d.Type.ToString();
        data.Kind = typeName.Contains("Credit", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Gutschrift : DocumentKind.Rechnung;
        return data;
    }

    /// <summary>UNTDID 4461 payment means codes (BT-81).</summary>
    public static PaymentMethod MapPaymentMeans(int? code) => code switch
    {
        10 => PaymentMethod.Bar,
        30 or 31 or 42 or 58 => PaymentMethod.Ueberweisung,
        49 or 59 => PaymentMethod.Lastschrift,
        48 or 54 or 55 => PaymentMethod.Kreditkarte,
        57 => PaymentMethod.Ueberweisung, // standing agreement
        68 => PaymentMethod.BereitsBezahlt, // online payment service
        _ => PaymentMethod.Unbekannt,
    };
}
