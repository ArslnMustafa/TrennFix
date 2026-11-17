namespace TrennFix.Core.Mail;

public sealed record MailAttachment(string FileName, string? ContentType, byte[] Content)
{
    public bool IsPdf =>
        FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || Content.Length > 4 && Content[0] == '%' && Content[1] == 'P' && Content[2] == 'D' && Content[3] == 'F';

    public bool IsXml =>
        FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || (ContentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>One e-mail with its (PDF/XML) attachments.</summary>
public sealed class MailItem
{
    /// <summary>Stable id: Internet Message-ID header if available, otherwise the source-specific id.</summary>
    public required string MessageId { get; init; }
    public required string Subject { get; init; }
    public required string FromAddress { get; init; }
    public required string FromName { get; init; }
    public required DateTimeOffset Received { get; init; }
    public string FolderPath { get; init; } = "";
    public required IReadOnlyList<MailAttachment> Attachments { get; init; }

    /// <summary>Saves the original message (.eml / .msg) – optional, for the archive.</summary>
    public Func<string, CancellationToken, Task<string>>? SaveOriginalAsync { get; init; }
    /// <summary>File extension used by <see cref="SaveOriginalAsync"/> (".eml" or ".msg").</summary>
    public string OriginalExtension { get; init; } = ".eml";
}

/// <summary>A mailbox: Outlook desktop, Microsoft 365 (Graph) or IMAP.</summary>
public interface IMailSource : IAsyncDisposable
{
    string DisplayName { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// All messages received at or after <paramref name="since"/> that have at least one PDF or XML
    /// attachment, in the configured folders (incl. subfolders – mails may already have been moved).
    /// </summary>
    IAsyncEnumerable<MailItem> GetMessagesAsync(DateTimeOffset since, CancellationToken ct);
}
