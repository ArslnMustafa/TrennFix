using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using TrennFix.Core.Mail;
using TrennFix.Core.Scanning;

namespace TrennFix.Mail;

/// <summary>
/// Microsoft 365 / Exchange Online via Microsoft Graph. Sign-in happens once in the browser
/// (the token is cached encrypted for the current Windows user). Requires an Entra ID app
/// registration ("Mobile and desktop application", redirect URI http://localhost) with the
/// delegated permission Mail.Read (Mail.Read.Shared for a shared mailbox).
/// </summary>
public sealed class GraphMailSource(GraphSettings settings, IReadOnlyList<string> folders, bool includeSubfolders, IntPtr parentWindow = default)
    : IMailSource
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    private string Base => string.IsNullOrWhiteSpace(settings.SharedMailbox) ? "me" : $"users/{Uri.EscapeDataString(settings.SharedMailbox)}";

    public string DisplayName => string.IsNullOrWhiteSpace(settings.SharedMailbox) ? "Microsoft 365" : $"Microsoft 365 ({settings.SharedMailbox})";

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            throw new InvalidOperationException("Microsoft 365: In den Einstellungen fehlt die Client-ID der App-Registrierung.");

        string[] scopes = string.IsNullOrWhiteSpace(settings.SharedMailbox) ? ["Mail.Read"] : ["Mail.Read", "Mail.Read.Shared"];
        var app = PublicClientApplicationBuilder.Create(settings.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(settings.TenantId) ? "organizations" : settings.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrennFix");
        var storage = new StorageCreationPropertiesBuilder("msal.cache", cacheDir).Build();
        (await MsalCacheHelper.CreateAsync(storage)).RegisterCache(app.UserTokenCache);

        AuthenticationResult token;
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        try
        {
            token = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            var interactive = app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount);
            if (parentWindow != IntPtr.Zero) interactive = interactive.WithParentActivityOrWindow(parentWindow);
            token = await interactive.ExecuteAsync(ct);
        }
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    public async IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, DateTimeOffset? until, [EnumeratorCancellation] CancellationToken ct)
    {
        var folderList = await ResolveFoldersAsync(ct);
        var sinceIso = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var untilFilter = until is { } u ? $" and receivedDateTime lt {u.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" : "";

        foreach (var (folderId, folderName) in folderList)
        {
            string? url = $"{Base}/mailFolders/{folderId}/messages?$filter=receivedDateTime ge {sinceIso}{untilFilter} and hasAttachments eq true" +
                          "&$select=id,subject,from,receivedDateTime,internetMessageId&$orderby=receivedDateTime&$top=50";
            while (url is not null)
            {
                using var page = await GetJsonAsync(url, ct);
                foreach (var msg in page.RootElement.GetProperty("value").EnumerateArray())
                {
                    var id = msg.GetProperty("id").GetString()!;
                    var attachments = await GetAttachmentsAsync(id, ct);
                    if (attachments.Count == 0) continue;

                    var from = msg.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? f.GetProperty("emailAddress") : default;
                    yield return new MailItem
                    {
                        MessageId = MailHelpers.NormalizeMessageId(msg.TryGetProperty("internetMessageId", out var imid) ? imid.GetString() : null)
                            is { Length: > 0 } mid ? mid : $"graph:{id}",
                        Subject = msg.GetProperty("subject").GetString() ?? "",
                        FromAddress = from.ValueKind == JsonValueKind.Object ? from.GetProperty("address").GetString() ?? "" : "",
                        FromName = from.ValueKind == JsonValueKind.Object ? from.GetProperty("name").GetString() ?? "" : "",
                        Received = msg.GetProperty("receivedDateTime").GetDateTimeOffset(),
                        FolderPath = folderName,
                        Attachments = attachments,
                        OriginalExtension = ".eml",
                        SaveOriginalAsync = async (path, token) =>
                        {
                            using var resp = await SendAsync($"{Base}/messages/{id}/$value", token);
                            await using var fs = File.Create(path);
                            await resp.Content.CopyToAsync(fs, token);
                            return path;
                        },
                    };
                }
                url = page.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
        }
    }

    private async Task<List<MailAttachment>> GetAttachmentsAsync(string messageId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/messages/{messageId}/attachments", ct);
        var list = new List<MailAttachment>();
        foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (a.GetProperty("@odata.type").GetString() != "#microsoft.graph.fileAttachment") continue;
            var name = a.GetProperty("name").GetString() ?? "Anhang";
            var type = a.TryGetProperty("contentType", out var t) ? t.GetString() : null;
            if (!MailHelpers.IsRelevantAttachment(name, type)) continue;
            if (!a.TryGetProperty("contentBytes", out var bytes) || bytes.ValueKind != JsonValueKind.String) continue;
            list.Add(new MailAttachment(name, type, Convert.FromBase64String(bytes.GetString()!)));
        }
        return list;
    }

    private async Task<List<(string Id, string Name)>> ResolveFoldersAsync(CancellationToken ct)
    {
        var roots = new List<(string Id, string Name)>();
        if (folders.Count == 0)
        {
            roots.Add(("inbox", "Posteingang"));
        }
        else
        {
            foreach (var path in folders)
            {
                var segments = MailHelpers.SplitFolderPath(path);
                string? currentId = null;
                foreach (var seg in segments)
                {
                    if (currentId is null && MailHelpers.IsInboxAlias(seg)) { currentId = "inbox"; continue; }
                    var listUrl = currentId is null ? $"{Base}/mailFolders?$top=200" : $"{Base}/mailFolders/{currentId}/childFolders?$top=200";
                    using var doc = await GetJsonAsync(listUrl, ct);
                    currentId = doc.RootElement.GetProperty("value").EnumerateArray()
                        .FirstOrDefault(x => string.Equals(x.GetProperty("displayName").GetString(), seg, StringComparison.OrdinalIgnoreCase))
                        is { ValueKind: JsonValueKind.Object } match
                        ? match.GetProperty("id").GetString()
                        : throw new InvalidOperationException($"Ordner \"{path}\" nicht gefunden.");
                }
                roots.Add((currentId ?? "inbox", path));
            }
        }

        var all = new List<(string, string)>();
        foreach (var root in roots)
        {
            all.Add(root);
            if (includeSubfolders) await AddChildrenAsync(root.Id, root.Name, all, ct);
        }
        return all;
    }

    private async Task AddChildrenAsync(string id, string name, List<(string, string)> into, CancellationToken ct)
    {
        string? url = $"{Base}/mailFolders/{id}/childFolders?$top=100&$select=id,displayName,childFolderCount";
        while (url is not null)
        {
            using var doc = await GetJsonAsync(url, ct);
            foreach (var child in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var childId = child.GetProperty("id").GetString()!;
                var childName = $"{name}/{child.GetProperty("displayName").GetString()}";
                into.Add((childId, childName));
                if (child.TryGetProperty("childFolderCount", out var cnt) && cnt.GetInt32() > 0)
                    await AddChildrenAsync(childId, childName, into, ct);
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(url, ct);
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) return resp;
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new HttpRequestException($"Microsoft Graph {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
