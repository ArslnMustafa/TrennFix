using TrennFix.Core.Extraction;
using TrennFix.Core.Mail;
using TrennFix.Core.Models;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Scanning;

/// <summary>
/// Turns one e-mail into invoice records. Nothing is written to the archive folder here: the documents
/// are kept in the database ("nicht gespeichert") until the user clicks "PDFs speichern"
/// (see <see cref="InvoiceArchiver"/>), or right away when auto-save is switched on.
/// </summary>
public sealed class InvoiceImporter(InvoiceExtractor extractor, InvoiceDatabase db, bool saveOriginalMail, decimal priceAlertPercent = 3m,
    Pricing.ProductCatalog? catalog = null)
{
    private readonly SupplierResolver _suppliers = new(db);

    private sealed class Doc
    {
        public required MailAttachment Main { get; init; }
        public required InvoiceData Data { get; set; }
        public MailAttachment? Xml { get; set; }
    }

    public sealed record ImportResult(List<InvoiceRecord> Records, string Note);

    public async Task<ImportResult> ImportAsync(MailItem mail, CancellationToken ct)
    {
        var docs = new List<Doc>();
        var xmlDocs = new List<Doc>();
        var skippedKnown = 0;

        foreach (var att in mail.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (db.ShaExists(ArchiveService.Sha256(att.Content))) { skippedKnown++; continue; }

            if (att.IsXml)
            {
                if (extractor.ExtractFromXml(att.Content, out _) is { } xmlData)
                    xmlDocs.Add(new Doc { Main = att, Data = xmlData, Xml = att });
            }
            else if (att.IsPdf)
            {
                docs.Add(new Doc { Main = att, Data = await extractor.ExtractFromPdfAsync(att.Content, ct) });
            }
        }

        // XRechnung XML + PDF copy in the same mail → one invoice: exact data from XML, PDF for printing.
        foreach (var x in xmlDocs)
        {
            var pdf = docs.FirstOrDefault(d => d.Xml is null && d.Data.InvoiceNumber is not null
                                                && string.Equals(d.Data.InvoiceNumber, x.Data.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
                      ?? (xmlDocs.Count == 1 && docs.Count(d => d.Data.Source != ExtractionSource.EInvoiceXml) == 1
                          ? docs.First(d => d.Data.Source != ExtractionSource.EInvoiceXml) : null);
            if (pdf is not null)
            {
                x.Data.RawText = pdf.Data.RawText;
                pdf.Data = x.Data;
                pdf.Xml = x.Xml;
            }
            else
            {
                docs.Add(x);
            }
        }

        // Ignore AGB, Lieferscheine … as long as the mail contains something else.
        var relevant = docs.Where(d => d.Data.Kind != DocumentKind.Sonstiges).ToList();
        if (relevant.Count == 0)
        {
            var note = docs.Count > 0 ? $"{docs.Count} Anhang/Anhänge, aber kein Rechnungsdokument erkannt"
                : skippedKnown > 0 ? "Anhänge bereits archiviert" : "Keine PDF/XML-Anhänge";
            return new ImportResult([], note);
        }

        byte[]? mailBytes = null;
        if (saveOriginalMail && mail.SaveOriginalAsync is not null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"trennfix-{Guid.NewGuid():N}{mail.OriginalExtension}");
            try
            {
                await mail.SaveOriginalAsync(tmp, ct);
                mailBytes = await File.ReadAllBytesAsync(tmp, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the invoice itself is what matters – the original mail is a nice-to-have
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }

        var records = new List<InvoiceRecord>();
        foreach (var doc in relevant)
            records.Add(Store(mail, doc, mailBytes));

        return new ImportResult(records, $"{records.Count} Rechnung(en) erkannt");
    }

    private InvoiceRecord Store(MailItem mail, Doc doc, byte[]? mailBytes)
    {
        var data = doc.Data;
        var (supplier, isNew) = _suppliers.Resolve(data, mail.FromAddress, mail.FromName);

        var record = new InvoiceRecord
        {
            Data = data,
            SupplierId = supplier.Id,
            SupplierName = supplier.Name,
            OriginalFileName = doc.Main.FileName,
            FileSha256 = ArchiveService.Sha256(doc.Main.Content),
            MailMessageId = mail.MessageId,
            MailSubject = mail.Subject,
            MailFrom = string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : $"{mail.FromName} <{mail.FromAddress}>",
            MailReceived = mail.Received,
        };

        var duplicate = data.Kind is DocumentKind.Rechnung or DocumentKind.Gutschrift
            ? db.FindDuplicate(supplier.Id, data.InvoiceNumber, record.FileSha256)
            : null;

        var missing = data.MissingFields().ToList();
        if (missing.Count > 0) record.Warnings.Add("Nicht erkannt: " + string.Join(", ", missing));
        if (data.Kind == DocumentKind.Unbekannt) record.Warnings.Add("Dokumenttyp nicht erkannt – ist das eine Rechnung?");
        if (data.Kind == DocumentKind.Mahnung) record.Warnings.Add("Mahnung/Zahlungserinnerung – prüfen, ob die Rechnung bereits bezahlt wurde.");
        if (SupplierResolver.CheckIban(supplier, data, isNew, db) is { } ibanWarning) record.Warnings.Add(ibanWarning);
        if (duplicate is null && catalog is not null)
        {
            var (increases, cheaper) = PriceCheck.Check(catalog, supplier.Id, data, record.MailReceived, priceAlertPercent);
            if (increases.Count > 0) record.Warnings.Add(PriceCheck.DescribeIncreases(increases));
            if (cheaper.Count > 0) record.Warnings.Add(PriceCheck.DescribeCheaper(cheaper));
        }
        if (duplicate is not null)
            record.Warnings.Add($"Doppelt: Rechnung {duplicate.Data.InvoiceNumber} wurde bereits am {duplicate.ImportedAt:dd.MM.yyyy} erfasst" +
                               (duplicate.IsSaved ? $" ({Path.GetFileName(duplicate.FilePath)})." : "."));

        record.Status = duplicate is not null ? InvoiceStatus.Duplikat
            : record.Warnings.Count > 0 ? InvoiceStatus.Pruefen
            : InvoiceStatus.Ok;

        db.InsertInvoice(record);
        if (duplicate is null && catalog is not null)
            foreach (var line in data.Lines)
                catalog.Add(new PriceHistoryEntry(record.Id, supplier.Id, supplier.Name, data.InvoiceNumber, data.InvoiceDate,
                    record.MailReceived, line, data.LinesVerified));
        db.SavePending(record.Id, new PendingFiles(
            doc.Main.Content,
            doc.Main.IsXml ? ".xml" : ".pdf",
            doc.Xml is not null && !ReferenceEquals(doc.Xml, doc.Main) ? doc.Xml.Content : null,
            mailBytes,
            mail.OriginalExtension));
        return record;
    }
}
