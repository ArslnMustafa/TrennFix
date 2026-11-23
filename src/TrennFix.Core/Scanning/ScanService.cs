using TrennFix.Core.Extraction;
using TrennFix.Core.Mail;
using TrennFix.Core.Models;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Scanning;

public enum ScanEventLevel { Info, Success, Warning, Error }

public sealed record ScanEvent(ScanEventLevel Level, string Message, InvoiceRecord? Record = null);

public sealed record ScanSummary(int MailsSeen, int MailsSkipped, int InvoicesImported, int Warnings, int Errors, DateTimeOffset From, bool Cancelled);

/// <summary>
/// The "Rechnungen abrufen" button: reads every mail in the chosen period (default: since the last
/// successful scan), lists the invoices and remembers where it stopped. Mails that were already processed are skipped (also when
/// they were moved to another folder in the meantime).
/// </summary>
public sealed class ScanService(AppSettings settings, InvoiceDatabase db, IOcrEngine? ocr, Action<AppSettings> saveSettings)
{
    /// <summary>Re-check this much before the last scan to be safe against clock skew / late delivery.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>Default start of the next scan: last scan (minus a safety overlap) or the configured first date.</summary>
    public static DateTimeOffset DefaultFrom(AppSettings settings) =>
        settings.LastScanUtc is { } last ? last - Overlap : settings.InitialScanFrom;

    /// <param name="from">Mails received at or after this time.</param>
    /// <param name="until">Mails received before this time; null = up to now (then the scan position is remembered).</param>
    public async Task<ScanSummary> RunAsync(IMailSource source, DateTimeOffset from, DateTimeOffset? until,
        IProgress<ScanEvent> progress, CancellationToken ct)
    {
        var scanStart = DateTimeOffset.UtcNow;

        var extractor = new InvoiceExtractor(settings.ToExtractionOptions(), settings.OcrEnabled ? ocr : null);
        var importer = new InvoiceImporter(extractor, db, settings.SaveOriginalMail, settings.PriceAlertPercent);
        var archiver = settings.AutoSaveDocuments ? new InvoiceArchiver(db, new ArchiveService(settings.ArchiveRoot)) : null;

        int seen = 0, skipped = 0, imported = 0, warnings = 0, errors = 0;
        DateTimeOffset? earliestFailure = null;

        progress.Report(new(ScanEventLevel.Info, $"Verbinde mit {source.DisplayName} …"));
        await source.ConnectAsync(ct);
        progress.Report(new(ScanEventLevel.Info, until is { } u
            ? $"Suche Mails mit Anhängen vom {from.LocalDateTime:dd.MM.yyyy} bis {u.LocalDateTime.AddSeconds(-1):dd.MM.yyyy} …"
            : $"Suche Mails mit Anhängen seit {from.LocalDateTime:dd.MM.yyyy HH:mm} …"));

        try
        {
            await foreach (var mail in source.GetMessagesAsync(from, until, ct))
            {
                if (mail.Received < from || until is { } end && mail.Received >= end) continue;
                seen++;
                if (db.IsMessageProcessed(mail.MessageId))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var result = await importer.ImportAsync(mail, ct);
                    db.MarkMessageProcessed(mail.MessageId, mail.Received, result.Records.Count, result.Note);
                    if (archiver is not null)
                        foreach (var r in result.Records) archiver.Save(r);

                    if (result.Records.Count == 0)
                        progress.Report(new(ScanEventLevel.Info, $"{mail.Received.LocalDateTime:dd.MM. HH:mm}  {mail.Subject} – {result.Note}"));

                    foreach (var r in result.Records)
                    {
                        imported++;
                        var level = r.Status == InvoiceStatus.Ok ? ScanEventLevel.Success : ScanEventLevel.Warning;
                        if (level == ScanEventLevel.Warning) warnings++;
                        progress.Report(new(level,
                            $"{r.SupplierName}: {r.Data.InvoiceNumber ?? "(ohne Nr.)"}  {r.Data.GrossAmount:N2} {r.Data.Currency}" +
                            (r.Warnings.Count > 0 ? "  ⚠ " + string.Join(" | ", r.Warnings) : ""), r));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    earliestFailure = earliestFailure is { } ef && ef < mail.Received ? ef : mail.Received;
                    progress.Report(new(ScanEventLevel.Error, $"Fehler bei Mail \"{mail.Subject}\" ({mail.Received.LocalDateTime:dd.MM.yyyy}): {ex.Message}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress.Report(new(ScanEventLevel.Warning, "Scan abgebrochen. Beim nächsten Scan wird an derselben Stelle weitergemacht."));
            return new ScanSummary(seen, skipped, imported, warnings, errors, from, Cancelled: true);
        }

        // Next scan starts here – or at the first failed mail so it is retried.
        // A scan of a past period (until set) does not move the position.
        if (until is null)
        {
            settings.LastScanUtc = earliestFailure is { } f && f < scanStart ? f : scanStart;
            saveSettings(settings);
        }

        progress.Report(new(errors > 0 ? ScanEventLevel.Warning : ScanEventLevel.Success,
            $"Fertig: {seen} Mails geprüft, {skipped} bereits bekannt, {imported} Rechnungen erkannt, {warnings} zu prüfen, {errors} Fehler."));
        return new ScanSummary(seen, skipped, imported, warnings, errors, from, Cancelled: false);
    }
}
