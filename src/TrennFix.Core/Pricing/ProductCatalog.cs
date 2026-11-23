using System.Text;
using System.Text.RegularExpressions;
using TrennFix.Core.Models;

namespace TrennFix.Core.Pricing;

/// <summary>Manual correction of the automatic product matching.</summary>
public sealed record ProductOverride(string MemberKey, string? GroupKey, bool Separate);

/// <summary>All purchases of one product – possibly from several suppliers with differently written names.</summary>
public sealed class ProductGroup
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Unit { get; init; }
    public required IReadOnlyList<PriceHistoryEntry> Purchases { get; init; } // oldest first
    public required IReadOnlyList<string> MemberKeys { get; init; }

    public int PurchaseCount => Purchases.Count;
    public int SupplierCount => Purchases.Select(p => p.SupplierId).Distinct().Count();
    public PriceHistoryEntry Last => Purchases[^1];
    /// <summary>
    /// The purchase before the last price change: the latest earlier purchase with a different price
    /// (or simply the one before the last, if the price never changed).
    /// </summary>
    public PriceHistoryEntry? Previous =>
        Purchases.Count < 2 ? null
        : Purchases.Take(Purchases.Count - 1).LastOrDefault(p => p.Line.UnitPrice != LastPrice) ?? Purchases[^2];
    /// <summary>Cheapest purchase (the most recent one if several have the same price).</summary>
    public PriceHistoryEntry Cheapest => Purchases.OrderBy(p => p.Line.UnitPrice).ThenByDescending(p => p.EffectiveDate).First();
    public PriceHistoryEntry MostExpensive => Purchases.OrderByDescending(p => p.Line.UnitPrice).ThenByDescending(p => p.EffectiveDate).First();
    public decimal LastPrice => Last.Line.UnitPrice!.Value;
    public decimal? ChangePercent => Previous?.Line.UnitPrice is > 0 and var prev ? (LastPrice - prev) / prev * 100m : null;
    /// <summary>How much more the last purchase cost than the cheapest one (0 = last was the cheapest).</summary>
    public decimal AboveCheapestPercent => Cheapest.Line.UnitPrice is > 0 and var c ? (LastPrice - c) / c * 100m : 0;
    public decimal TotalQuantity => Purchases.Sum(p => p.Line.Quantity ?? 0);
    public decimal TotalAmount => Purchases.Sum(p => p.Line.LineTotal ?? 0);
    public bool Uncertain => Purchases.Any(p => !p.Verified);
}

/// <summary>
/// Recognises the same product across invoices and suppliers.
///
/// Two positions are the same product when
///  • their EAN is equal, or
///  • (same supplier) their article number is equal, or
///  • their descriptions match: all model/size codes (tokens with digits: "305A", "3x1.5", "500ml") agree and
///    the words largely overlap ("Toner HP 305A" = "HP 305A Toner schwarz").
/// Different codes ("305A" vs "305X", "4x40" vs "4x50") or different units never match.
/// The user can merge or separate products manually; that always wins.
/// </summary>
public sealed partial class ProductCatalog
{
    private sealed class Member
    {
        public required string Key { get; init; }
        public required long SupplierId { get; init; }
        public string? Unit { get; init; }
        public string? Ean { get; set; }
        public string? Article { get; set; }
        public required HashSet<string> Words { get; init; }
        public required HashSet<string> Codes { get; init; }
        public List<PriceHistoryEntry> Entries { get; } = [];
        public int Parent { get; set; }
    }

    private readonly List<Member> _members = [];
    private readonly Dictionary<string, int> _byKey = [];
    private readonly Dictionary<string, List<int>> _byToken = [];
    private readonly Dictionary<string, ProductOverride> _overrides;

    public ProductCatalog(IEnumerable<PriceHistoryEntry> history, IEnumerable<ProductOverride> overrides)
    {
        _overrides = overrides.ToDictionary(o => o.MemberKey);
        foreach (var e in history.Where(e => e.Line.IsPriceRelevant)) Add(e);
    }

    public static string MemberKey(long supplierId, InvoiceLine line) => $"{supplierId}|{line.ItemKey}|{line.Unit}";

    /// <summary>Adds a purchase and links it to matching products.</summary>
    public void Add(PriceHistoryEntry e)
    {
        var key = MemberKey(e.SupplierId, e.Line);
        if (!_byKey.TryGetValue(key, out var idx))
        {
            idx = _members.Count;
            var (words, codes) = Tokenize(e.Line.Description);
            var m = new Member
            {
                Key = key, SupplierId = e.SupplierId, Unit = e.Line.Unit, Ean = e.Line.Ean, Article = e.Line.ArticleNumber,
                Words = words, Codes = codes, Parent = idx,
            };
            _members.Add(m);
            _byKey[key] = idx;
            Link(idx);
        }
        var member = _members[idx];
        member.Ean ??= e.Line.Ean;
        member.Article ??= e.Line.ArticleNumber;
        member.Entries.Add(e);
    }

    /// <summary>Earlier purchases of the same product (any supplier) – used for price alerts on import.</summary>
    public IReadOnlyList<PriceHistoryEntry> FindPurchases(long supplierId, InvoiceLine line)
    {
        var key = MemberKey(supplierId, line);
        if (_byKey.TryGetValue(key, out var idx)) return GroupEntries(Find(idx));

        // not seen yet: look for a matching product without adding it
        var (words, codes) = Tokenize(line.Description);
        var probe = new Member
        {
            Key = key, SupplierId = supplierId, Unit = line.Unit, Ean = line.Ean, Article = line.ArticleNumber,
            Words = words, Codes = codes, Parent = -1,
        };
        if (_overrides.TryGetValue(key, out var o) && o.Separate) return [];
        foreach (var candidate in Candidates(probe))
            if (Similar(probe, _members[candidate]))
                return GroupEntries(Find(candidate));
        return [];
    }

    public List<ProductGroup> Groups()
    {
        return Enumerable.Range(0, _members.Count)
            .GroupBy(Find)
            .Select(g =>
            {
                var members = g.Select(i => _members[i]).ToList();
                var purchases = members.SelectMany(m => m.Entries)
                    .OrderBy(p => p.EffectiveDate).ThenBy(p => p.InvoiceId).ToList();
                var name = purchases
                    .GroupBy(p => p.Line.Description)
                    .OrderByDescending(x => x.Count()).ThenByDescending(x => x.Max(p => p.EffectiveDate))
                    .First().Key;
                var manual = members.Select(m => _overrides.GetValueOrDefault(m.Key)?.GroupKey).FirstOrDefault(k => k is not null);
                return new ProductGroup
                {
                    Key = manual ?? members.Select(m => m.Key).Min(StringComparer.Ordinal)!,
                    Name = name,
                    Unit = members.Select(m => m.Unit).FirstOrDefault(u => u is not null),
                    Purchases = purchases,
                    MemberKeys = members.Select(m => m.Key).ToList(),
                };
            })
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ matching

    private void Link(int idx)
    {
        var m = _members[idx];
        var ov = _overrides.GetValueOrDefault(m.Key);

        // manual group: join every member with the same group key
        if (ov?.GroupKey is { } gk)
        {
            for (var i = 0; i < _members.Count; i++)
                if (i != idx && _overrides.GetValueOrDefault(_members[i].Key)?.GroupKey == gk)
                    Union(i, idx);
        }

        if (ov?.Separate != true && ov?.GroupKey is null)
        {
            foreach (var other in Candidates(m))
            {
                if (other == idx) continue;
                var oo = _overrides.GetValueOrDefault(_members[other].Key);
                if (oo?.Separate == true || oo?.GroupKey is not null) continue;
                if (Similar(m, _members[other])) Union(other, idx);
            }
        }

        foreach (var t in IndexTokens(m))
        {
            if (!_byToken.TryGetValue(t, out var list)) _byToken[t] = list = [];
            list.Add(idx);
        }
    }

    private IEnumerable<int> Candidates(Member m) =>
        IndexTokens(m).SelectMany(t => _byToken.TryGetValue(t, out var l) ? l : []).Distinct();

    private static IEnumerable<string> IndexTokens(Member m)
    {
        if (m.Ean is not null) yield return "ean:" + m.Ean;
        if (m.Article is not null) yield return "art:" + m.Article.ToUpperInvariant();
        foreach (var w in m.Words) yield return "w:" + w;
        foreach (var c in m.Codes) yield return "c:" + c;
    }

    private static bool Similar(Member a, Member b)
    {
        if (a.Unit is not null && b.Unit is not null && a.Unit != b.Unit) return false;

        if (a.Ean is not null && b.Ean is not null) return a.Ean == b.Ean;
        if (a.SupplierId == b.SupplierId && a.Article is not null && b.Article is not null)
            return string.Equals(a.Article, b.Article, StringComparison.OrdinalIgnoreCase);

        // model / size codes must (almost) agree
        if (a.Codes.Count > 0 || b.Codes.Count > 0)
        {
            if (Jaccard(a.Codes, b.Codes) < 0.66) return false;
        }

        if (a.Words.Count == 0 || b.Words.Count == 0) return a.Codes.Count > 0 && a.Codes.SetEquals(b.Codes) && a.Words.Count == b.Words.Count;
        var common = a.Words.Intersect(b.Words).Count();
        var containment = (double)common / Math.Min(a.Words.Count, b.Words.Count);
        var jaccard = (double)common / a.Words.Union(b.Words).Count();
        // with matching codes the words may differ more ("Toner 305A" vs "HP 305A Toner schwarz")
        return a.Codes.Count > 0
            ? containment >= 0.6 && jaccard >= 0.3
            : containment >= 0.8 && jaccard >= 0.5;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    // ------------------------------------------------------------------ tokens

    private static readonly HashSet<string> StopWords =
    [
        "und", "mit", "fuer", "inkl", "incl", "der", "die", "das", "den", "dem", "ein", "eine", "von", "zum", "zur", "fur", "for", "the",
        "and", "with", "of", "stk", "stueck", "st", "pack", "packung", "set", "neu", "new", "x", "ca", "je", "pro", "per", "art", "nr",
    ];

    /// <summary>Words (letters only, normalised) and codes (tokens containing digits).</summary>
    public static (HashSet<string> Words, HashSet<string> Codes) Tokenize(string description)
    {
        var s = description.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        s = DecimalComma().Replace(s, "$1.$2");          // 3x1,5 → 3x1.5
        s = NumberUnitGap().Replace(s, "$1$2");           // 500 ml → 500ml, 80 g → 80g
        var words = new HashSet<string>();
        var codes = new HashSet<string>();
        foreach (Match m in TokenRegex().Matches(s))
        {
            var t = m.Value.Trim('.', '-', '/');
            if (t.Length == 0) continue;
            if (t.Any(char.IsDigit)) codes.Add(CanonicalCode(t));
            else if (t.Length >= 2 && !StopWords.Contains(t)) words.Add(Stem(t));
        }
        return (words, codes);
    }

    /// <summary>"500blatt" = "500bl", "1liter" = "1l", "10stueck" = "10stk" …</summary>
    private static string CanonicalCode(string code) => CodeUnit().Replace(code, m => m.Groups[1].Value + m.Groups[2].Value switch
    {
        "blatt" or "bl" => "bl",
        "stueck" or "stk" or "st" or "stck" => "stk",
        "gramm" or "gr" or "g" => "g",
        "liter" or "ltr" or "l" => "l",
        "meter" or "mtr" or "m" => "m",
        var other => other,
    });

    [GeneratedRegex(@"(\d)(blatt|bl|stueck|stck|stk|st|gramm|gr|g|liter|ltr|l|meter|mtr|m)$")]
    private static partial Regex CodeUnit();

    /// <summary>Very light German stemming so that "Kabel"/"Kabeln", "Schraube"/"Schrauben" match.</summary>
    private static string Stem(string w)
    {
        if (w.Length > 5 && (w.EndsWith("en") || w.EndsWith("er"))) return w[..^2];
        if (w.Length > 4 && (w.EndsWith('e') || w.EndsWith('n') || w.EndsWith('s'))) return w[..^1];
        return w;
    }

    [GeneratedRegex(@"(\d),(\d)")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"(\d)\s+(ml|l|ltr|liter|g|gr|kg|mg|mm|cm|m|m2|qm|w|kw|v|a|mah|gb|tb|bl|blatt|stk|stueck|er|x)\b")]
    private static partial Regex NumberUnitGap();

    [GeneratedRegex(@"[a-z0-9][a-z0-9.\-/+]*")]
    private static partial Regex TokenRegex();

    // ------------------------------------------------------------------ union-find

    private int Find(int i)
    {
        while (_members[i].Parent != i)
        {
            _members[i].Parent = _members[_members[i].Parent].Parent;
            i = _members[i].Parent;
        }
        return i;
    }

    private void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb) _members[Math.Max(ra, rb)].Parent = Math.Min(ra, rb);
    }

    private List<PriceHistoryEntry> GroupEntries(int root) =>
        Enumerable.Range(0, _members.Count).Where(i => Find(i) == root)
            .SelectMany(i => _members[i].Entries)
            .OrderBy(p => p.EffectiveDate).ThenBy(p => p.InvoiceId)
            .ToList();
}
