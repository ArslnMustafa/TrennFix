using System.Globalization;
using System.Text.RegularExpressions;
using TrennFix.Core.Models;

namespace TrennFix.Core.Extraction;

/// <summary>
/// Reads the position table of an invoice from layout text (PDF text layer or OCR).
///
/// 1. Find a table header ("Pos  Artikel  Menge  Einzelpreis  Gesamt", "Leistung  Betrag" …).
/// 2. Every following line whose right end consists of numbers is a position:
///    the numbers at the end are quantity / unit price / total, the text before is the description.
///    Lines with text only directly below a position continue its description.
/// 3. The table ends at the totals ("Nettobetrag", "Summe", "Übertrag" …); a new header
///    (next page) starts a new block.
/// 4. The sum of all positions is compared with the net (or gross) total of the invoice.
///    Only if it matches are the lines marked as verified – unverified lines never raise price alerts.
/// </summary>
public static partial class LineItemParser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private enum Col { Pos, Article, Description, Quantity, Unit, UnitPrice, Total, Tax }

    private static readonly (Col Col, Regex Rx)[] HeaderWords =
    [
        (Col.Pos, new(@"^\s*(?:Pos(?:ition)?\.?|Nr\.?|lfd\.?\s*Nr\.?)(?=\s|$)", Ci)),
        (Col.Article, new(@"Art(?:ikel)?\.?[-\s]?(?:Nr|Nummer|No)\.?|Artikelnummer|Material(?:nummer|[-\s]?Nr\.?)?|\bSKU\b|Bestell[-\s]?Nr\.?|Produktnummer|Art\.?[-\s]?Nr\.?", Ci)),
        (Col.Description, new(@"Bezeichnung|Beschreibung|Artikel(?![-.\s]*(?:Nr|Nummer|No))|Leistung(?:sbeschreibung)?|Produkt(?!nummer)|\bText\b|Description|\bItem\b|\bWare\b|Positionstext|Dienstleistung", Ci)),
        (Col.Quantity, new(@"Menge|Anzahl|\bAnz\.|St(?:ü|ue)ckzahl|\bQty\b|Quantity|\bStk\.?(?=\s|$)", Ci)),
        (Col.Unit, new(@"Einheit|\bME\b|\bUnit\b(?!\s*price)", Ci)),
        (Col.UnitPrice, new(@"Einzelpreis|E-?Preis|\bEP\b|St(?:ü|ue)ckpreis|Preis\s*(?:/|pro|je)\s*(?:Einheit|Stk|St(?:ü|ue)ck|ME)|Einzelbetrag|Unit\s*price|Netto[-\s]?Preis|VK[-\s]?Preis|\bPreis\b", Ci)),
        (Col.Total, new(@"Gesamt(?:preis|betrag)?|\bBetrag\b|\bSumme\b|\bGP\b|\bTotal\b|\bAmount\b|Netto[-\s]?betrag|\bWert\b|Positionsbetrag|Zeilenbetrag", Ci)),
        (Col.Tax, new(@"MwSt|\bUSt\b|Steuer(?:satz)?", Ci)),
    ];

    [GeneratedRegex(@"^\s*(?:Netto|Nettobetrag|Nettosumme|Zwischensumme|Summe|Gesamt|Gesamtbetrag|Gesamtsumme|Rechnungsbetrag|Rechnungssumme|(?:Ü|Ue)bertrag|zzgl\.?|zuz(?:ü|ue)glich|MwSt|USt|Umsatzsteuer|Mehrwertsteuer|Warenwert|Endbetrag|Total|Bruttobetrag|Zu\s+zahlen|Zahlbetrag|Versandkosten\s+gesamt)\b", Ci)]
    private static partial Regex StopRegex();

    // German number: 1.234,56 | 12,5 | 0,1234 | 10 – not part of dates, codes or percentages
    [GeneratedRegex(@"^-?(?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d{1,4})?-?$")]
    private static partial Regex NumberTokenRegex();

    [GeneratedRegex(@"^(?:St(?:ü|ue)ck|Stk?\.?|Stck\.?|pcs|pc|EA|x|Std\.?|Stunden?|h|kg|g|t|m|m2|m²|qm|m3|m³|cbm|lfm|km|l|Ltr\.?|Liter|Pkt\.?|Pck\.?|Pak\.?|Packung|Karton|Ktn\.?|Krt\.?|Rolle|Rol\.?|Paar|Satz|Set|psch\.?|pauschal|Pauschale|ME|VE|PE|Fl\.?|Flasche|Dose|Kiste|Palette|Pal\.?|Eimer|Sack|Btl\.?|Beutel|Lizenz(?:en)?|Tag|Tage|Monat|Monate|Mon\.?|Woche|Jahr)$", Ci)]
    private static partial Regex UnitTokenRegex();

    [GeneratedRegex(@"^(?:€|EUR|%|\d{1,2}(?:,\d+)?\s?%|[A-D])$", Ci)]
    private static partial Regex TailNoiseRegex();

    private sealed record Header(int LineIndex, Dictionary<Col, (int Start, int End)> Columns);

    public static (List<InvoiceLine> Lines, bool Verified) Parse(string[] lines, decimal? net, decimal? gross)
    {
        var result = new List<InvoiceLine>();
        Header? header = null;
        InvoiceLine? last = null;
        var continuationCount = 0;
        var noise = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (TryHeader(line, i) is { } h)
            {
                header = h;
                last = null;
                noise = 0;
                continue;
            }
            if (header is null) continue;
            if (line.Trim().Length == 0) { last = null; continue; }
            if (StopRegex().IsMatch(line)) { header = null; last = null; continue; }

            if (ParseItem(line, header) is { } item)
            {
                result.Add(item);
                last = item;
                continuationCount = 0;
                noise = 0;
            }
            else if (last is not null && continuationCount < 2 && line.Trim().Length < 100 && line.Count(char.IsLetter) >= 3
                     && !line.Contains(':') )
            {
                last.Description = (last.Description + " " + Regex.Replace(line.Trim(), @"\s{2,}", " ")).Trim();
                continuationCount++;
            }
            else if (++noise >= 3)
            {
                header = null; // left the table without seeing the totals
            }
        }

        var verified = result.Count > 0 && Matches(result.Sum(l => l.LineTotal ?? 0), net, gross);
        return (result, verified);
    }

    private static bool Matches(decimal sum, decimal? net, decimal? gross)
    {
        bool Close(decimal? target) => target is { } t && Math.Abs(sum - t) <= Math.Max(0.05m, Math.Abs(t) * 0.001m);
        return Close(net) || Close(gross);
    }

    // ------------------------------------------------------------------ header

    private static Header? TryHeader(string line, int index)
    {
        if (GermanFormats.AmountRegex().IsMatch(line)) return null;
        var cols = new Dictionary<Col, (int, int)>();
        foreach (var (col, rx) in HeaderWords)
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            // "Artikel" must not be counted twice (Article column "Artikel-Nr." vs description "Artikel")
            if (cols.Values.Any(v => m.Index < v.Item2 && m.Index + m.Length > v.Item1)) continue;
            cols[col] = (m.Index, m.Index + m.Length);
        }

        var hasPrice = cols.ContainsKey(Col.UnitPrice) || cols.ContainsKey(Col.Total);
        var ok = hasPrice && (cols.ContainsKey(Col.Description) || cols.ContainsKey(Col.Quantity) && cols.Count >= 3);
        return ok ? new Header(index, cols) : null;
    }

    // ------------------------------------------------------------------ item line

    private static InvoiceLine? ParseItem(string line, Header header)
    {
        // Split into whitespace-separated tokens with their positions.
        var tokens = Regex.Matches(line, @"\S+").Select(m => (Text: m.Value, m.Index)).ToList();
        if (tokens.Count < 2) return null;

        // The description column reaches up to the next column header – numbers inside it
        // ("Milch 3,5 % 1 l", "Business Flat L") belong to the description, not to quantity/price.
        var descBoundary = DescriptionBoundary(header);

        // Walk from the right: numbers, units, currency, tax codes form the "tail".
        var tailStart = tokens.Count;
        for (var t = tokens.Count - 1; t >= 0; t--)
        {
            if (tokens[t].Index + tokens[t].Text.Length <= descBoundary) break;
            var tok = tokens[t].Text.TrimEnd('€');
            var isNumber = NumberTokenRegex().IsMatch(tok);
            // Without column information a unit only counts directly after a number ("100 m", "6 Stk").
            var isUnit = UnitTokenRegex().IsMatch(tok)
                         && (descBoundary > 0 || t > 0 && NumberTokenRegex().IsMatch(tokens[t - 1].Text));
            if (tok.Length == 0 || isNumber || isUnit || TailNoiseRegex().IsMatch(tok) && t < tokens.Count - 1 && descBoundary > 0
                || tok is "%")
                tailStart = t;
            else
                break;
        }
        if (tailStart == tokens.Count || tailStart == 0) return null;

        var tail = tokens.Skip(tailStart).ToList();
        var numbers = new List<(decimal Value, bool HasDecimals, int Index)>();
        string? unit = null;
        foreach (var (text, index) in tail)
        {
            var tok = text.TrimEnd('€');
            if (NumberTokenRegex().IsMatch(tok))
            {
                // percentages ("19 %" with a space) – skip the number directly before "%"
                var next = tail.SkipWhile(x => x.Index != index).Skip(1).FirstOrDefault().Text;
                if (next is "%") continue;
                var neg = tok.StartsWith('-') || tok.EndsWith('-');
                var v = GermanFormats.ParseAmount(tok.Trim('-'));
                if (v is null) continue;
                numbers.Add((neg ? -v.Value : v.Value, tok.Contains(','), index));
            }
            else if (UnitTokenRegex().IsMatch(tok) && unit is null)
            {
                unit = NormalizeUnit(tok);
            }
        }
        // The last number must look like money (two decimals) – otherwise this is not a position line
        if (numbers.Count == 0 || !numbers[^1].HasDecimals) return null;

        var item = new InvoiceLine { Unit = unit, LineTotal = numbers[^1].Value };
        var before = numbers.Take(numbers.Count - 1).ToList();
        AssignQuantityAndPrice(item, before, header);

        // Description part: everything before the tail
        var descEnd = tokens[tailStart].Index;
        var head = line[..descEnd].TrimEnd();
        var cells = Regex.Split(head.Trim(), @"\s{3,}").Where(c => c.Length > 0).ToList();
        if (cells.Count == 0) return null;

        // Position number
        var posMatch = Regex.Match(cells[0], @"^(\d{1,4})\.?(?:\s+|$)");
        if (posMatch.Success && (header.Columns.ContainsKey(Col.Pos) || cells.Count > 1 || cells[0].Length > posMatch.Length))
        {
            item.Position = posMatch.Groups[1].Value;
            cells[0] = cells[0][posMatch.Length..].Trim();
            if (cells[0].Length == 0) cells.RemoveAt(0);
        }

        // Article number: own column, or a code-like first cell followed by a text cell
        if (cells.Count > 1 && LooksLikeCode(cells[0]) && (header.Columns.ContainsKey(Col.Article) || cells[0].Any(char.IsDigit)))
        {
            item.ArticleNumber = cells[0];
            cells.RemoveAt(0);
        }
        else if (cells.Count == 1 && header.Columns.ContainsKey(Col.Article))
        {
            var parts = cells[0].Split(' ', 2);
            if (parts.Length == 2 && LooksLikeCode(parts[0]) && parts[0].Any(char.IsDigit))
            {
                item.ArticleNumber = parts[0];
                cells[0] = parts[1];
            }
        }

        item.Description = string.Join(" ", cells).Trim();
        if (item.Description.Count(char.IsLetter) < 2) return null;
        return item;
    }

    private static bool LooksLikeCode(string s) =>
        s.Length is >= 3 and <= 25 && !s.Contains(' ') && Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9\-./_,+]*$");

    /// <summary>Character column where the description ends (start of the next header column minus a little slack).</summary>
    private static int DescriptionBoundary(Header header)
    {
        if (!header.Columns.TryGetValue(Col.Description, out var desc)) return 0;
        var next = header.Columns
            .Where(kv => kv.Key is not (Col.Pos or Col.Article or Col.Description) && kv.Value.Start > desc.Start)
            .Select(kv => kv.Value.Start)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return next == int.MaxValue ? 0 : Math.Max(desc.End, next - 4);
    }

    private static void AssignQuantityAndPrice(InvoiceLine item, List<(decimal Value, bool HasDecimals, int Index)> before, Header header)
    {
        var total = item.LineTotal!.Value;
        bool Fits(decimal q, decimal p) => q != 0 && Math.Abs(q * p - total) <= Math.Max(0.02m, Math.Abs(total) * 0.005m);

        // Order of the columns in the header (some invoices print "Einzelpreis  Menge")
        var priceFirst = header.Columns.TryGetValue(Col.UnitPrice, out var up) && header.Columns.TryGetValue(Col.Quantity, out var qc) && up.Start < qc.Start;

        // Prefer adjacent pairs directly before the total that multiply to the total.
        for (var j = before.Count - 1; j >= 1; j--)
        {
            var a = before[j - 1].Value;
            var b = before[j].Value;
            if (!Fits(a, b)) continue;
            (item.Quantity, item.UnitPrice) = priceFirst ? (b, a) : (a, b);
            return;
        }
        // Any pair
        for (var x = 0; x < before.Count; x++)
            for (var y = x + 1; y < before.Count; y++)
                if (Fits(before[x].Value, before[y].Value))
                {
                    (item.Quantity, item.UnitPrice) = priceFirst ? (before[y].Value, before[x].Value) : (before[x].Value, before[y].Value);
                    return;
                }

        switch (before.Count)
        {
            case 0:
                // Only an amount ("Business Flat L   49,95") – one unit at that price
                item.Quantity = 1;
                item.UnitPrice = total;
                break;
            case 1:
                // One extra number: with decimals it is the unit price, without it is the quantity
                if (before[0].HasDecimals && !Fits(1, total))
                {
                    item.UnitPrice = before[0].Value;
                }
                else
                {
                    item.Quantity = before[0].Value;
                    if (before[0].Value != 0) item.UnitPrice = Math.Round(total / before[0].Value, 4);
                }
                break;
            default:
                // e.g. line discount in between – take the number before the total as unit price
                item.UnitPrice = before[^1].Value;
                item.Quantity = before[^2].Value;
                break;
        }
    }

    public static string? NormalizeUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var u = raw.Trim().TrimEnd('.').ToLowerInvariant();
        return u switch
        {
            "stk" or "st" or "stück" or "stueck" or "stck" or "pcs" or "pc" or "ea" or "x" or "h87" or "c62" or "xpp" => "Stk",
            "std" or "stunde" or "stunden" or "h" or "hur" => "Std",
            "kg" or "kgm" => "kg",
            "g" or "grm" => "g",
            "t" or "tne" => "t",
            "m" or "mtr" or "lfm" => "m",
            "m2" or "m²" or "qm" or "mtk" => "m²",
            "m3" or "m³" or "cbm" or "mtq" => "m³",
            "l" or "ltr" or "liter" => "l",
            "km" or "kmt" => "km",
            "psch" or "pauschal" or "pauschale" or "ls" => "pauschal",
            "tag" or "tage" or "day" => "Tag",
            "monat" or "monate" or "mon" => "Monat",
            "pkt" or "pck" or "pak" or "packung" or "xpk" => "Pkt",
            "karton" or "ktn" or "krt" or "xct" => "Karton",
            "paar" or "pr" => "Paar",
            _ => raw.Trim().TrimEnd('.'),
        };
    }

    public static string ToInvariant(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? "";
}
