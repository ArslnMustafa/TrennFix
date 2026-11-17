using System.Text;

namespace TrennFix.Core.Extraction;

/// <summary>A positioned word. Coordinates: origin top-left, y grows downwards, any unit.</summary>
public readonly record struct LayoutWord(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Turns positioned words (from a PDF text layer or from OCR) into plain text that keeps
/// the visual layout: one line per printed line, and words placed at a character column
/// proportional to their x position. Columns therefore line up across lines, which the
/// parser uses to read table-style headers ("Rechnungsnr.  Datum") and the values below.
/// Larger horizontal gaps become at least three spaces, so "cells" can be split on /\s{3,}/.
/// </summary>
public static class LayoutText
{
    public static string Build(IReadOnlyCollection<LayoutWord> words)
    {
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0).ToList();
        if (list.Count == 0) return "";

        var charWidth = Median(list.Select(w => w.Width / Math.Max(1, w.Text.Length)));
        var lineHeight = Median(list.Select(w => w.Height));
        if (charWidth <= 0) charWidth = 1;
        if (lineHeight <= 0) lineHeight = 1;

        // Group into lines by vertical centre.
        var lines = new List<List<LayoutWord>>();
        var lineCentres = new List<double>();
        foreach (var w in list.OrderBy(w => w.Y + w.Height / 2))
        {
            var centre = w.Y + w.Height / 2;
            var idx = lineCentres.Count - 1;
            if (idx >= 0 && Math.Abs(lineCentres[idx] - centre) <= lineHeight * 0.5)
            {
                lines[idx].Add(w);
            }
            else
            {
                lines.Add([w]);
                lineCentres.Add(centre);
            }
        }

        var minX = list.Min(w => w.X);
        var sb = new StringBuilder();
        double? previousCentre = null;
        for (var li = 0; li < lines.Count; li++)
        {
            // keep big vertical gaps as an empty line (separates blocks)
            if (previousCentre is { } pc && lineCentres[li] - pc > lineHeight * 2.2) sb.Append('\n');
            previousCentre = lineCentres[li];

            var line = new StringBuilder();
            double? prevEnd = null;
            foreach (var w in lines[li].OrderBy(w => w.X))
            {
                var col = (int)Math.Round((w.X - minX) / charWidth);
                if (line.Length > 0)
                {
                    var gapChars = prevEnd is { } pe ? (w.X - pe) / charWidth : 1;
                    var spaces = gapChars >= 1.8 ? Math.Max(3, col - line.Length) : 1;
                    line.Append(' ', spaces);
                }
                else if (col > 0)
                {
                    line.Append(' ', col);
                }
                line.Append(w.Text);
                prevEnd = w.X + w.Width;
            }
            sb.Append(line.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.Where(v => v > 0 && !double.IsNaN(v)).OrderBy(v => v).ToArray();
        return arr.Length == 0 ? 0 : arr[arr.Length / 2];
    }
}
