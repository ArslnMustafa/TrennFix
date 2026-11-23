using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TrennFix.App.Services;
using TrennFix.Core.Export;
using TrennFix.Core.Mail;
using TrennFix.Core.Models;
using TrennFix.Core.Scanning;
using TrennFix.Core.Storage;
using TrennFix.Mail;

namespace TrennFix.App;

public sealed record LogLine(string Text, Brush Brush);

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private InvoiceDatabase? _db;
    private readonly ObservableCollection<InvoiceRow> _rows = [];
    private readonly ObservableCollection<LogLine> _log = [];
    private readonly HashSet<long> _lastScanIds = [];
    private ICollectionView? _view;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
    }

    // ------------------------------------------------------------------ startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppSettings.DefaultPath))
        {
            MessageBox.Show(this,
                "Willkommen bei TrennFix!\n\nBitte zuerst Postfach, Archivordner und die Daten der eigenen Firma eintragen " +
                "(eigene IBAN / USt-IdNr. werden benötigt, damit sie nicht für die des Lieferanten gehalten werden).",
                "Ersteinrichtung", MessageBoxButton.OK, MessageBoxImage.Information);
            if (!ShowSettings()) { Close(); return; }
        }
        OpenDatabase();
    }

    private void OpenDatabase()
    {
        _db?.Dispose();
        Directory.CreateDirectory(_settings.ArchiveRoot);
        _db = new InvoiceDatabase(_settings.DatabasePath);

        _rows.Clear();
        foreach (var r in _db.GetInvoices()) _rows.Add(new InvoiceRow(r));
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is InvoiceRow row && FilterRow(row);
        Grid.ItemsSource = _view;
        ResetDateRange();
        UpdateStatus();
    }

    /// <summary>Default period: from the last scan until today.</summary>
    private void ResetDateRange()
    {
        FromDate.SelectedDate = ScanService.DefaultFrom(_settings).LocalDateTime.Date;
        ToDate.SelectedDate = DateTime.Today;
    }

    private InvoiceArchiver Archiver => new(_db!, new ArchiveService(_settings.ArchiveRoot));

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cts is not null)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Bitte warten, bis der Abruf beendet ist (oder \"Abbrechen\" klicken).", "TrennFix");
            return;
        }
        _db?.Dispose();
    }

    // ------------------------------------------------------------------ scan

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        var fromDay = FromDate.SelectedDate ?? ScanService.DefaultFrom(_settings).LocalDateTime.Date;
        var toDay = ToDate.SelectedDate ?? DateTime.Today;
        if (toDay < fromDay)
        {
            MessageBox.Show(this, "Das Bis-Datum liegt vor dem Von-Datum.", "Zeitraum");
            return;
        }
        // Default start keeps the exact time of the last scan; a manually chosen day starts at 00:00.
        var defaultFrom = ScanService.DefaultFrom(_settings);
        var from = fromDay == defaultFrom.LocalDateTime.Date ? defaultFrom : new DateTimeOffset(fromDay);
        // Up to today → open end (and the scan position is remembered). Earlier day → end of that day.
        DateTimeOffset? until = toDay >= DateTime.Today ? null : new DateTimeOffset(toDay.AddDays(1));

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _log.Clear();
        BottomTabs.SelectedIndex = 1; // show the log while scanning
        _lastScanIds.Clear();

        var progress = new Progress<ScanEvent>(ev =>
        {
            AddLog(ev.Level, ev.Message);
            if (ev.Record is { } rec)
            {
                _rows.Insert(0, new InvoiceRow(rec));
                _lastScanIds.Add(rec.Id);
                UpdateStatus();
            }
        });

        var db = _db;
        var settings = _settings;
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var ocr = settings.OcrEnabled ? new WindowsOcrEngine(settings.OcrLanguage) : null;
            if (ocr is not null && !WindowsOcrEngine.IsLanguageAvailable(settings.OcrLanguage))
                AddLog(ScanEventLevel.Warning, $"Hinweis: OCR-Sprache {settings.OcrLanguage} ist in Windows nicht installiert – gescannte PDFs werden evtl. schlecht erkannt.");

            var service = new ScanService(settings, db, ocr, s => s.Save());
            var ct = _cts.Token;
            var summary = await Task.Run(async () =>
            {
                await using var source = CreateMailSource(settings, db, hwnd);
                return await service.RunAsync(source, from, until, progress, ct);
            }, ct);

            if (!summary.Cancelled && summary.InvoicesImported > 0)
            {
                FilterBox.SelectedIndex = 5; // "Letzter Abruf"
                var saveHint = settings.AutoSaveDocuments ? "" : "\n\nDie PDFs sind noch nicht im Archiv – dafür \"PDFs speichern\" klicken.";
                MessageBox.Show(this,
                    $"{summary.InvoicesImported} Rechnungen gefunden." +
                    (summary.Warnings > 0 ? $"\n{summary.Warnings} davon bitte prüfen (gelb markiert)." : "") + saveHint,
                    "Abruf fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(ScanEventLevel.Warning, "Abgebrochen.");
        }
        catch (Exception ex)
        {
            AddLog(ScanEventLevel.Error, "Abruf fehlgeschlagen: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Abruf fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateStatus();
        }
    }

    private static IMailSource CreateMailSource(AppSettings s, InvoiceDatabase db, IntPtr hwnd) => s.MailSource switch
    {
        MailSourceKind.OutlookDesktop => new OutlookMailSource(s.Outlook, s.MailFolders, s.IncludeSubfolders, db.IsMessageProcessed, s.SaveOriginalMail),
        MailSourceKind.Microsoft365 => new GraphMailSource(s.Graph, s.MailFolders, s.IncludeSubfolders, hwnd),
        MailSourceKind.Imap => new ImapMailSource(s.Imap, SecretProtector.Unprotect(s.Imap.ProtectedPassword), s.MailFolders, s.IncludeSubfolders),
        _ => throw new NotSupportedException(),
    };

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = busy;
        foreach (var b in new[] { ConfirmIbanButton, PaidButton, CheckedButton, SaveDocsButton, DiscardButton }) b.IsEnabled = !busy;
        FromDate.IsEnabled = ToDate.IsEnabled = !busy;
    }

    private void AddLog(ScanEventLevel level, string text)
    {
        var brush = level switch
        {
            ScanEventLevel.Success => Brushes.DarkGreen,
            ScanEventLevel.Warning => Brushes.DarkOrange,
            ScanEventLevel.Error => Brushes.Firebrick,
            _ => Brushes.DimGray,
        };
        _log.Add(new LogLine($"{DateTime.Now:HH:mm:ss}  {text}", brush));
        LogList.ScrollIntoView(_log[^1]);
    }

    // ------------------------------------------------------------------ filter / status

    private bool FilterRow(InvoiceRow row)
    {
        if (!row.Matches(SearchBox.Text)) return false;
        var r = row.Record;
        return FilterBox.SelectedIndex switch
        {
            1 => r.Status == InvoiceStatus.Pruefen,
            2 => r.Data.PaymentMethod == PaymentMethod.Ueberweisung && r.Status is InvoiceStatus.Ok or InvoiceStatus.Pruefen
                 && r.Data.Kind is DocumentKind.Rechnung or DocumentKind.Mahnung,
            3 => r.Data.PaymentMethod == PaymentMethod.Lastschrift,
            4 => !r.Printed && r.Status != InvoiceStatus.Duplikat,
            5 => _lastScanIds.Contains(r.Id),
            6 => !r.IsSaved,
            _ => true,
        };
    }

    private void OnFilterChanged(object sender, EventArgs e) => _view?.Refresh();

    private void UpdateStatus()
    {
        var all = _rows.Select(r => r.Record).ToList();
        var open = all.Where(r => r.Data.PaymentMethod == PaymentMethod.Ueberweisung && r.Status is InvoiceStatus.Ok or InvoiceStatus.Pruefen
                                  && r.Data.Kind == DocumentKind.Rechnung).ToList();
        var check = all.Count(r => r.Status == InvoiceStatus.Pruefen);
        var unsaved = all.Count(r => !r.IsSaved);
        SaveDocsButton.Content = unsaved > 0 ? $"💾  PDFs speichern ({unsaved})" : "💾  PDFs speichern";
        StatusText.Text = $"{all.Count} Belege ({unsaved} nicht gespeichert)   ·   offen per Überweisung: {open.Count} ({open.Sum(r => r.Data.GrossAmount ?? 0):N2} €)   ·   zu prüfen: {check}";
        LastScanText.Text = _settings.LastScanUtc is { } ls
            ? $"Letzter Abruf: {ls.LocalDateTime:dd.MM.yyyy HH:mm}"
            : $"Erster Abruf ab {_settings.InitialScanFrom.LocalDateTime:dd.MM.yyyy}";
    }

    // ------------------------------------------------------------------ row actions

    private List<InvoiceRow> Selected => Grid.SelectedItems.Cast<InvoiceRow>().ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var one = Selected.FirstOrDefault();
        MailButton.IsEnabled = one is not null && (one.Record.MailFilePath is { } p && File.Exists(p) || !one.Record.IsSaved);
        FolderButton.IsEnabled = one?.Record.IsSaved == true;

        LinesGrid.ItemsSource = one?.Lines;
        LinesInfo.Text = one is null || one.Lines.Count == 0
            ? (one is null ? "" : "Keine Positionen erkannt.")
            : $"{one.Lines.Count} Positionen, Summe {one.Lines.Sum(l => l.LineTotal ?? 0):N2} € – Netto laut Rechnung {one.Record.Data.NetAmount:N2} €" +
              (one.Record.Data.LinesVerified ? "  ✓ stimmt überein" : "  ? weicht ab – Positionen bitte prüfen, sie lösen keine Preiswarnung aus");
        if (_cts is null && one is not null) BottomTabs.SelectedIndex = 0;
        ConfirmIbanButton.Visibility = Selected.Any(r => r.Record.Warnings.Any(w => w.StartsWith("ACHTUNG: IBAN", StringComparison.Ordinal)))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnOpen(sender, e);

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected.Take(5))
            if (DocumentPath(row.Record) is { } path) ShellOpen(path);
    }

    private void OnOpenMail(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } row) return;
        var path = row.Record.IsSaved ? row.Record.MailFilePath : Archiver.WritePreview(row.Record, originalMail: true);
        if (path is null) MessageBox.Show(this, "Die Original-Mail wurde nicht mit abgelegt.", "Mail");
        else ShellOpen(path);
    }

    /// <summary>Archived file, or a temporary copy for invoices that are not saved yet.</summary>
    private string? DocumentPath(InvoiceRecord r) => r.IsSaved ? r.FilePath : Archiver.WritePreview(r);

    private void OnSaveDocuments(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => !r.Record.IsSaved).ToList();
        if (rows.Count == 0)
        {
            rows = _view!.Cast<InvoiceRow>().Where(r => !r.Record.IsSaved).ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Alle angezeigten Rechnungen sind bereits gespeichert.", "PDFs speichern");
                return;
            }
            if (MessageBox.Show(this, $"Alle {rows.Count} angezeigten, noch nicht gespeicherten Rechnungen in\n{_settings.ArchiveRoot}\nablegen?",
                    "PDFs speichern", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }

        var archiver = Archiver;
        var saved = 0;
        foreach (var row in rows)
        {
            try
            {
                if (archiver.Save(row.Record)) saved++;
                row.Update(row.Record);
            }
            catch (Exception ex)
            {
                AddLog(ScanEventLevel.Error, $"{row.Supplier} {row.InvoiceNumber}: {ex.Message}");
            }
        }
        AddLog(ScanEventLevel.Success, $"{saved} PDF(s) im Archiv gespeichert.");
        UpdateStatus();
        _view?.Refresh();
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        var count = _rows.Count(r => !r.Record.IsSaved);
        if (count == 0) return;
        if (MessageBox.Show(this,
                $"{count} nicht gespeicherte Einträge aus der Liste entfernen?\n\n" +
                "Gespeicherte PDFs bleiben unverändert. Die betroffenen Mails werden beim nächsten Abruf erneut gelesen.",
                "Verwerfen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _db!.DiscardUnsaved();
        _lastScanIds.Clear();
        var keepFrom = FromDate.SelectedDate;
        var keepTo = ToDate.SelectedDate;
        OpenDatabase();
        FromDate.SelectedDate = keepFrom;
        ToDate.SelectedDate = keepTo;
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { IsSavedRow: true } row && File.Exists(row.Record.FilePath))
            Process.Start("explorer.exe", $"/select,\"{row.Record.FilePath}\"");
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        if (rows.Count > 1 && MessageBox.Show(this, $"{rows.Count} Belege drucken?", "Drucken", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        foreach (var row in rows)
        {
            try
            {
                var path = DocumentPath(row.Record) ?? throw new FileNotFoundException();
                Process.Start(new ProcessStartInfo(path) { Verb = "print", UseShellExecute = true });
                _db!.MarkPrinted(row.Record.Id);
                row.Record.Printed = true;
                row.Update(row.Record);
            }
            catch (Win32Exception)
            {
                MessageBox.Show(this,
                    "Das Standard-PDF-Programm unterstützt kein direktes Drucken.\nDie Datei wird geöffnet – bitte dort drucken.",
                    "Drucken", MessageBoxButton.OK, MessageBoxImage.Information);
                if (DocumentPath(row.Record) is { } p) ShellOpen(p);
                break;
            }
        }
    }

    private void OnConfirmIban(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Record.Data.Iban is not null).ToList();
        if (rows.Count == 0) return;
        if (MessageBox.Show(this,
                "Haben Sie die neue Bankverbindung beim Lieferanten (telefonisch, über eine bekannte Nummer) bestätigt?\n\n" +
                string.Join("\n", rows.Select(r => $"{r.Supplier}: {r.Iban}")),
                "IBAN bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var row in rows)
        {
            _db!.ConfirmIban(row.Record.Id);
            Refresh(row);
        }
        UpdateStatus();
    }

    private void OnMarkPaid(object sender, RoutedEventArgs e) => SetStatus(InvoiceStatus.Bezahlt);
    private void OnMarkChecked(object sender, RoutedEventArgs e) => SetStatus(InvoiceStatus.Ok);

    private void SetStatus(InvoiceStatus status)
    {
        foreach (var row in Selected)
        {
            _db!.UpdateStatus(row.Record.Id, status);
            Refresh(row);
        }
        UpdateStatus();
        _view?.Refresh();
    }

    private void Refresh(InvoiceRow row)
    {
        if (_db!.GetInvoice(row.Record.Id) is { } fresh) row.Update(fresh);
    }

    // ------------------------------------------------------------------ toolbar right

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel-Datei (*.xlsx)|*.xlsx",
            FileName = $"Rechnungen_{DateTime.Today:yyyy-MM-dd}.xlsx",
        };
        if (dlg.ShowDialog(this) != true) return;
        var rows = _view!.Cast<InvoiceRow>().Select(r => r.Record).ToList();
        ExcelExporter.Export(rows, dlg.FileName);
        ShellOpen(dlg.FileName);
    }

    private void OnPriceList(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        var history = _db.GetPriceHistory();
        if (history.Count == 0)
        {
            MessageBox.Show(this, "Es wurden noch keine Rechnungspositionen erkannt.", "Preisliste");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Excel-Datei (*.xlsx)|*.xlsx",
            FileName = $"Preisliste_{DateTime.Today:yyyy-MM-dd}.xlsx",
        };
        if (dlg.ShowDialog(this) != true) return;
        PriceListExporter.Export(history, dlg.FileName, _settings.PriceAlertPercent);
        ShellOpen(dlg.FileName);
    }

    private void OnOpenArchive(object sender, RoutedEventArgs e) => ShellOpen(_settings.ArchiveRoot);

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var oldRoot = _settings.ArchiveRoot;
        if (ShowSettings() && !string.Equals(oldRoot, _settings.ArchiveRoot, StringComparison.OrdinalIgnoreCase))
            OpenDatabase();
        UpdateStatus();
    }

    private bool ShowSettings()
    {
        var copy = AppSettings.Load();
        var win = new SettingsWindow(copy) { Owner = IsLoaded ? this : null };
        if (win.ShowDialog() != true) return false;
        copy.Save();
        _settings = copy;
        return true;
    }

    private static void ShellOpen(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
