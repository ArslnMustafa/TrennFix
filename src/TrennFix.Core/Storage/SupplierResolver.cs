using System.Text.RegularExpressions;
using TrennFix.Core.Extraction;
using TrennFix.Core.Models;

namespace TrennFix.Core.Storage;

/// <summary>
/// Finds the supplier an invoice belongs to, so that all invoices of one supplier land in the same
/// folder – even if the name is written differently ("Telekom" vs. "Telekom Deutschland GmbH").
///
/// Matching order: USt-IdNr → Steuernummer → known IBAN → sender address → sender domain → name.
/// </summary>
public sealed class SupplierResolver(InvoiceDatabase db)
{
    /// <summary>Free-mail / shared domains: the domain says nothing about the supplier.</summary>
    private static readonly HashSet<string> GenericDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "web.de", "gmx.de", "gmx.net", "t-online.de", "outlook.com", "outlook.de", "hotmail.com",
        "hotmail.de", "live.com", "live.de", "yahoo.com", "yahoo.de", "icloud.com", "me.com", "aol.com", "freenet.de", "posteo.de",
        "mail.de", "online.de", "arcor.de", "email.de", "gmx.com", "protonmail.com", "proton.me",
        // invoice / marketplace platforms sending on behalf of many sellers
        "amazon.de", "amazon.com", "ebay.de", "ebay.com", "paypal.de", "paypal.com", "sevdesk.de", "lexoffice.de", "lexware.de",
        "billomat.com", "debitoor.de", "sumup.com", "stripe.com", "invoice.stripe.com", "zervant.com", "fastbill.com",
    };

    private static readonly Regex GenericSenderName = new(
        @"^(?:no[-_.]?reply|do[-_.]?not[-_.]?reply|rechnung(?:en)?|invoice[s]?|billing|buchhaltung|info|service|kundenservice|support|mail|post|office)\b",
        RegexOptions.IgnoreCase);

    public sealed record Result(Supplier Supplier, bool IsNew);

    public Result Resolve(InvoiceData data, string senderAddress, string senderName)
    {
        var suppliers = db.GetSuppliers();
        var vat = data.SellerVatId;
        var taxNo = data.SellerTaxNumber;
        var address = senderAddress.Trim().ToLowerInvariant();
        var domain = RegistrableDomain(address);
        var genericDomain = domain is null || GenericDomains.Contains(domain) || GenericDomains.Contains(address.Split('@').Last());

        Supplier? match =
            (vat is null ? null : suppliers.FirstOrDefault(s => SameId(s.VatId, vat)))
            ?? (taxNo is null ? null : suppliers.FirstOrDefault(s => SameId(s.TaxNumber, taxNo)))
            ?? (data.Iban is null ? null : suppliers.FirstOrDefault(s => s.KnownIbans.Contains(data.Iban)));

        if (match is null && address.Length > 0)
        {
            // Sender address / domain – but not if the IDs on the invoice prove it is somebody else
            // (e.g. one billing platform sending for several companies).
            bool Compatible(Supplier s) => vat is null || s.VatId is null || SameId(s.VatId, vat);

            match = suppliers.FirstOrDefault(s => Compatible(s) && s.EmailAddresses.Contains(address))
                    ?? (genericDomain ? null : suppliers.FirstOrDefault(s => Compatible(s) && s.EmailAddresses.Any(e => RegistrableDomain(e) == domain)));
        }

        var name = ChooseName(data, senderName, domain, genericDomain);
        if (match is null && name is not null)
            match = suppliers.FirstOrDefault(s => NormalizeName(s.Name) == NormalizeName(name) && (vat is null || s.VatId is null || SameId(s.VatId, vat)));

        var isNew = false;
        if (match is null)
        {
            var displayName = name ?? "Unbekannter Lieferant";
            var folder = ArchiveService.SanitizeFolderName(displayName);
            var usedFolders = suppliers.Select(s => s.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = folder;
            for (var i = 2; usedFolders.Contains(unique); i++) unique = $"{folder} ({i})";
            match = db.CreateSupplier(displayName, unique, vat, taxNo);
            isNew = true;
        }
        else if ((match.VatId is null && vat is not null) || (match.TaxNumber is null && taxNo is not null))
        {
            db.UpdateSupplierIds(match.Id, vat, taxNo);
        }

        if (address.Length > 0 && !match.EmailAddresses.Contains(address))
        {
            db.AddSupplierEmail(match.Id, address);
            match.EmailAddresses.Add(address);
        }
        return new Result(match, isNew);
    }

    private static string? ChooseName(InvoiceData data, string senderName, string? domain, bool genericDomain)
    {
        if (!string.IsNullOrWhiteSpace(data.SellerName)) return data.SellerName.Trim();
        var sn = senderName.Trim().Trim('"', '\'');
        if (sn.Length > 1 && !sn.Contains('@') && !GenericSenderName.IsMatch(sn)) return sn;
        if (!genericDomain && domain is not null)
        {
            var label = domain.Split('.')[0];
            return char.ToUpperInvariant(label[0]) + label[1..];
        }
        return null;
    }

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"\b(gmbh|co|kg|ag|ug|ohg|e\.?\s?k|haftungsbeschränkt|mbh|se|gbr|ltd|inc|llc)\b\.?", "");
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    public static string? RegistrableDomain(string address)
    {
        var at = address.LastIndexOf('@');
        var host = (at >= 0 ? address[(at + 1)..] : address).Trim().TrimEnd('>').ToLowerInvariant();
        if (host.Length == 0) return null;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return host;
        // co.uk, com.tr, …
        var twoPartTld = parts.Length >= 3 && parts[^2] is "co" or "com" or "org" or "net" or "gv" or "ac";
        return string.Join('.', parts.TakeLast(twoPartTld ? 3 : 2));
    }

    /// <summary>Checks the IBAN on the invoice against the supplier's known accounts (fraud protection).</summary>
    public static string? CheckIban(Supplier supplier, InvoiceData data, bool supplierIsNew, InvoiceDatabase db)
    {
        if (data.Iban is null || data.PaymentMethod == PaymentMethod.Lastschrift) return null;
        if (supplier.KnownIbans.Contains(data.Iban)) return null;

        if (supplierIsNew || supplier.KnownIbans.Count == 0)
        {
            // Trust on first use – but remember it.
            db.AddSupplierIban(supplier.Id, data.Iban, confirmed: true);
            supplier.KnownIbans.Add(data.Iban);
            return null;
        }

        db.AddSupplierIban(supplier.Id, data.Iban, confirmed: false);
        return $"ACHTUNG: IBAN {GermanFormats.FormatIban(data.Iban)} ist neu für {supplier.Name}. " +
               $"Bisher bekannt: {string.Join(", ", supplier.KnownIbans.Select(GermanFormats.FormatIban))}. " +
               "Vor der Zahlung telefonisch beim Lieferanten bestätigen (Betrugsgefahr)!";
    }
}
