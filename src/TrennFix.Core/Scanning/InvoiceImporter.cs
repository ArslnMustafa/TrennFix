using TrennFix.Core.Extraction;
using TrennFix.Core.Mail;
using TrennFix.Core.Models;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Scanning;

/// <summary>Turns one e-mail into archived invoice records.</summary>
public sealed class InvoiceImporter(InvoiceExtractor extractor, InvoiceDatabase db, ArchiveService archive, bool saveOriginalMail)
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

        var records = new List<InvoiceRecord>();
        foreach (var doc in relevant)
            if (Store(mail, doc) is { } rec) records.Add(rec);

        if (saveOriginalMail && mail.SaveOriginalAsync is not null && records.Count > 0)
        {
            try
            {
                var mailPath = await mail.SaveOriginalAsync(ArchiveService.CompanionPath(records[0].FilePath, mail.OriginalExtension), ct);
                foreach (var r in records) r.MailFilePath = mailPath;
                db.SetMailFilePath(records.Select(r => r.Id), mailPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the invoice itself is safe – the original mail is a nice-to-have
            }
        }

        return new ImportResult(records, $"{records.Count} Rechnung(en) importiert");
    }

    private InvoiceRecord? Store(MailItem mail, Doc doc)
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
        if (duplicate is not null)
            record.Warnings.Add($"Doppelt: Rechnung {duplicate.Data.InvoiceNumber} wurde bereits am {duplicate.ImportedAt:dd.MM.yyyy} archiviert ({Path.GetFileName(duplicate.FilePath)}).");

        record.Status = duplicate is not null ? InvoiceStatus.Duplikat
            : record.Warnings.Count > 0 ? InvoiceStatus.Pruefen
            : InvoiceStatus.Ok;

        record.FilePath = archive.SaveDocument(supplier.FolderName, data, mail.Received, doc.Main.FileName, doc.Main.Content,
            doc.Main.IsXml ? ".xml" : ".pdf");
        if (doc.Xml is not null && !ReferenceEquals(doc.Xml, doc.Main))
        {
            record.XmlFilePath = ArchiveService.CompanionPath(record.FilePath, ".xml");
            File.WriteAllBytes(record.XmlFilePath, doc.Xml.Content);
        }

        db.InsertInvoice(record);
        return record;
    }
}
