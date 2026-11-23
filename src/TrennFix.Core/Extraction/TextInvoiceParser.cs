using System.Text.RegularExpressions;
using TrennFix.Core.Models;

namespace TrennFix.Core.Extraction;

/// <summary>
/// Extracts invoice fields from plain text (PDF text layer or OCR output) using
/// heuristics tuned for German invoices.
///
/// The text is expected in "layout" form: one physical line per line, columns kept at
/// roughly the same character position (see <see cref="LayoutText"/>). This lets us
/// read values that are printed *below* a column header, which is very common
/// ("Rechnungsnr.   Datum   Kundennr." / "RE-4711   03.11.2026   K-12").
/// </summary>
public sealed partial class TextInvoiceParser(ExtractionOptions options)
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public InvoiceData Parse(string text, ExtractionSource source)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var data = new InvoiceData
        {
            Source = source,
            FormatName = source == ExtractionSource.Ocr ? "PDF (OCR)" : "PDF",
            RawText = text,
        };

        data.Kind = DetectKind(lines);
        data.InvoiceNumber = FindInvoiceNumber(lines);
        data.InvoiceDate = FindInvoiceDate(lines);
        data.DueDate = FindDueDate(text, lines, data.InvoiceDate);
        data.DiscountTerms = SkontoRegex().Match(text) is { Success: true } sk ? Collapse(sk.Value) : null;

        FindAmounts(lines, data);

        var ibans = FindIbans(text);
        data.Iban = ibans.FirstOrDefault(i => !options.IsOwnIban(i));
        data.Bic = BicRegex().Match(text) is { Success: true } bic ? bic.Groups[1].Value : null;
        data.SellerVatId = FindVatIds(text).FirstOrDefault(v => !options.IsOwnVatId(v));
        data.SellerTaxNumber = TaxNumberRegex().Match(text) is { Success: true } tn ? tn.Groups[1].Value.Replace(" ", "") : null;
        data.MandateReference = MandateRegex().Match(text) is { Success: true } mr ? mr.Groups[1].Value.TrimEnd('.', ',') : null;
        data.CreditorId = CreditorIdRegex().Match(text) is { Success: true } ci ? ci.Value.Replace(" ", "").ToUpperInvariant() : null;
        data.PaymentReference = PaymentReferenceRegex().Match(text) is { Success: true } pr ? SplitCells(pr.Groups[1].Value).First().Trim() : null;

        var ownIbanPrinted = ibans.Any(options.IsOwnIban);
        data.PaymentMethod = DetectPaymentMethod(text, data, ownIbanPrinted);

        data.SellerName = FindSellerName(lines);
        (data.Lines, data.LinesVerified) = LineItemParser.Parse(lines, data.NetAmount, data.GrossAmount);
        return data;
    }

    // ---------------------------------------------------------------- document kind

    [GeneratedRegex(@"Gutschrift|Rechnungskorrektur|Storno(?:rechnung)?|Credit\s*note", Ci)]
    private static partial Regex CreditNoteRegex();

    [GeneratedRegex(@"Rechnung|Invoice|Faktura", Ci)]
    private static partial Regex InvoiceWordRegex();

    [GeneratedRegex(@"\b(?:\d\.\s*)?(?:Mahnung|Zahlungserinnerung|Letzte\s+Erinnerung)\b", Ci)]
    private static partial Regex DunningRegex();

    [GeneratedRegex(@"Lieferschein|Auftragsbest(?:ä|ae)tigung|Bestellbest(?:ä|ae)tigung|\bAngebot\b|Allgemeine\s+Gesch(?:ä|ae)ftsbedingungen|Widerrufsbelehrung|Datenschutz", Ci)]
    private static partial Regex OtherDocRegex();

    private static DocumentKind DetectKind(string[] lines)
    {
        // The document title is usually in the upper part of the first page.
        var head = string.Join('\n', lines.Take(30));
        int Pos(Regex r, string s) => r.Match(s) is { Success: true } m ? m.Index : int.MaxValue;

        foreach (var scope in new[] { head, string.Join('\n', lines) })
        {
            var dunning = Pos(DunningRegex(), scope);
            var credit = Pos(CreditNoteRegex(), scope);
            var invoice = Pos(InvoiceWordRegex(), scope);
            var other = Pos(OtherDocRegex(), scope);

            if (dunning != int.MaxValue && dunning <= Math.Min(invoice, credit)) return DocumentKind.Mahnung;
            if (credit != int.MaxValue && credit <= invoice) return DocumentKind.Gutschrift;
            if (invoice != int.MaxValue && invoice <= other) return DocumentKind.Rechnung;
            if (other != int.MaxValue) return DocumentKind.Sonstiges;
            if (dunning != int.MaxValue) return DocumentKind.Mahnung;
        }
        return DocumentKind.Unbekannt;
    }

    // ---------------------------------------------------------------- invoice number

    [GeneratedRegex(
        @"Rechnungs?[-\s]?(?:nummer|nr\b\.?|no\b\.?)|Rechnung\s+(?:Nr\b\.?|Nummer|No\b\.?)|Gutschrifts?[-\s]?(?:nummer|nr\b\.?)" +
        @"|Gutschrift\s+(?:Nr\b\.?|Nummer)|Invoice\s*(?:No\b\.?|Number|#)|\bR[eg]\.?\s?-?\s?Nr\b\.?|Beleg[-\s]?(?:nummer|nr\b\.?)" +
        @"|Dokumenten?[-\s]?(?:nummer|nr\b\.?)|Mahnungs?[-\s]?(?:nummer|nr\b\.?)", Ci)]
    private static partial Regex InvoiceNumberLabelRegex();

    [GeneratedRegex(@"\b(?:Rechnung|Gutschrift|Invoice)\b(?![-\s]*(?:datum|date|vom|an\b|f(?:ü|ue)r|zu\b|betrag|adresse|s))", Ci)]
    private static partial Regex WeakInvoiceNumberLabelRegex();

    [GeneratedRegex(@"(?=[A-Za-z0-9\-/_.]*\d)[A-Za-z0-9][A-Za-z0-9\-/_.]{1,30}[A-Za-z0-9]")]
    private static partial Regex InvoiceNumberValueRegex();

    private string? FindInvoiceNumber(string[] lines)
    {
        bool Valid(string v) =>
            v.Length >= 3
            && !GermanFormats.DateRegex().IsMatch(v)
            && !Regex.IsMatch(v, @"^\d{1,3}(?:[.,]\d{3})*[.,]\d{2}$")      // amount
            && !Regex.IsMatch(v, @"^(?:DE|AT)\d{9}$")                        // VAT ID
            && !Regex.IsMatch(v, @"^\d{1,2}$");

        return FindLabeledValue(lines, InvoiceNumberLabelRegex(), InvoiceNumberValueRegex(), 40, Valid)
            ?? FindLabeledValue(lines, WeakInvoiceNumberLabelRegex(), InvoiceNumberValueRegex(), 25, Valid, allowColumn: false);
    }

    // ---------------------------------------------------------------- dates

    [GeneratedRegex(
        @"Rechnungs[-\s]?datum|Datum\s+der\s+Rechnung|Beleg[-\s]?datum|Invoice\s+date|Ausstellungsdatum|Gutschriftsdatum" +
        @"|(?:Rechnung|Gutschrift)\s+vom|(?<![A-Za-zäöüÄÖÜß-])Datum", Ci)]
    private static partial Regex InvoiceDateLabelRegex();

    private static DateOnly? FindInvoiceDate(string[] lines)
    {
        var v = FindLabeledValue(lines, InvoiceDateLabelRegex(), GermanFormats.DateRegex(), 45,
            s => GermanFormats.ParseDate(s) is not null);
        if (v is not null) return GermanFormats.ParseDate(v);

        // Fallback: first plausible date in the upper part of the document,
        // ignoring delivery / service period dates.
        foreach (var line in lines.Take(Math.Max(40, lines.Length / 2)))
        {
            if (Regex.IsMatch(line, @"Liefer|Leistung|Zeitraum|geboren|Bestell|Auftrag", Ci)) continue;
            foreach (Match m in GermanFormats.DateRegex().Matches(line))
                if (GermanFormats.ParseDate(m.Value) is { } d) return d;
        }
        return null;
    }

    [GeneratedRegex(
        @"zahlbar\s+(?:bis(?:\s+(?:zum|sp(?:ä|ae)testens))?|am|zum|sp(?:ä|ae)testens\s+(?:am|bis))|f(?:ä|ae)llig\s+(?:am|bis|zum)" +
        @"|F(?:ä|ae)lligkeit(?:sdatum)?|Zahlungsziel|sp(?:ä|ae)testens\s+(?:bis\s+)?(?:zum|am)|Due\s+date|Zahlungstermin" +
        @"|Abbuchung\s+(?:am|ab|zum)|Einzug\s+(?:am|ab|zum)|Belastung\s+(?:am|ab|zum)" +
        @"|(?<!(?:Zeitraum|Leistung|Abrechnung|Liefer|vom)[^\n]{0,40})\bbis\s+(?:zum|sp(?:ä|ae)testens(?:\s+(?:zum|am))?)", Ci)]
    private static partial Regex DueDateLabelRegex();

    [GeneratedRegex(@"(?:am|zum|ab(?:\s+dem)?)\s+(" + GermanFormats.DatePattern + @")[^\n]{0,60}?(?:abgebucht|eingezogen|belastet)", Ci)]
    private static partial Regex DebitDateRegex();

    [GeneratedRegex(@"(?:innerhalb|binnen)\s+(?:von\s+)?(\d{1,3})\s+(?:Kalender|Werk)?tag|(\d{1,3})\s+Tage\s+(?:rein\s+)?netto|Zahlungsziel:?\s*(\d{1,3})\s*Tage", Ci)]
    private static partial Regex DueDaysRegex();

    [GeneratedRegex(@"(?:zahlbar|f(?:ä|ae)llig)\s+sofort|sofort\s+(?:zahlbar|f(?:ä|ae)llig)", Ci)]
    private static partial Regex DueImmediatelyRegex();

    private static DateOnly? FindDueDate(string text, string[] lines, DateOnly? invoiceDate)
    {
        var v = FindLabeledValue(lines, DueDateLabelRegex(), GermanFormats.DateRegex(), 50,
            s => GermanFormats.ParseDate(s) is not null);
        if (v is not null) return GermanFormats.ParseDate(v);

        if (DebitDateRegex().Match(text) is { Success: true } dm && GermanFormats.ParseDate(dm.Groups[1].Value) is { } dd)
            return dd;

        if (invoiceDate is { } inv)
        {
            if (DueDaysRegex().Match(text) is { Success: true } m)
            {
                var days = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                if (days is > 0 and <= 120) return inv.AddDays(days);
            }
            if (DueImmediatelyRegex().IsMatch(text)) return inv;
        }
        return null;
    }

    [GeneratedRegex(@"\d{1,2}(?:[.,]\d{1,2})?\s*%\s*Skonto[^\n]{0,80}|Skonto[^\n]{0,80}", Ci)]
    private static partial Regex SkontoRegex();

    // ---------------------------------------------------------------- amounts

    [GeneratedRegex(
        @"zu\s+zahlen(?:de[rn]?\s+(?:Gesamt)?betrag)?|Zahlbetrag|zahlbarer\s+Betrag|Rechnungsendbetrag|Amount\s+due" +
        @"|Gesamt\s+zu\s+zahlen|Abbuchungsbetrag|Lastschriftbetrag", Ci)]
    private static partial Regex PayableLabelRegex();

    [GeneratedRegex(
        @"Rechnungsbetrag|Gesamtbetrag|Endbetrag|Gesamtsumme|Rechnungssumme|Bruttobetrag|Brutto[-\s]?summe|Summe\s+brutto" +
        @"|Gesamt\s+brutto|Gesamtpreis\s+brutto|Betrag\s+brutto|Total\s+(?:amount|incl)|Gutschriftsbetrag|Bruttorechnungsbetrag" +
        @"|Gesamt\s*\(?brutto\)?|Rechnungsbetrag\s+brutto", Ci)]
    private static partial Regex GrossLabelRegex();

    [GeneratedRegex(@"\bBrutto\b|\bGesamt\b|\bSumme\b|\bTotal\b", Ci)]
    private static partial Regex WeakGrossLabelRegex();

    [GeneratedRegex(@"Netto(?:betrag|summe|wert)?\b|Summe\s+netto|Gesamt\s+netto|Nettorechnungsbetrag|Zwischensumme|Warenwert|Total\s+net", Ci)]
    private static partial Regex NetLabelRegex();

    [GeneratedRegex(@"(?:MwSt\.?|Mehrwertsteuer|Umsatzsteuer|\bUSt\.?)(?![-\s.]*(?:Id|Nr|IdNr|Identifikation))", Ci)]
    private static partial Regex TaxLabelRegex();

    [GeneratedRegex(@"netto|exkl|zzgl|ohne|(?<!(?:inkl|incl|einschl)\.?\s*)(?:MwSt|\bUSt|Mehrwertsteuer|Umsatzsteuer|Steuer)", Ci)]
    private static partial Regex NotGrossRegex();

    private static void FindAmounts(string[] lines, InvoiceData data)
    {
        var amountRx = GermanFormats.AmountRegex();

        // 1) "zu zahlen" – the amount actually payable, take the last one in the document
        var payable = CollectAmounts(lines, PayableLabelRegex(), amountRx, allowColumn: true, reject: null);
        // 2) strong gross labels, take the largest (line items rarely use these words)
        var gross = CollectAmounts(lines, GrossLabelRegex(), amountRx, allowColumn: true, reject: NotGrossRegex());
        // 3) weak labels, only same line
        var weak = CollectAmounts(lines, WeakGrossLabelRegex(), amountRx, allowColumn: false, reject: NotGrossRegex());

        data.GrossAmount = payable.Count > 0 ? payable[^1]
            : gross.Count > 0 ? gross.MaxBy(Math.Abs)
            : weak.Count > 0 ? weak.MaxBy(Math.Abs)
            : null;

        var net = CollectAmounts(lines, NetLabelRegex(), amountRx, allowColumn: false, reject: null);
        var tax = CollectAmounts(lines, TaxLabelRegex(), amountRx, allowColumn: false, reject: new Regex(@"IdNr|Identifikation|Id\.?-?Nr", Ci));

        if (net.Count > 0) data.NetAmount = net.MaxBy(Math.Abs);
        if (data.GrossAmount is { } g && data.NetAmount is { } n && Math.Abs(n) > Math.Abs(g)) data.NetAmount = null;

        if (data.GrossAmount is { } g2 && data.NetAmount is { } n2) data.TaxAmount = g2 - n2;
        else if (tax.Count > 0) data.TaxAmount = tax.MaxBy(Math.Abs);

        if (data.GrossAmount is null && data.NetAmount is { } n3 && data.TaxAmount is { } t3)
            data.GrossAmount = n3 + t3;
    }

    private static List<decimal> CollectAmounts(string[] lines, Regex label, Regex amountRx, bool allowColumn, Regex? reject)
    {
        var result = new List<decimal>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match lm in label.Matches(lines[i]))
            {
                var rest = lines[i][(lm.Index + lm.Length)..];
                var am = amountRx.Match(rest);
                if (am.Success && am.Index <= 90)
                {
                    var between = rest[..am.Index];
                    if (reject is not null && reject.IsMatch(between)) continue;
                    // "Gesamtbetrag netto" written *before* the label on the same line
                    if (reject is not null && reject.IsMatch(lines[i][..lm.Index].Split("   ").Last())) continue;
                    if (GermanFormats.ParseAmount(am.Value) is { } v) result.Add(v);
                    continue;
                }
                if (!allowColumn || rest.Trim().Length > 0 && amountRx.IsMatch(rest)) continue;
                if (reject is not null && reject.IsMatch(rest)) continue;
                if (FindInColumnBelow(lines, i, lm, amountRx, _ => true) is { } colValue && GermanFormats.ParseAmount(colValue) is { } cv)
                    result.Add(cv);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- bank / tax ids

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?")]
    private static partial Regex IbanCandidateRegex();

    public static List<string> FindIbans(string text)
    {
        var result = new List<string>();
        // OCR sometimes writes "IBAN: DE12 3456 ..." with odd spacing – normalise runs of spaces first.
        var compact = Regex.Replace(text, @"(?<=[A-Z0-9]) {1,2}(?=[A-Z0-9])", " ");
        foreach (Match m in IbanCandidateRegex().Matches(compact))
            if (GermanFormats.TryFixIban(m.Value) is { } iban && !result.Contains(iban))
                result.Add(iban);
        return result;
    }

    [GeneratedRegex(@"\b(?:BIC|SWIFT)(?:[-\s]?Code)?[\s:.]*([A-Z]{6}[A-Z0-9]{2}(?:[A-Z0-9]{3})?)\b")]
    private static partial Regex BicRegex();

    [GeneratedRegex(@"\b(?:DE\s?\d{3}\s?\d{3}\s?\d{3}|ATU\s?\d{8}|NL\d{9}B\d{2}|BE\s?0?\d{9}|FR[A-Z0-9]{2}\d{9}|IT\d{11}|ES[A-Z0-9]\d{7}[A-Z0-9]|PL\d{10}|LU\d{8}|DK\d{8}|CHE[-\s]?\d{3}\.\d{3}\.\d{3}|IE\d{7}[A-Z]{1,2})\b")]
    private static partial Regex VatIdRegex();

    public static List<string> FindVatIds(string text) =>
        VatIdRegex().Matches(text).Select(m => Regex.Replace(m.Value, @"[\s]", "")).Distinct().ToList();

    [GeneratedRegex(@"Steuer[-\s]?(?:Nr\.?|nummer)[\s:.]*(\d{2,3}\s?/\s?\d{3,4}\s?/\s?\d{4,5})", Ci)]
    private static partial Regex TaxNumberRegex();

    [GeneratedRegex(@"Mandats?[-\s]?(?:referenz|ref\.?)(?:[-\s]?(?:nummer|nr\.?))?[\s:.]*([A-Za-z0-9][A-Za-z0-9\-/_.+]{2,34})", Ci)]
    private static partial Regex MandateRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}\s?ZZZ\s?\d{8}\s?\d{0,3}\b|\bDE\d{2}ZZZ\d{11}\b", Ci)]
    private static partial Regex CreditorIdRegex();

    [GeneratedRegex(@"Verwendungszweck[\s:]+([^\n]{3,80})", Ci)]
    private static partial Regex PaymentReferenceRegex();

    // ---------------------------------------------------------------- payment method

    private static readonly (Regex Rx, PaymentMethod Method, int Score)[] PaymentSignals =
    [
        (new(@"Lastschrift|abgebucht|abbuchen|Abbuchung|eingezogen|einziehen|Bankeinzug|Einzugsermächtigung|SEPA[-\s]?(?:Basis|Firmen)", Ci), PaymentMethod.Lastschrift, 3),
        (new(@"Mandatsreferenz|Gl(?:ä|ae)ubiger[-\s]?(?:ID|Identifikationsnummer)", Ci), PaymentMethod.Lastschrift, 3),
        (new(@"(?:wird|werden)\s+(?:von\s+Ihrem\s+Konto\s+)?(?:abgebucht|eingezogen|belastet)|Ihrem\s+Konto\s+belastet", Ci), PaymentMethod.Lastschrift, 4),

        (new(@"(?<!nicht\s)(?<!nicht\s+zu\s)(?:überweisen|ueberweisen)|Überweisung|Ueberweisung", Ci), PaymentMethod.Ueberweisung, 2),
        (new(@"zahlbar\s+(?:bis|innerhalb|binnen|sofort)|Zahlungsziel|bitte\s+(?:begleichen|zahlen|bezahlen|überweisen)|Verwendungszweck|auf\s+(?:unser|folgendes)\s+Konto|innerhalb\s+von\s+\d+\s+Tagen", Ci), PaymentMethod.Ueberweisung, 2),
        (new(@"Bankverbindung|Kontoverbindung", Ci), PaymentMethod.Ueberweisung, 1),
        (new(@"(?:bitte\s+)?nicht\s+(?:zu\s+)?(?:überweisen|ueberweisen)|keine\s+(?:Überweisung|Zahlung)\s+(?:erforderlich|notwendig)", Ci), PaymentMethod.Ueberweisung, -6),

        (new(@"bereits\s+(?:bezahlt|beglichen|erhalten)|dankend\s+erhalten|Betrag\s+(?:haben\s+wir\s+)?erhalten|Zahlung\s+erhalten|bezahlt\s+(?:am|per|mit|via)|PayPal|Vorkasse|Sofort(?:überweisung)?\s+bezahlt|Amazon\s+Pay|Apple\s+Pay|Google\s+Pay|Klarna", Ci), PaymentMethod.BereitsBezahlt, 4),
        (new(@"Kreditkarte|Visa|Mastercard|American\s+Express|Amex", Ci), PaymentMethod.Kreditkarte, 3),
        (new(@"\bbar\s+(?:bezahlt|erhalten)|Barzahlung|Barverkauf", Ci), PaymentMethod.Bar, 4),
    ];

    private static PaymentMethod DetectPaymentMethod(string text, InvoiceData data, bool ownIbanPrinted)
    {
        var scores = new Dictionary<PaymentMethod, int>();
        void Add(PaymentMethod m, int s) => scores[m] = scores.GetValueOrDefault(m) + s;

        foreach (var (rx, method, score) in PaymentSignals)
            if (rx.IsMatch(text)) Add(method, score);

        if (data.MandateReference is not null || data.CreditorId is not null) Add(PaymentMethod.Lastschrift, 2);
        // Our own IBAN on the invoice almost always means "we will debit your account".
        if (ownIbanPrinted) Add(PaymentMethod.Lastschrift, 3);
        if (data.Iban is not null) Add(PaymentMethod.Ueberweisung, 1);

        var ordered = scores.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).ToList();
        if (ordered.Count == 0) return PaymentMethod.Unbekannt;
        if (ordered.Count > 1 && ordered[0].Value == ordered[1].Value)
        {
            // Tie: a specific payment instrument beats the generic "Bankverbindung" signals.
            var specific = ordered.Where(kv => kv.Value == ordered[0].Value && kv.Key != PaymentMethod.Ueberweisung).ToList();
            return specific.Count == 1 ? specific[0].Key : PaymentMethod.Unbekannt;
        }
        return ordered[0].Key;
    }

    // ---------------------------------------------------------------- seller

    [GeneratedRegex(@"(?<![A-Za-zäöüÄÖÜ])(?:GmbH\s*&\s*Co\.?\s*(?:KG|OHG)|gGmbH|GmbH|AG|KGaA|KG|OHG|UG(?:\s*\(haftungsbeschränkt\))?|e\.\s?K(?:fm)?\.?|e\.\s?V\.|GbR|SE|mbH|Ltd\.?|Limited|Inc\.?|LLC|B\.V\.|S\.A\.R\.L\.|S\.?A\.?S\.?|S\.r\.l\.|S\.A\.)(?![A-Za-zäöüÄÖÜ])")]
    private static partial Regex LegalFormRegex();

    [GeneratedRegex(@"Amtsgericht|Registergericht|HRB|HRA|Gesch(?:ä|ae)ftsf(?:ü|ue)hr|Vorstand|Aufsichtsrat|Sitz\s+der|Bank\b|Sparkasse|Volksbank|Kreditinstitut|IBAN|Kontoinhaber|Postbank|Commerzbank|Raiffeisen")]
    private static partial Regex NotSellerContextRegex();

    private string? FindSellerName(string[] lines)
    {
        var candidates = new Dictionary<string, (int Count, int FirstLine, string Display)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var cell in SplitCells(lines[i]).SelectMany(c => Regex.Split(c, @"\s[·•|]\s|\s[-–]\s|,\s|;\s")))
            {
                var segment = cell.Trim();
                if (segment.Length is < 4 or > 70) continue;
                var lf = LegalFormRegex().Match(segment);
                if (!lf.Success) continue;
                if (NotSellerContextRegex().IsMatch(segment)) continue;
                // strip "Absender:", "Firma:" etc.
                segment = Regex.Replace(segment, @"^[^:]{0,25}:\s*", "");
                // cut anything after the legal form (", Musterstraße 1" was already split away)
                lf = LegalFormRegex().Match(segment);
                if (!lf.Success) continue;
                segment = segment[..(lf.Index + lf.Length)].Trim();
                if (segment.Count(char.IsLetter) < lf.Length + 2) continue;
                if (options.IsOwnCompany(segment)) continue;

                var key = Regex.Replace(segment, @"\s+", " ");
                candidates[key] = candidates.TryGetValue(key, out var c) ? (c.Count + 1, c.FirstLine, c.Display) : (1, i, key);
            }
        }
        return candidates.Values
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.FirstLine)
            .Select(c => c.Display)
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> SplitCells(string line) =>
        Regex.Split(line, @"\s{3,}|\t").Where(c => c.Trim().Length > 0);

    private static string Collapse(string s) => Regex.Replace(s, @"\s{2,}", " ").Trim();

    /// <summary>
    /// Finds the first value (matching <paramref name="value"/>) that follows a label — either on the
    /// same line, or in the same column on one of the next lines (table layout).
    /// </summary>
    private static string? FindLabeledValue(string[] lines, Regex label, Regex value, int maxDistance,
        Func<string, bool> accept, bool allowColumn = true)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match lm in label.Matches(lines[i]))
            {
                var after = lm.Index + lm.Length;
                var rest = lines[i][after..];
                var lead = Regex.Match(rest, @"^[\s:#.\-–]*");
                var restTrim = rest[lead.Length..];
                // Value must start right after the label (only separators in between), except
                // for a few filler words like "vom".
                var filler = Regex.Match(restTrim, @"^(?:vom|am|bis|zum|Nr\.?)\s*[:.]?\s*", RegexOptions.IgnoreCase);
                var candidateText = restTrim[filler.Length..];

                var vm = value.Match(candidateText);
                if (vm.Success && vm.Index == 0 && lead.Length + filler.Length <= maxDistance && accept(vm.Value))
                    return vm.Value;

                // Allow the value a bit further right on the same line for dates/amounts
                // ("Rechnungsdatum ........ 03.11.2026"), but only across dots/spaces.
                if (vm.Success && vm.Index > 0 && vm.Index <= maxDistance
                    && Regex.IsMatch(candidateText[..vm.Index], @"^[\s.:_\-–]*$") && accept(vm.Value))
                    return vm.Value;

                if (allowColumn && candidateText.Trim().Length == 0 || allowColumn && Regex.IsMatch(rest, @"^\s{3,}"))
                {
                    if (FindInColumnBelow(lines, i, lm, value, accept) is { } below) return below;
                }
            }
        }
        return null;
    }

    private static string? FindInColumnBelow(string[] lines, int lineIndex, Match labelMatch, Regex value, Func<string, bool> accept)
    {
        var start = labelMatch.Index - 4;
        var end = labelMatch.Index + labelMatch.Length + 12;
        for (var j = lineIndex + 1; j < Math.Min(lines.Length, lineIndex + 4); j++)
        {
            if (lines[j].Trim().Length == 0) continue;
            foreach (Match vm in value.Matches(lines[j]))
            {
                var vEnd = vm.Index + vm.Length;
                if (vEnd >= start && vm.Index <= end && accept(vm.Value)) return vm.Value;
            }
            return null; // only the first non-empty line below counts
        }
        return null;
    }
}
