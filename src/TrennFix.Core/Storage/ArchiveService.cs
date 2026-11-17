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

        var prefix = data.Kind switch
        {
            DocumentKind.Gutschrift => "Gutschrift_",
            DocumentKind.Mahnung => "Mahnung_",
            DocumentKind.Sonstiges => "Dokument_",
            _ => "",
        };
        var core = !string.IsNullOrWhiteSpace(data.InvoiceNumber)
            ? data.InvoiceNumber
            : Path.GetFileNameWithoutExtension(originalFileName);
        var baseName = SanitizeFileName($"{date:yyyy-MM-dd}_{prefix}{core}", 100);

        var path = UniquePath(dir, baseName, ext);
        File.WriteAllBytes(path, content);
        return path;
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
