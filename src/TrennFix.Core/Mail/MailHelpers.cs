namespace TrennFix.Core.Mail;

public static class MailHelpers
{
    /// <summary>Message-IDs come with or without angle brackets depending on the source.</summary>
    public static string NormalizeMessageId(string? id) => (id ?? "").Trim().Trim('<', '>').Trim();

    /// <summary>Only invoice-like attachments are loaded (no logos, signatures, calendar files …).</summary>
    public static bool IsRelevantAttachment(string? fileName, string? contentType)
    {
        var name = fileName ?? "";
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return true;
        var ct = contentType ?? "";
        return ct.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
               || ct.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
               || ct.Equals("text/xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Posteingang/Rechnungen" or "Posteingang\Rechnungen" → ["Posteingang", "Rechnungen"].</summary>
    public static string[] SplitFolderPath(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsInboxAlias(string name) =>
        name.Equals("Inbox", StringComparison.OrdinalIgnoreCase) || name.Equals("Posteingang", StringComparison.OrdinalIgnoreCase);
}
