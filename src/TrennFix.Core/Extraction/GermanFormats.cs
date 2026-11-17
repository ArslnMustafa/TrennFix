using System.Globalization;
using System.Text.RegularExpressions;

namespace TrennFix.Core.Extraction;

/// <summary>Parsing helpers for German number and date formats as they appear on invoices.</summary>
public static partial class GermanFormats
{
    // 1.234,56 | 1 234,56 | 1234,56 | 1,234.56 | 1234.56   (always exactly two decimals)
    public const string AmountPattern =
        @"(?<![\d.,])-?(?:\d{1,3}(?:[. ' ]\d{3})+,\d{2}|\d+,\d{2}|\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2})(?![\d%])(?!\s?%)";

    [GeneratedRegex(AmountPattern)]
    public static partial Regex AmountRegex();

    private static readonly string[] MonthNames =
    [
        "januar", "februar", "märz", "april", "mai", "juni",
        "juli", "august", "september", "oktober", "november", "dezember",
    ];

    public const string DatePattern =
        @"(?<!\d)(?:\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})(?!\d)|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}|\d{1,2}\.?\s+(?:Jan(?:uar)?|Feb(?:ruar)?|M(?:ä|ae)rz|Mär|Apr(?:il)?|Mai|Jun[i]?|Jul[i]?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Okt(?:ober)?|Nov(?:ember)?|Dez(?:ember)?)\.?\s+\d{4})";

    [GeneratedRegex(DatePattern, RegexOptions.IgnoreCase)]
    public static partial Regex DateRegex();

    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(" ", "").Replace(" ", "").Replace("'", "").Replace("€", "").Replace("EUR", "");
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma > lastDot)
        {
            // German: dots are thousands separators
            s = s.Replace(".", "").Replace(',', '.');
        }
        else
        {
            // English: commas are thousands separators
            s = s.Replace(",", "");
        }
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(" ", "");

        string[] formats = ["d.M.yyyy", "d.M.yy", "yyyy-MM-dd", "d/M/yyyy"];
        if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return Plausible(d);

        // "3. November 2026", "03 Nov. 2026"
        var m = Regex.Match(text.Trim(), @"^(\d{1,2})\.?\s+([A-Za-zäÄ]+)\.?\s+(\d{4})$");
        if (m.Success)
        {
            var name = m.Groups[2].Value.ToLowerInvariant().Replace("ae", "ä");
            var month = Array.FindIndex(MonthNames, n => n.StartsWith(name.Length >= 3 ? name[..3] : name, StringComparison.Ordinal)) + 1;
            if (month > 0 && int.TryParse(m.Groups[1].Value, out var day) && int.TryParse(m.Groups[3].Value, out var year)
                && day <= DateTime.DaysInMonth(year, month))
                return Plausible(new DateOnly(year, month, day));
        }
        return null;
    }

    /// <summary>Reject obviously wrong dates (OCR noise like 31.12.1899).</summary>
    private static DateOnly? Plausible(DateOnly d) => d.Year is >= 2000 and <= 2100 ? d : null;

    public static string NormalizeIban(string raw) => Regex.Replace(raw.ToUpperInvariant(), @"[^A-Z0-9]", "");

    private static readonly Dictionary<string, int> IbanLengths = new()
    {
        ["DE"] = 22, ["AT"] = 20, ["CH"] = 21, ["NL"] = 18, ["BE"] = 16, ["FR"] = 27, ["IT"] = 27, ["ES"] = 24,
        ["LU"] = 20, ["DK"] = 18, ["PL"] = 28, ["CZ"] = 24, ["IE"] = 22, ["GB"] = 22, ["TR"] = 26, ["LI"] = 21,
        ["SE"] = 24, ["FI"] = 18, ["PT"] = 25, ["SK"] = 24, ["SI"] = 19, ["HU"] = 28, ["NO"] = 15, ["MT"] = 31,
    };

    /// <summary>Validates an IBAN (country length + ISO 7064 mod 97 checksum).</summary>
    public static bool IsValidIban(string iban)
    {
        iban = NormalizeIban(iban);
        if (iban.Length < 15 || iban.Length > 34) return false;
        if (IbanLengths.TryGetValue(iban[..2], out var len) && len != iban.Length) return false;
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            int value;
            if (char.IsDigit(c)) value = c - '0';
            else if (c is >= 'A' and <= 'Z') value = c - 'A' + 10;
            else return false;
            remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }

    /// <summary>Cuts a candidate (which may contain trailing garbage) to the IBAN length of its country.</summary>
    public static string? TryFixIban(string candidate)
    {
        var s = NormalizeIban(candidate);
        if (s.Length < 15) return null;
        if (IbanLengths.TryGetValue(s[..2], out var len))
        {
            if (s.Length < len) return null;
            s = s[..len];
            return IsValidIban(s) ? s : null;
        }
        for (var l = Math.Min(34, s.Length); l >= 15; l--)
            if (IsValidIban(s[..l])) return s[..l];
        return null;
    }

    public static string FormatIban(string iban)
    {
        iban = NormalizeIban(iban);
        return string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));
    }
}
