using TrennFix.Core.Models;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Scanning;

/// <summary>The "PDFs speichern" button: writes listed invoices into the archive folder.</summary>
public sealed class InvoiceArchiver(InvoiceDatabase db, ArchiveService archive)
{
    /// <summary>Saves one invoice. Returns false if it was already saved.</summary>
    public bool Save(InvoiceRecord record)
    {
        if (record.IsSaved) return false;
        var pending = db.GetPending(record.Id)
                      ?? throw new InvalidOperationException($"Für {record.Data.InvoiceNumber ?? record.OriginalFileName} sind keine Dateien mehr vorhanden.");

        var supplier = db.GetSuppliers().FirstOrDefault(s => s.Id == record.SupplierId)
                       ?? throw new InvalidOperationException("Lieferant nicht gefunden.");

        var file = archive.SaveDocument(supplier.FolderName, record.Data, record.MailReceived, record.OriginalFileName,
            pending.Main, pending.MainExtension);

        string? xml = null;
        if (pending.Xml is not null)
        {
            xml = ArchiveService.CompanionPath(file, ".xml");
            File.WriteAllBytes(xml, pending.Xml);
        }

        string? mail = null;
        if (pending.Mail is not null)
        {
            mail = ArchiveService.CompanionPath(file, pending.MailExtension);
            File.WriteAllBytes(mail, pending.Mail);
        }

        db.MarkSaved(record.Id, file, xml, mail);
        record.FilePath = file;
        record.XmlFilePath = xml;
        record.MailFilePath = mail;
        return true;
    }

    /// <summary>
    /// Writes the document of a not-yet-saved invoice to a temp file so it can be opened or printed.
    /// </summary>
    public string? WritePreview(InvoiceRecord record, bool originalMail = false)
    {
        if (db.GetPending(record.Id) is not { } p) return null;
        var bytes = originalMail ? p.Mail : p.Main;
        if (bytes is null) return null;

        var dir = Path.Combine(Path.GetTempPath(), "TrennFix", "Vorschau");
        Directory.CreateDirectory(dir);
        var date = record.Data.InvoiceDate ?? DateOnly.FromDateTime(record.MailReceived.LocalDateTime);
        var name = ArchiveService.DocumentBaseName(record.Data, date, record.OriginalFileName);
        var path = Path.Combine(dir, name + (originalMail ? p.MailExtension : p.MainExtension));
        try { File.WriteAllBytes(path, bytes); }
        catch (IOException) { path = Path.Combine(dir, $"{name}_{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 120)] + p.MainExtension); File.WriteAllBytes(path, bytes); }
        return path;
    }
}
