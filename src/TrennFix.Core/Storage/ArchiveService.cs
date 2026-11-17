using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TrennFix.Core.Models;

namespace TrennFix.Core.Storage;

/// <summary>
/// Stores invoice files in the folder structure
/// <c>{Archiv}\{Lieferant}\{Jahr}\{Datum}_{Rechnungsnr}.pdf</c>.
/// Files are written once and never modified (GoBD: unveränderte Aufbewahrung).
/// </summary>
public sealed partial class ArchiveService(string root)
{
    public string Root { get; } = root;

    public string SaveDocument(string supplierFolder, InvoiceData data, DateTimeOffset fallbackDate, string originalFileName, byte[] content, string? extension = null)
    {
        var date = data.InvoiceDate ?? DateOnly.FromDateTime(fallbackDate.LocalDateTime);
        var dir = Path.Combine(Root, supplierFolder, date.Year.ToString());
        Directory.CreateDirectory(dir);

        var ext = extension ?? Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext)) ext = ".pdf";

        var path = UniquePath(dir, DocumentBaseName(data, date, originalFileName), ext);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// File name = invoice number, so the file can be found by searching for the number in Explorer.
    /// "/" and "\" (not allowed in Windows file names) become "-". Without a number: date + original name.
    /// </summary>
    public static string DocumentBaseName(InvoiceData data, DateOnly date, string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(data.InvoiceNumber))
            return SanitizeFileName($"{date:yyyy-MM-dd}_{Path.GetFileNameWithoutExtension(originalFileName)}", 100);

        var nr = data.InvoiceNumber.Trim().Replace('/', '-').Replace('\\', '-');
        // A Mahnung carries the number of the original invoice – keep both files apart.
        var suffix = data.Kind == DocumentKind.Mahnung ? "_Mahnung" : "";
        return SanitizeFileName(nr + suffix, 100);
    }

    /// <summary>Path for a companion file (XML, original mail) next to the main document.</summary>
    public static string CompanionPath(string documentPath, string extension)
    {
        var dir = Path.GetDirectoryName(documentPath)!;
        return UniquePath(dir, Path.GetFileNameWithoutExtension(documentPath), extension);
    }

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    public static string SanitizeFolderName(string name)
    {
        var s = SanitizeFileName(name, 60).TrimEnd('.', ' ');
        return s.Length == 0 ? "Unbekannt" : s;
    }

    public static string SanitizeFileName(string name, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        var s = MultiSpace().Replace(sb.ToString(), " ").Trim();
        if (s.Length > maxLength) s = s[..maxLength].Trim();
        // Reserved device names on Windows
        if (Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase)) s = "_" + s;
        return s;
    }

    private static string UniquePath(string dir, string baseName, string ext)
    {
        var path = Path.Combine(dir, baseName + ext);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName}_{i}{ext}");
        return path;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
