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

        public async IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, DateTimeOffset? until, [EnumeratorCancellation] CancellationToken ct)
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

        var s1 = await service.RunAsync(new FakeMailbox(mails), ScanService.DefaultFrom(settings), null, progress, CancellationToken.None);
        Assert.Equal(3, s1.MailsSeen);
        Assert.Equal(2, s1.InvoicesImported);
        Assert.Equal(1, saved);
        Assert.NotNull(settings.LastScanUtc);

        var invoices = db.GetInvoices();
        Assert.Equal(2, invoices.Count);
        var schneider = invoices.Single(i => i.Data.InvoiceNumber == "RE-2026-10457");
        Assert.Equal("Bürobedarf Schneider GmbH", schneider.SupplierName);
        Assert.Equal(InvoiceStatus.Ok, schneider.Status);

        // Scanning only lists – nothing is written to the archive until "PDFs speichern".
        Assert.False(schneider.IsSaved);
        Assert.False(Directory.Exists(Path.Combine(settings.ArchiveRoot, "Bürobedarf Schneider GmbH")));
        var archiver = new InvoiceArchiver(db, new ArchiveService(settings.ArchiveRoot));
        var preview = archiver.WritePreview(schneider);
        Assert.True(File.Exists(preview));

        Assert.True(archiver.Save(schneider));
        Assert.Equal(Path.Combine(settings.ArchiveRoot, "Bürobedarf Schneider GmbH", "2026", "RE-2026-10457.pdf"), schneider.FilePath);
        Assert.True(File.Exists(schneider.FilePath));
        Assert.True(db.GetInvoice(schneider.Id)!.IsSaved);
        Assert.Null(db.GetPending(schneider.Id));
        Assert.False(archiver.Save(schneider)); // already saved
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

        var s2 = await service.RunAsync(new FakeMailbox(mails), ScanService.DefaultFrom(settings), null, progress, CancellationToken.None);
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

        // "Verwerfen": unsaved invoices are removed and their mails will be read again
        var discarded = db.DiscardUnsaved();
        Assert.Equal(all.Count(i => !i.IsSaved), discarded);
        Assert.Single(db.GetInvoices());
        Assert.False(db.IsMessageProcessed("<2@netzfunk>"));
        Assert.True(db.IsMessageProcessed("<1@schneider>"));

        // a past period can be re-scanned without moving the scan position
        var position = settings.LastScanUtc;
        var s3 = await service.RunAsync(new FakeMailbox(mails), t0.AddHours(-1), t0.AddHours(1.5), progress, CancellationToken.None);
        Assert.Equal(1, s3.InvoicesImported); // only the NetzFunk mail lies in that window and is unprocessed
        Assert.Equal(position, settings.LastScanUtc);

        // Excel export works
        var xlsx = Path.Combine(_dir.FullName, "export.xlsx");
        Export.ExcelExporter.Export(all, xlsx);
        Assert.True(new FileInfo(xlsx).Length > 1000);
    }

    [Fact]
    public async Task Detects_price_increase_and_exports_price_list()
    {
        var settings = new AppSettings
        {
            ArchiveRoot = Path.Combine(_dir.FullName, "Rechnungen"),
            InitialScanFrom = DateTimeOffset.UtcNow.AddDays(-60),
            OwnCompanyNames = [SampleInvoices.OwnCompany],
            OwnIbans = [SampleInvoices.OwnIban],
            SaveOriginalMail = false,
            PriceAlertPercent = 3m,
        };
        var later = SampleInvoices.Ueberweisung
            .Replace("RE-2026-10457", "RE-2026-12001")
            .Replace("03.11.2026", "20.11.2026")
            .Replace("2         89,00        178,00", "2         97,50        195,00")
            .Replace("227,90 €", "244,90 €")
            .Replace("43,30 €", "46,53 €")
            .Replace("271,20 €", "291,43 €");
        var mails = new List<MailItem>
        {
            Mail("<a@s>", "rechnung@buero-schneider.de", "Buchhaltung", DateTimeOffset.UtcNow.AddDays(-20), Pdf("a.pdf", SampleInvoices.Ueberweisung)),
            Mail("<b@s>", "rechnung@buero-schneider.de", "Buchhaltung", DateTimeOffset.UtcNow.AddDays(-2), Pdf("b.pdf", later)),
        };

        using var db = new InvoiceDatabase(settings.DatabasePath);
        var service = new ScanService(settings, db, ocr: null, _ => { });
        await service.RunAsync(new FakeMailbox(mails), ScanService.DefaultFrom(settings), null, new SyncProgress(_ => { }), CancellationToken.None);

        var invoices = db.GetInvoices();
        var first = invoices.Single(i => i.Data.InvoiceNumber == "RE-2026-10457");
        var second = invoices.Single(i => i.Data.InvoiceNumber == "RE-2026-12001");
        Assert.Equal(2, first.Data.Lines.Count); // lines survive the database round trip
        Assert.True(second.Data.LinesVerified);
        Assert.Equal(InvoiceStatus.Pruefen, second.Status);
        var warning = Assert.Single(second.Warnings);
        Assert.StartsWith("Preiserhöhung: Toner HP 305A: 89,00 → 97,50 €", warning);

        var groups = new Pricing.ProductCatalog(db.GetPriceHistory(), db.GetProductOverrides()).Groups();
        var toner = groups.Single(g => g.Name == "Toner HP 305A");
        Assert.Equal(97.50m, toner.LastPrice);
        Assert.Equal(89.00m, toner.Previous!.Line.UnitPrice);
        Assert.Equal(2, toner.PurchaseCount);
        Assert.InRange(toner.ChangePercent!.Value, 9.5m, 9.6m);
        Assert.Equal(0m, groups.Single(g => g.Name.StartsWith("Kopierpapier")).ChangePercent);

        // a third supplier sells the toner cheaper → hint on that supplier's next invoice
        var other = SampleInvoices.Ueberweisung
            .Replace("Bürobedarf Schneider GmbH", "Office Discount Müller GmbH")
            .Replace("DE987654321", "DE222333444")
            .Replace("RE-2026-10457", "OD-77")
            .Replace("03.11.2026", "10.11.2026")
            .Replace("DE90 1004 0000 0123 4567 89", "DE44 5001 0517 5407 3249 31");
        mails.Add(Mail("<c@o>", "info@office-mueller.de", "Office Müller", DateTimeOffset.UtcNow.AddDays(-1), Pdf("c.pdf", other)));
        mails.Add(Mail("<d@s>", "rechnung@buero-schneider.de", "Buchhaltung", DateTimeOffset.UtcNow, Pdf("d.pdf", later.Replace("RE-2026-12001", "RE-2026-12500").Replace("20.11.2026", "22.11.2026"))));
        await service.RunAsync(new FakeMailbox(mails), ScanService.DefaultFrom(settings), null, new SyncProgress(_ => { }), CancellationToken.None);
        var latest = db.GetInvoices().Single(i => i.Data.InvoiceNumber == "RE-2026-12500");
        Assert.Contains(latest.Warnings, w => w.StartsWith("Anderswo günstiger: Toner HP 305A: bei Office Discount Müller GmbH"));

        var xlsx = Path.Combine(_dir.FullName, "preise.xlsx");
        groups = new Pricing.ProductCatalog(db.GetPriceHistory(), db.GetProductOverrides()).Groups();
        Export.PriceListExporter.Export(groups, xlsx, settings.PriceAlertPercent);
        using var wb = new ClosedXML.Excel.XLWorkbook(xlsx);
        Assert.Equal(["Preisvergleich", "Preiserhöhungen", "Einkäufe"], wb.Worksheets.Select(w => w.Name));
        // only products bought more than once: toner + paper
        Assert.Equal(2, wb.Worksheet("Preisvergleich").RangeUsed()!.RowCount() - 1);
        Assert.Equal("Toner HP 305A", wb.Worksheet("Preiserhöhungen").Cell(2, 1).GetString());
    }

    private sealed class SyncProgress(Action<ScanEvent> action) : IProgress<ScanEvent>
    {
        public void Report(ScanEvent value) => action(value);
    }
}
