using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using TrennFix.Core.Mail;
using TrennFix.Core.Scanning;

namespace TrennFix.Mail;

/// <summary>Any IMAP mailbox (IONOS, Strato, GMX, Exchange with IMAP enabled …).</summary>
public sealed class ImapMailSource(ImapSettings settings, string password, IReadOnlyList<string> folders, bool includeSubfolders) : IMailSource
{
    private readonly ImapClient _client = new();

    public string DisplayName => $"IMAP {settings.UserName}@{settings.Host}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        var ssl = settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await _client.ConnectAsync(settings.Host, settings.Port, ssl, ct);
        await _client.AuthenticateAsync(settings.UserName, password, ct);
    }

    public async IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, DateTimeOffset? until, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var folder in await ResolveFoldersAsync(ct))
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            // IMAP SINCE has day granularity → search one day earlier, filter exactly below.
            SearchQuery query = SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1));
            if (until is { } u) query = query.And(SearchQuery.DeliveredBefore(u.UtcDateTime.Date.AddDays(1)));
            var uids = await folder.SearchAsync(query, ct);
            if (uids.Count == 0) continue;

            var summaries = await folder.FetchAsync(uids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate, ct);

            foreach (var summary in summaries.OrderBy(s => s.InternalDate))
            {
                var received = summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue;
                if (received < since || until is { } end && received >= end) continue;

                var parts = (summary.BodyParts ?? [])
                    .Where(p => MailHelpers.IsRelevantAttachment(p.FileName, p.ContentType?.MimeType))
                    .ToList();
                if (parts.Count == 0) continue;

                var attachments = new List<MailAttachment>();
                foreach (var part in parts)
                {
                    if (await folder.GetBodyPartAsync(summary.UniqueId, part, ct) is not MimePart { Content: not null } mime) continue;
                    using var ms = new MemoryStream();
                    await mime.Content.DecodeToAsync(ms, ct);
                    attachments.Add(new MailAttachment(mime.FileName ?? part.FileName ?? "Anhang.pdf", mime.ContentType.MimeType, ms.ToArray()));
                }

                var from = summary.Envelope?.From.Mailboxes.FirstOrDefault();
                var uid = summary.UniqueId;
                var f = folder;
                yield return new MailItem
                {
                    MessageId = MailHelpers.NormalizeMessageId(summary.Envelope?.MessageId) is { Length: > 0 } mid
                        ? mid : $"imap:{folder.FullName}:{folder.UidValidity}:{uid.Id}",
                    Subject = summary.Envelope?.Subject ?? "",
                    FromAddress = from?.Address ?? "",
                    FromName = from?.Name ?? "",
                    Received = received,
                    FolderPath = folder.FullName,
                    Attachments = attachments,
                    OriginalExtension = ".eml",
                    SaveOriginalAsync = async (path, token) =>
                    {
                        if (!f.IsOpen) await f.OpenAsync(FolderAccess.ReadOnly, token);
                        var message = await f.GetMessageAsync(uid, token);
                        await message.WriteToAsync(path, token);
                        return path;
                    },
                };
            }
        }
    }

    private async Task<List<IMailFolder>> ResolveFoldersAsync(CancellationToken ct)
    {
        var roots = new List<IMailFolder>();
        if (folders.Count == 0)
        {
            roots.Add(_client.Inbox);
        }
        else
        {
            var personal = _client.GetFolder(_client.PersonalNamespaces[0]);
            foreach (var path in folders)
            {
                var segments = MailHelpers.SplitFolderPath(path);
                IMailFolder current = segments.Length > 0 && MailHelpers.IsInboxAlias(segments[0]) ? _client.Inbox : personal;
                foreach (var seg in segments.Skip(MailHelpers.IsInboxAlias(segments.FirstOrDefault() ?? "") ? 1 : 0))
                {
                    var subs = await current.GetSubfoldersAsync(false, ct);
                    current = subs.FirstOrDefault(s => s.Name.Equals(seg, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidOperationException($"IMAP-Ordner \"{path}\" nicht gefunden.");
                }
                roots.Add(current);
            }
        }

        var all = new List<IMailFolder>();
        foreach (var root in roots)
        {
            all.Add(root);
            if (includeSubfolders) await AddSubfoldersAsync(root, all, ct);
        }
        return all.DistinctBy(f => f.FullName).ToList();
    }

    private static async Task AddSubfoldersAsync(IMailFolder folder, List<IMailFolder> into, CancellationToken ct)
    {
        foreach (var sub in await folder.GetSubfoldersAsync(false, ct))
        {
            if (!sub.Exists || sub.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
            into.Add(sub);
            await AddSubfoldersAsync(sub, into, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected) await _client.DisconnectAsync(true);
        _client.Dispose();
    }
}
