using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TrennFix.Core.Extraction;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace TrennFix.Core.Tests;

public static class PdfTestHelper
{
    /// <summary>
    /// Renders layout text to a real PDF (Helvetica). Each "cell" (separated by 3+ spaces) is placed at
    /// the x position of its character column, like a real invoice table.
    /// </summary>
    public static byte[] CreatePdf(string layoutText)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        // A TrueType font is needed for umlauts; fall back to Helvetica (ASCII-only text) otherwise.
        const string ttf = "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        var hasTtf = File.Exists(ttf);
        var font = hasTtf ? builder.AddTrueTypeFont(File.ReadAllBytes(ttf)) : builder.AddStandard14Font(Standard14Font.Helvetica);
        if (!hasTtf)
            layoutText = layoutText.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ü", "Ue").Replace("ß", "ss").Replace("·", "-");
        const double fontSize = 9, colWidth = 5.0, lineHeight = 13, left = 40;
        var y = page.PageSize.Height - 50;
        foreach (var line in layoutText.Replace("\r", "").Split('\n'))
        {
            foreach (Match cell in Regex.Matches(line, @"\S+(?: {1,2}\S+)*"))
                page.AddText(cell.Value, fontSize, new PdfPoint(left + cell.Index * colWidth, y), font);
            y -= lineHeight;
        }
        return builder.Build();
    }

    public static bool ToolAvailable(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// OCR via the Tesseract command line (pdftoppm + tesseract). Used in tests on Linux to exercise the
/// OCR path end-to-end; the Windows app uses the built-in Windows OCR instead.
/// </summary>
public sealed class TesseractCliOcrEngine : IOcrEngine
{
    public string Name => "Tesseract CLI";

    public async Task<string> RecognizePdfAsync(byte[] pdf, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("trennfix-ocr");
        try
        {
            var pdfPath = Path.Combine(dir.FullName, "in.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdf, ct);
            await Run("pdftoppm", $"-r 300 -gray -png \"{pdfPath}\" \"{Path.Combine(dir.FullName, "page")}\"", ct);

            var pages = new List<string>();
            foreach (var png in Directory.GetFiles(dir.FullName, "page*.png").Order())
            {
                var tsv = await Run("tesseract", $"\"{png}\" stdout -l deu --psm 4 tsv", ct);
                var words = new List<LayoutWord>();
                foreach (var row in tsv.Split('\n').Skip(1))
                {
                    var c = row.Split('\t');
                    if (c.Length < 12 || c[0] != "5" || string.IsNullOrWhiteSpace(c[11])) continue;
                    double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
                    words.Add(new LayoutWord(c[11].Trim(), D(c[6]), D(c[7]), D(c[8]), D(c[9])));
                }
                pages.Add(LayoutText.Build(words));
            }
            return string.Join("\n\n", pages);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static async Task<string> Run(string file, string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {await p.StandardError.ReadToEndAsync(ct)}");
        return output;
    }
}
