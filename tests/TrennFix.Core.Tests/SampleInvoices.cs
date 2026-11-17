namespace TrennFix.Core.Tests;

/// <summary>Realistic invoice texts in "layout" form (as produced by <c>LayoutText</c>).</summary>
public static class SampleInvoices
{
    public const string OwnCompany = "Muster Handel GmbH";
    public const string OwnIban = "DE89 3704 0044 0532 0130 00";
    public const string OwnVatId = "DE123456789";

    /// <summary>Classic invoice to be paid by bank transfer, values next to labels.</summary>
    public const string Ueberweisung = """
        Bürobedarf Schneider GmbH · Hauptstraße 12 · 50667 Köln

        Muster Handel GmbH
        Industriestr. 5
        80331 München
                                                            Rechnungsnummer:   RE-2026-10457
                                                            Rechnungsdatum:    03.11.2026
                                                            Kundennummer:      K-88231

        Rechnung

        Pos.   Artikel                         Menge     Einzelpreis      Gesamt
        1      Kopierpapier A4, 500 Blatt         10          4,99         49,90
        2      Toner HP 305A                       2         89,00        178,00

                                                        Nettobetrag              227,90 €
                                                        zzgl. 19 % MwSt.          43,30 €
                                                        Gesamtbetrag             271,20 €

        Bitte überweisen Sie den Rechnungsbetrag innerhalb von 14 Tagen ohne Abzug
        unter Angabe der Rechnungsnummer als Verwendungszweck.
        Bei Zahlung bis 10.11.2026 gewähren wir 2 % Skonto.

        Bürobedarf Schneider GmbH   Commerzbank Köln   IBAN: DE90 1004 0000 0123 4567 89   BIC: COBADEFFXXX
        Amtsgericht Köln HRB 12345   USt-IdNr.: DE987654321   Geschäftsführer: Peter Schneider
        """;

    /// <summary>Direct-debit invoice (telecom style), header values in a table below the labels.</summary>
    public const string Lastschrift = """
        NetzFunk Telekommunikation AG, Postfach 1020, 10115 Berlin

        Muster Handel GmbH
        Industriestr. 5
        80331 München

        Ihre Rechnung für Oktober 2026

        Rechnungsnr.          Datum           Kundennummer        Seite
        7700123456            05.11.2026      55512345            1/2

        Leistung                                                        Betrag
        Business Flat L                                                  49,95
        Zusatzoption Festnetz                                            10,00
        Summe netto                                                      59,95
        Umsatzsteuer 19 %                                                11,39
        Rechnungsbetrag                                                  71,34

        Der Rechnungsbetrag wird am 12.11.2026 von Ihrem Konto DE89 3704 0044 0532 0130 00 abgebucht.
        Mandatsreferenz: NF-55512345-01   Gläubiger-ID: DE98ZZZ09999999999

        NetzFunk Telekommunikation AG · Sitz Berlin · Amtsgericht Charlottenburg HRB 99999 · USt-IdNr. DE111222333
        """;

    /// <summary>Already paid online order.</summary>
    public const string BereitsBezahlt = """
        ShopProfi24 GmbH & Co. KG
        Lagerweg 1, 33602 Bielefeld

        Rechnung Nr. SP-998877
        Rechnungsdatum 28.10.2026
        Bestelldatum   27.10.2026

        Artikel                           Menge    Preis
        USB-C Dockingstation               1       119,00

        Gesamtsumme (inkl. MwSt.)                  119,00 EUR
        enthaltene MwSt. 19 %                       19,00 EUR

        Der Betrag wurde bereits per PayPal bezahlt. Vielen Dank für Ihren Einkauf!
        ShopProfi24 GmbH & Co. KG · USt-IdNr. DE444555666
        """;

    public const string Gutschrift = """
        Bürobedarf Schneider GmbH · Hauptstraße 12 · 50667 Köln

        Gutschrift
        Gutschriftsnummer: GS-2026-0042
        Datum: 06.11.2026
        Bezug: Rechnung RE-2026-10457

        Rücksendung Toner HP 305A                                   -89,00
        zzgl. 19 % MwSt.                                            -16,91
        Gutschriftsbetrag                                          -105,91

        Der Betrag wird mit der nächsten Rechnung verrechnet.
        """;

    public const string Mahnung = """
        Hausmeisterservice Yilmaz e.K.
        Gartenstraße 8, 70173 Stuttgart

        1. Mahnung

        Rechnung Nr. 2026-311 vom 01.10.2026
        Offener Betrag 595,00 EUR

        Wir bitten Sie, den Betrag bis zum 20.11.2026 auf unser Konto zu überweisen.
        IBAN DE44 5001 0517 5407 3249 31
        """;
}
