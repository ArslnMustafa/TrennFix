using TrennFix.Core.Extraction;
using TrennFix.Core.Models;

namespace TrennFix.Core.Tests;

public class LineItemParserTests
{
    private static InvoiceData Parse(string text) =>
        new TextInvoiceParser(new ExtractionOptions { OwnCompanyNames = [SampleInvoices.OwnCompany] })
            .Parse(text, ExtractionSource.PdfText);

    [Fact]
    public void Reads_classic_article_table()
    {
        var d = Parse(SampleInvoices.Ueberweisung);
        Assert.True(d.LinesVerified);
        Assert.Equal(2, d.Lines.Count);

        var paper = d.Lines[0];
        Assert.Equal("1", paper.Position);
        Assert.Equal("Kopierpapier A4, 500 Blatt", paper.Description);
        Assert.Equal(10m, paper.Quantity);
        Assert.Equal(4.99m, paper.UnitPrice);
        Assert.Equal(49.90m, paper.LineTotal);

        var toner = d.Lines[1];
        Assert.Equal("Toner HP 305A", toner.Description);
        Assert.Equal(2m, toner.Quantity);
        Assert.Equal(89.00m, toner.UnitPrice);
    }

    [Fact]
    public void Reads_service_lines_with_amount_only()
    {
        var d = Parse(SampleInvoices.Lastschrift);
        Assert.True(d.LinesVerified);
        Assert.Equal(["Business Flat L", "Zusatzoption Festnetz"], d.Lines.Select(l => l.Description));
        Assert.Equal(49.95m, d.Lines[0].UnitPrice);
        Assert.Equal(1m, d.Lines[0].Quantity);
    }

    [Fact]
    public void Reads_article_numbers_units_multiline_descriptions_and_discounts()
    {
        const string text = """
            Elektro Großhandel Weber GmbH · Industriering 4 · 44137 Dortmund

            Rechnung
            Rechnungsnummer: 2026-88412
            Rechnungsdatum: 12.11.2026

            Pos   Art.-Nr.      Bezeichnung                           Menge   ME     Einzelpreis   Rabatt     Gesamtpreis
            1     NYM-3x1,5     Mantelleitung NYM-J 3x1,5 mm²           100   m            0,6120                  61,20
                                Ring 100 m, grau
            2     4011234567    Abzweigdose AP 80x80 IP54                25   Stk          1,85                    46,25
            3     SCH-16B       Leitungsschutzschalter B16 1-polig        6   Stk          4,90        10 %        26,46
            4                   Versandkostenpauschale                    1   psch         9,90                     9,90
                                Kundenrabatt                                                                    -5,00

                                                                     Nettobetrag                               138,81 €
                                                                     MwSt. 19 %                                 26,37 €
                                                                     Rechnungsbetrag                           165,18 €

            Zahlbar innerhalb von 30 Tagen ohne Abzug.
            """;

        var d = Parse(text);
        Assert.Equal(138.81m, d.NetAmount);
        Assert.True(d.LinesVerified, string.Join("\n", d.Lines.Select(l => $"{l.Description} {l.LineTotal}")));
        Assert.Equal(5, d.Lines.Count);

        var cable = d.Lines[0];
        Assert.Equal("NYM-3x1,5", cable.ArticleNumber);
        Assert.Equal("Mantelleitung NYM-J 3x1,5 mm² Ring 100 m, grau", cable.Description);
        Assert.Equal(100m, cable.Quantity);
        Assert.Equal("m", cable.Unit);
        Assert.Equal(0.6120m, cable.UnitPrice);

        Assert.Equal("4011234567", d.Lines[1].ArticleNumber);
        Assert.Equal("Stk", d.Lines[1].Unit);

        // line discount: unit price stays the list price
        var breaker = d.Lines[2];
        Assert.Equal(6m, breaker.Quantity);
        Assert.Equal(4.90m, breaker.UnitPrice);
        Assert.Equal(26.46m, breaker.LineTotal);

        Assert.Equal("pauschal", d.Lines[3].Unit);
        Assert.Equal(-5.00m, d.Lines[4].LineTotal);
        Assert.False(d.Lines[4].IsPriceRelevant);
    }

    [Fact]
    public void Price_column_before_quantity_and_second_page()
    {
        const string text = """
            Gastro Bedarf Kaya OHG

            Rechnung Nr. GK-5531   Datum 20.11.2026

            Artikel                         Preis     Anzahl     Summe
            Kaffeebohnen Crema 1 kg         18,90        4       75,60
            Milch 3,5 % 1 l                  1,19       24       28,56
                                                     Übertrag   104,16

            Seite 2

            Artikel                         Preis     Anzahl     Summe
            Pappbecher 0,3 l (50 Stk.)       3,49       10       34,90

            Summe netto                                         139,06
            MwSt                                                 20,46
            Gesamtbetrag                                        159,52
            """;

        var d = Parse(text);
        Assert.True(d.LinesVerified);
        Assert.Equal(3, d.Lines.Count);
        Assert.Equal(18.90m, d.Lines[0].UnitPrice);
        Assert.Equal(4m, d.Lines[0].Quantity);
        Assert.Equal("Milch 3,5 % 1 l", d.Lines[1].Description);
        Assert.Equal(24m, d.Lines[1].Quantity);
        Assert.Equal("Pappbecher 0,3 l (50 Stk.)", d.Lines[2].Description);
    }

    [Fact]
    public void No_table_no_lines()
    {
        var d = Parse(SampleInvoices.Mahnung);
        Assert.Empty(d.Lines);
        Assert.False(d.LinesVerified);
    }
}
