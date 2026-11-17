using System.Windows;
using Microsoft.Win32;
using TrennFix.App.Services;
using TrennFix.Core.Extraction;
using TrennFix.Core.Scanning;

namespace TrennFix.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;

        SrcOutlook.IsChecked = _s.MailSource == MailSourceKind.OutlookDesktop;
        SrcGraph.IsChecked = _s.MailSource == MailSourceKind.Microsoft365;
        SrcImap.IsChecked = _s.MailSource == MailSourceKind.Imap;

        OutlookStore.Text = _s.Outlook.StoreName;
        GraphClientId.Text = _s.Graph.ClientId;
        GraphTenant.Text = _s.Graph.TenantId;
        GraphShared.Text = _s.Graph.SharedMailbox;
        ImapHost.Text = _s.Imap.Host;
        ImapPort.Text = _s.Imap.Port.ToString();
        ImapSsl.IsChecked = _s.Imap.UseSsl;
        ImapUser.Text = _s.Imap.UserName;
        ImapPassword.Password = SecretProtector.Unprotect(_s.Imap.ProtectedPassword);

        MailFolders.Text = string.Join(Environment.NewLine, _s.MailFolders);
        IncludeSub.IsChecked = _s.IncludeSubfolders;
        ArchiveRoot.Text = _s.ArchiveRoot;
        SaveMail.IsChecked = _s.SaveOriginalMail;
        OcrEnabled.IsChecked = _s.OcrEnabled;
        InitialFrom.SelectedDate = _s.InitialScanFrom.LocalDateTime.Date;
        LastScanInfo.Text = _s.LastScanUtc is { } ls ? $"Letzter Abruf: {ls.LocalDateTime:dd.MM.yyyy HH:mm}" : "Noch kein Abruf.";

        OwnNames.Text = string.Join(Environment.NewLine, _s.OwnCompanyNames);
        OwnIbans.Text = string.Join(Environment.NewLine, _s.OwnIbans.Select(GermanFormats.FormatIban));
        OwnVat.Text = string.Join(Environment.NewLine, _s.OwnVatIds);
        OnSourceChanged(this, new RoutedEventArgs());
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (OutlookGroup is null) return; // during InitializeComponent
        OutlookGroup.Visibility = SrcOutlook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GraphGroup.Visibility = SrcGraph.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImapGroup.Visibility = SrcImap.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Archivordner wählen", InitialDirectory = ArchiveRoot.Text };
        if (dlg.ShowDialog(this) == true) ArchiveRoot.Text = dlg.FolderName;
    }

    private void OnResetScan(object sender, RoutedEventArgs e)
    {
        _s.LastScanUtc = null;
        LastScanInfo.Text = "Nächster Abruf startet ab dem oben gewählten Datum.";
    }

    private static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var ibans = Lines(OwnIbans.Text);
        var invalid = ibans.Where(i => !GermanFormats.IsValidIban(i)).ToList();
        if (invalid.Count > 0)
        {
            MessageBox.Show(this, "Ungültige IBAN:\n" + string.Join("\n", invalid), "Einstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(ArchiveRoot.Text))
        {
            MessageBox.Show(this, "Bitte einen Archivordner wählen.", "Einstellungen");
            return;
        }
        if (SrcImap.IsChecked == true && (string.IsNullOrWhiteSpace(ImapHost.Text) || !int.TryParse(ImapPort.Text, out _)))
        {
            MessageBox.Show(this, "Bitte IMAP-Server und Port angeben.", "Einstellungen");
            return;
        }

        _s.MailSource = SrcGraph.IsChecked == true ? MailSourceKind.Microsoft365
            : SrcImap.IsChecked == true ? MailSourceKind.Imap
            : MailSourceKind.OutlookDesktop;
        _s.Outlook.StoreName = OutlookStore.Text.Trim();
        _s.Graph.ClientId = GraphClientId.Text.Trim();
        _s.Graph.TenantId = string.IsNullOrWhiteSpace(GraphTenant.Text) ? "organizations" : GraphTenant.Text.Trim();
        _s.Graph.SharedMailbox = GraphShared.Text.Trim();
        _s.Imap.Host = ImapHost.Text.Trim();
        _s.Imap.Port = int.TryParse(ImapPort.Text, out var port) ? port : 993;
        _s.Imap.UseSsl = ImapSsl.IsChecked == true;
        _s.Imap.UserName = ImapUser.Text.Trim();
        _s.Imap.ProtectedPassword = SecretProtector.Protect(ImapPassword.Password);

        _s.MailFolders = Lines(MailFolders.Text);
        _s.IncludeSubfolders = IncludeSub.IsChecked == true;
        _s.ArchiveRoot = ArchiveRoot.Text.Trim();
        _s.SaveOriginalMail = SaveMail.IsChecked == true;
        _s.OcrEnabled = OcrEnabled.IsChecked == true;
        if (InitialFrom.SelectedDate is { } d) _s.InitialScanFrom = new DateTimeOffset(d);

        _s.OwnCompanyNames = Lines(OwnNames.Text);
        _s.OwnIbans = ibans.Select(GermanFormats.NormalizeIban).ToList();
        _s.OwnVatIds = Lines(OwnVat.Text).Select(v => v.Replace(" ", "").ToUpperInvariant()).ToList();

        DialogResult = true;
    }
}
