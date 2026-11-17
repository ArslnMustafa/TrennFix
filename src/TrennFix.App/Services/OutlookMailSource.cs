using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TrennFix.Core.Mail;
using TrennFix.Core.Scanning;

namespace TrennFix.App.Services;

/// <summary>
/// Reads mails from the classic Outlook desktop app installed on this PC (COM automation).
/// Works with every account type configured in Outlook (Exchange, IMAP, POP3/PST).
///
/// Note: the *new* Outlook ("Outlook (new)") has no COM interface – use Microsoft 365 or IMAP there.
/// All COM calls run on one dedicated STA thread; mails are collected first and then handed out.
/// </summary>
public sealed class OutlookMailSource(OutlookSettings settings, IReadOnlyList<string> folders, bool includeSubfolders,
    Func<string, bool> alreadyProcessed, bool saveOriginal) : IMailSource
{
    private const int OlFolderInbox = 6;
    private const int OlMailItemClass = 43;
    private const int OlByValue = 1;
    private const int OlMsgUnicode = 9;
    private const string PrInternetMessageId = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "TrennFix", Guid.NewGuid().ToString("N"));

    public string DisplayName => "Outlook (Desktop)";

    public Task ConnectAsync(CancellationToken ct)
    {
        if (Type.GetTypeFromProgID("Outlook.Application") is null)
            throw new InvalidOperationException(
                "Klassisches Outlook wurde auf diesem PC nicht gefunden. Das neue Outlook unterstützt keine Automatisierung – " +
                "bitte in den Einstellungen \"Microsoft 365\" oder \"IMAP\" wählen.");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, DateTimeOffset? until, [EnumeratorCancellation] CancellationToken ct)
    {
        Directory.CreateDirectory(_tempDir);
        var items = await RunOnStaThread(() => Collect(since, until, ct));
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private List<MailItem> Collect(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
    {
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)!;
        dynamic ns = app.GetNamespace("MAPI");
        var result = new List<MailItem>();
        var seen = new HashSet<string>();

        List<(dynamic Folder, string Path)> resolved = ResolveFolders(ns);
        foreach (var (folder, path) in resolved)
        {
            ct.ThrowIfCancellationRequested();
            dynamic items = folder.Items;
            // DASL filter values are UTC; one day of slack, exact filter below.
            var filter = "@SQL=\"urn:schemas:httpmail:datereceived\" >= '" +
                         since.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
                         "' AND \"urn:schemas:httpmail:hasattachment\" = 1" +
                         (until is { } u
                             ? " AND \"urn:schemas:httpmail:datereceived\" < '" +
                               u.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "'"
                             : "");
            dynamic restricted = items.Restrict(filter);
            var count = (int)restricted.Count;
            for (var i = 1; i <= count; i++)
            {
                ct.ThrowIfCancellationRequested();
                dynamic mail = restricted[i];
                try
                {
                    if ((int)mail.Class != OlMailItemClass) continue;
                    DateTime receivedLocal = mail.ReceivedTime;
                    var received = new DateTimeOffset(receivedLocal);
                    if (received < since || until is { } end && received >= end) continue;

                    string messageId = "";
                    try { messageId = MailHelpers.NormalizeMessageId((string)mail.PropertyAccessor.GetProperty(PrInternetMessageId)); }
                    catch (COMException) { }
                    if (messageId.Length == 0) messageId = "outlook:" + (string)mail.EntryID;
                    if (!seen.Add(messageId) || alreadyProcessed(messageId)) continue;

                    var attachments = new List<MailAttachment>();
                    dynamic atts = mail.Attachments;
                    var attCount = (int)atts.Count;
                    for (var a = 1; a <= attCount; a++)
                    {
                        dynamic att = atts[a];
                        try
                        {
                            if ((int)att.Type != OlByValue) continue;
                            string fileName = att.FileName;
                            if (!MailHelpers.IsRelevantAttachment(fileName, null)) continue;
                            var tmp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
                            att.SaveAsFile(tmp);
                            attachments.Add(new MailAttachment(fileName, null, File.ReadAllBytes(tmp)));
                            File.Delete(tmp);
                        }
                        finally { Marshal.ReleaseComObject(att); }
                    }
                    Marshal.ReleaseComObject(atts);
                    if (attachments.Count == 0) continue;

                    string? msgFile = null;
                    if (saveOriginal)
                    {
                        msgFile = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".msg");
                        try { mail.SaveAs(msgFile, OlMsgUnicode); } catch (COMException) { msgFile = null; }
                    }

                    result.Add(new MailItem
                    {
                        MessageId = messageId,
                        Subject = (string?)mail.Subject ?? "",
                        FromAddress = (string)SenderAddress(mail),
                        FromName = (string?)mail.SenderName ?? "",
                        Received = received,
                        FolderPath = path,
                        Attachments = attachments,
                        OriginalExtension = ".msg",
                        SaveOriginalAsync = msgFile is null ? null : (target, _) =>
                        {
                            File.Copy(msgFile, target, overwrite: false);
                            return Task.FromResult(target);
                        },
                    });
                }
                finally
                {
                    Marshal.ReleaseComObject(mail);
                }
            }
            Marshal.ReleaseComObject(restricted);
            Marshal.ReleaseComObject(items);
        }
        return result;
    }

    private static string SenderAddress(dynamic mail)
    {
        try
        {
            if ((string?)mail.SenderEmailType == "EX")
            {
                dynamic? exUser = mail.Sender?.GetExchangeUser();
                if (exUser is not null) return (string)exUser.PrimarySmtpAddress;
            }
            return (string?)mail.SenderEmailAddress ?? "";
        }
        catch (COMException)
        {
            return "";
        }
    }

    private List<(dynamic Folder, string Path)> ResolveFolders(dynamic ns)
    {
        dynamic inbox;
        if (string.IsNullOrWhiteSpace(settings.StoreName))
        {
            inbox = ns.GetDefaultFolder(OlFolderInbox);
        }
        else
        {
            dynamic? store = null;
            foreach (dynamic s in ns.Stores)
                if (string.Equals((string)s.DisplayName, settings.StoreName, StringComparison.OrdinalIgnoreCase)) { store = s; break; }
            if (store is null) throw new InvalidOperationException($"Outlook-Konto \"{settings.StoreName}\" nicht gefunden.");
            inbox = store.GetDefaultFolder(OlFolderInbox);
        }

        var roots = new List<(dynamic, string)>();
        if (folders.Count == 0)
        {
            roots.Add((inbox, (string)inbox.Name));
        }
        else
        {
            dynamic root = inbox.Parent;
            foreach (var path in folders)
            {
                var segments = MailHelpers.SplitFolderPath(path);
                dynamic current = root;
                var start = 0;
                if (segments.Length > 0 && (MailHelpers.IsInboxAlias(segments[0]) || string.Equals(segments[0], (string)inbox.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    current = inbox;
                    start = 1;
                }
                for (var i = start; i < segments.Length; i++)
                {
                    dynamic? next = null;
                    foreach (dynamic f in current.Folders)
                        if (string.Equals((string)f.Name, segments[i], StringComparison.OrdinalIgnoreCase)) { next = f; break; }
                    current = next ?? throw new InvalidOperationException($"Outlook-Ordner \"{path}\" nicht gefunden.");
                }
                roots.Add((current, path));
            }
        }

        var all = new List<(dynamic, string)>();
        foreach (var (folder, name) in roots)
        {
            all.Add((folder, name));
            if (includeSubfolders) AddSubfolders(folder, name, all);
        }
        return all;
    }

    private static void AddSubfolders(dynamic folder, string path, List<(dynamic, string)> into)
    {
        foreach (dynamic sub in folder.Folders)
        {
            var subPath = $"{path}/{(string)sub.Name}";
            // only mail folders (skip calendar/contacts below the inbox)
            if ((int)sub.DefaultItemType != 0) continue;
            into.Add((sub, subPath));
            AddSubfolders(sub, subPath, into);
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Outlook COM" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
