using System.Runtime.CompilerServices;
using TrennFix.Core.Mail;
using TrennFix.Core.Models;
using TrennFix.Core.Scanning;
using TrennFix.Core.Storage;

namespace TrennFix.Core.Tests;

public sealed class ScanServiceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("trennfix-scan-test");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    private sealed class FakeMailbox(List<MailItem> items) : IMailSource
    {
        public string DisplayName => "Testpostfach";
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var m in items.Where(m => m.Received >= since))
            {
                await Task.Yield();
                yield return m;
            }
        }
    }

    private static MailItem Mail(string id, string from, string fromName, DateTimeOffset received, params MailAttachment[] atts) => new()
    {
        MessageId = id, Subject = "Ihre Rechnung", FromAddress = from, FromName = fromName, Received = received, Attachments = atts,
    };

    private static MailAttachment Pdf(string name, string text) => new(name, "application/pdf", PdfTestHelper.CreatePdf(text));

    [Fact]
    public async Task Scans_mailbox_archives_by_supplier_and_continues_from_last_scan()
    {
        var settings = new AppSettings
        {
            ArchiveRoot = Path.Combine(_dir.FullName, "Rechnungen"),
            InitialScanFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            OwnCompanyNames = [SampleInvoices.OwnCompany],
            OwnIbans = [SampleInvoices.OwnIban],
            OwnVatIds = [SampleInvoices.OwnVatId],
            SaveOriginalMail = false,
        };
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);
        var mails = new List<MailItem>
        {
            Mail("<1@schneider>", "rechnung@buero-schneider.de", "Buchhaltung", t0,
                Pdf("RE-2026-10457.pdf", SampleInvoices.Ueberweisung),
                Pdf("AGB.pdf", "Allgemeine Geschäftsbedingungen der Bürobedarf Schneider GmbH\n§ 1 Geltungsbereich ...")),
            Mail("<2@netzfunk>", "noreply@netzfunk.de", "NetzFunk", t0.AddHours(1), Pdf("Rechnung.pdf", SampleInvoices.Lastschrift)),
            Mail("<3@private>", "freund@gmail.com", "Ali", t0.AddHours(2)), // no attachment
        };

        using var db = new InvoiceDatabase(settings.DatabasePath);
        var saved = 0;
        var service = new ScanService(settings, db, ocr: null, _ => saved++);
        var events = new List<ScanEvent>();
        var progress = new SyncProgress(events.Add);

        var s1 = await service.RunAsync(new FakeMailbox(mails), progress, CancellationToken.None);
        Assert.Equal(3, s1.MailsSeen);
        Assert.Equal(2, s1.InvoicesImported);
        Assert.Equal(1, saved);
        Assert.NotNull(settings.LastScanUtc);

        var invoices = db.GetInvoices();
        Assert.Equal(2, invoices.Count);
        var schneider = invoices.Single(i => i.Data.InvoiceNumber == "RE-2026-10457");
        Assert.Equal("Bürobedarf Schneider GmbH", schneider.SupplierName);
        Assert.Equal(InvoiceStatus.Ok, schneider.Status);
        Assert.Equal(Path.Combine(settings.ArchiveRoot, "Bürobedarf Schneider GmbH", "2026", "2026-11-03_RE-2026-10457.pdf"), schneider.FilePath);
        Assert.True(File.Exists(schneider.FilePath));
        Assert.Equal(PaymentMethod.Lastschrift, invoices.Single(i => i.Data.InvoiceNumber == "7700123456").Data.PaymentMethod);

        // ---- second scan: old mails (even if moved to a subfolder) are skipped; new mail with changed IBAN
        var changedIban = SampleInvoices.Ueberweisung
            .Replace("RE-2026-10457", "RE-2026-11999")
            .Replace("DE90 1004 0000 0123 4567 89", "DE44 5001 0517 5407 3249 31");
        mails.Add(Mail("<4@schneider>", "rechnung@buero-schneider.de", "Buchhaltung", DateTimeOffset.UtcNow.AddMinutes(1),
            Pdf("RE-2026-11999.pdf", changedIban)));
        // the same invoice sent again in another mail → duplicate
        mails.Add(Mail("<5@schneider>", "rechnung@buero-schneider.de", "Buchhaltung", DateTimeOffset.UtcNow.AddMinutes(2),
            Pdf("Kopie.pdf", SampleInvoices.Ueberweisung + "\nKOPIE")));

        var s2 = await service.RunAsync(new FakeMailbox(mails), progress, CancellationToken.None);
        Assert.Equal(2, s2.InvoicesImported);
        Assert.Equal(0, s2.Errors);

        var all = db.GetInvoices();
        Assert.Single(all.Select(i => i.SupplierId).Where(id => id == schneider.SupplierId).Distinct());
        var changed = all.Single(i => i.Data.InvoiceNumber == "RE-2026-11999");
        Assert.Equal(InvoiceStatus.Pruefen, changed.Status);
        Assert.Contains(changed.Warnings, w => w.StartsWith("ACHTUNG: IBAN"));
        var dup = all.Single(i => i.MailMessageId == "<5@schneider>");
        Assert.Equal(InvoiceStatus.Duplikat, dup.Status);

        // confirming the new IBAN removes the warning
        db.ConfirmIban(changed.Id);
        Assert.Equal(InvoiceStatus.Ok, db.GetInvoice(changed.Id)!.Status);

        // Excel export works
        var xlsx = Path.Combine(_dir.FullName, "export.xlsx");
        Export.ExcelExporter.Export(all, xlsx);
        Assert.True(new FileInfo(xlsx).Length > 1000);
    }

    private sealed class SyncProgress(Action<ScanEvent> action) : IProgress<ScanEvent>
    {
        public void Report(ScanEvent value) => action(value);
    }
}
