using System.Text.Json;
using System.Text.Json.Serialization;
using TrennFix.Core.Extraction;

namespace TrennFix.Core.Scanning;

public enum MailSourceKind
{
    /// <summary>Classic Outlook installed on this PC (works with every account type in Outlook).</summary>
    OutlookDesktop = 0,
    /// <summary>Microsoft 365 / Exchange Online via Microsoft Graph (no Outlook needed).</summary>
    Microsoft365 = 1,
    /// <summary>Any IMAP mailbox (IONOS, Strato, GMX, …).</summary>
    Imap = 2,
}

public sealed class ImapSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    /// <summary>Encrypted with Windows DPAPI (current Windows user) – never stored in plain text.</summary>
    public string ProtectedPassword { get; set; } = "";
}

public sealed class GraphSettings
{
    /// <summary>Application (client) ID of an Entra ID app registration with delegated Mail.Read permission.</summary>
    public string ClientId { get; set; } = "";
    public string TenantId { get; set; } = "organizations";
    /// <summary>Optional shared mailbox (e.g. rechnungen@firma.de). Empty = own mailbox.</summary>
    public string SharedMailbox { get; set; } = "";
}

public sealed class OutlookSettings
{
    /// <summary>Name of the account / data file in Outlook (empty = default store).</summary>
    public string StoreName { get; set; } = "";
}

public sealed class AppSettings
{
    public string ArchiveRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rechnungen");

    public MailSourceKind MailSource { get; set; } = MailSourceKind.OutlookDesktop;
    public ImapSettings Imap { get; set; } = new();
    public GraphSettings Graph { get; set; } = new();
    public OutlookSettings Outlook { get; set; } = new();

    /// <summary>Folders to scan, e.g. "Posteingang". Empty = inbox. Subfolders are always included.</summary>
    public List<string> MailFolders { get; set; } = [];
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Start date for the very first scan.</summary>
    public DateTimeOffset InitialScanFrom { get; set; } = DateTimeOffset.Now.AddDays(-30);
    /// <summary>Set after every successful scan – the next scan continues from here.</summary>
    public DateTimeOffset? LastScanUtc { get; set; }

    public List<string> OwnCompanyNames { get; set; } = [];
    public List<string> OwnIbans { get; set; } = [];
    public List<string> OwnVatIds { get; set; } = [];

    public bool SaveOriginalMail { get; set; } = true;
    /// <summary>false (default): invoices are only listed, the "PDFs speichern" button writes them to the archive.</summary>
    public bool AutoSaveDocuments { get; set; }
    /// <summary>Warn when an item costs more than this percentage above the last purchase.</summary>
    public decimal PriceAlertPercent { get; set; } = 3m;
    public bool OcrEnabled { get; set; } = true;
    public string OcrLanguage { get; set; } = "de-DE";

    [JsonIgnore]
    public string DatabasePath => Path.Combine(ArchiveRoot, "trennfix.db");

    public ExtractionOptions ToExtractionOptions() => new()
    {
        OwnCompanyNames = OwnCompanyNames,
        OwnIbans = OwnIbans,
        OwnVatIds = OwnVatIds,
    };

    // ------------------------------------------------------------------ persistence

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrennFix", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
