using TrennFix.Core.Extraction;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TrennFix.App.Services;

/// <summary>
/// OCR with the engine built into Windows 10/11 – no extra installation. German is available on
/// every German Windows; otherwise add it under Einstellungen → Zeit und Sprache → Sprache.
/// PDF pages are rendered with the PDF renderer built into Windows (Windows.Data.Pdf).
/// </summary>
public sealed class WindowsOcrEngine(string language) : IOcrEngine
{
    private const double RenderDpi = 300;

    public string Name => "Windows OCR";

    public static bool IsLanguageAvailable(string language) =>
        OcrEngine.IsLanguageSupported(new Language(language));

    public async Task<string> RecognizePdfAsync(byte[] pdf, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language(language))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException(
                         $"Für Texterkennung (OCR) ist kein Sprachpaket installiert ({language}). " +
                         "Windows-Einstellungen → Zeit und Sprache → Sprache → Deutsch hinzufügen.");

        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pdf);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);

        var doc = await PdfDocument.LoadFromStreamAsync(input);
        var pages = new List<string>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);

            // page.Size is in DIPs (1/96 inch)
            var scale = RenderDpi / 96.0;
            var max = OcrEngine.MaxImageDimension;
            var w = page.Size.Width * scale;
            var h = page.Size.Height * scale;
            var fit = Math.Min(1.0, Math.Min(max / w, max / h));

            using var image = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(image, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)(w * fit),
                DestinationHeight = (uint)(h * fit),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            var decoder = await BitmapDecoder.CreateAsync(image);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var result = await engine.RecognizeAsync(bitmap);
            var words = result.Lines
                .SelectMany(l => l.Words)
                .Select(word => new LayoutWord(word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height))
                .ToList();
            pages.Add(LayoutText.Build(words));
        }
        return string.Join("\n\n", pages);
    }
}
