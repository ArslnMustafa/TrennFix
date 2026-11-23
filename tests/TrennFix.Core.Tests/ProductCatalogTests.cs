using TrennFix.Core.Models;
using TrennFix.Core.Pricing;

namespace TrennFix.Core.Tests;

public class ProductCatalogTests
{
    private static long _invoice;

    private static PriceHistoryEntry Buy(long supplier, string desc, decimal price, string date, string? unit = "Stk", string? art = null, string? ean = null) =>
        new(++_invoice, supplier, $"Lieferant {supplier}", $"R-{_invoice}", DateOnly.Parse(date, System.Globalization.CultureInfo.GetCultureInfo("de-DE")),
            DateTimeOffset.Now, new InvoiceLine
            {
                Description = desc, UnitPrice = price, Quantity = 1, LineTotal = price, Unit = unit, ArticleNumber = art, Ean = ean,
            }, Verified: true);

    private static ProductGroup GroupOf(ProductCatalog c, string name) => c.Groups().Single(g => g.Purchases.Any(p => p.Line.Description == name));

    [Fact]
    public void Same_product_written_differently_at_different_suppliers_is_matched()
    {
        var c = new ProductCatalog(
        [
            Buy(1, "Toner HP 305A schwarz", 89.00m, "03.10.2026"),
            Buy(2, "HP 305A Toner Schwarz original", 82.50m, "20.10.2026"),
            Buy(1, "Toner HP 305A, schwarz", 97.50m, "15.11.2026"),
            Buy(1, "Kopierpapier A4 80g 500 Blatt", 4.99m, "03.10.2026"),
            Buy(3, "Kopierpapier A4, 80 g, 500 Bl.", 4.49m, "01.11.2026"),
        ], []);

        var toner = GroupOf(c, "Toner HP 305A schwarz");
        Assert.Equal(3, toner.PurchaseCount);
        Assert.Equal(2, toner.SupplierCount);
        Assert.Equal(97.50m, toner.LastPrice);
        Assert.Equal(82.50m, toner.Previous!.Line.UnitPrice);
        Assert.Equal("Lieferant 2", toner.Cheapest.SupplierName);
        Assert.InRange(toner.AboveCheapestPercent, 18.1m, 18.2m);

        var paper = GroupOf(c, "Kopierpapier A4 80g 500 Blatt");
        Assert.Equal(2, paper.PurchaseCount);
    }

    [Fact]
    public void Different_models_sizes_or_units_are_not_matched()
    {
        var c = new ProductCatalog(
        [
            Buy(1, "Toner HP 305A schwarz", 89m, "01.10.2026"),
            Buy(1, "Toner HP 305X schwarz", 120m, "02.10.2026"),
            Buy(1, "Spax Schraube 4x40", 0.05m, "01.10.2026"),
            Buy(1, "Spax Schraube 4x50", 0.06m, "02.10.2026"),
            Buy(1, "Mantelleitung NYM-J 3x1,5 mm²", 0.61m, "01.10.2026", "m"),
            Buy(1, "Mantelleitung NYM-J 3x2,5 mm²", 0.95m, "02.10.2026", "m"),
            Buy(1, "Kaffee Crema", 18.90m, "01.10.2026", "kg"),
            Buy(1, "Kaffee Crema", 4.90m, "02.10.2026", "Stk"),
            Buy(1, "Reinigung Büro", 120m, "01.10.2026", null),
            Buy(1, "Reinigung Treppenhaus", 80m, "02.10.2026", null),
        ], []);

        Assert.All(c.Groups(), g => Assert.Equal(1, g.PurchaseCount));
    }

    [Fact]
    public void Ean_and_article_number_win_over_the_description()
    {
        var c = new ProductCatalog(
        [
            Buy(1, "Abzweigdose AP grau", 1.85m, "01.10.2026", art: "4711"),
            Buy(1, "Feuchtraum-Abzweigdose 80x80 IP54", 1.95m, "01.11.2026", art: "4711"),
            Buy(2, "Dose AP", 1.70m, "05.11.2026", ean: "4011234567890"),
            Buy(3, "Aufputz Abzweigkasten", 1.60m, "06.11.2026", ean: "4011234567890"),
        ], []);

        Assert.Equal(2, GroupOf(c, "Abzweigdose AP grau").PurchaseCount);
        Assert.Equal(2, GroupOf(c, "Dose AP").PurchaseCount);
    }

    [Fact]
    public void Manual_merge_and_separate_override_the_automatic_matching()
    {
        var a = Buy(1, "Druckerpapier weiß", 4.99m, "01.10.2026");
        var b = Buy(2, "Copy Paper white", 4.20m, "01.11.2026");
        var t1 = Buy(1, "Toner HP 305A", 89m, "01.10.2026");
        var t2 = Buy(1, "Toner HP 305A Rückläufer", 20m, "01.11.2026");
        PriceHistoryEntry[] all = [a, b, t1, t2];

        var auto = new ProductCatalog(all, []);
        Assert.Equal(1, GroupOf(auto, "Druckerpapier weiß").PurchaseCount);
        Assert.Equal(2, GroupOf(auto, "Toner HP 305A").PurchaseCount);

        var manual = new ProductCatalog(all,
        [
            new ProductOverride(ProductCatalog.MemberKey(1, a.Line), "manual:paper", false),
            new ProductOverride(ProductCatalog.MemberKey(2, b.Line), "manual:paper", false),
            new ProductOverride(ProductCatalog.MemberKey(1, t2.Line), null, true),
        ]);
        Assert.Equal(2, GroupOf(manual, "Druckerpapier weiß").PurchaseCount);
        Assert.Equal(1, GroupOf(manual, "Toner HP 305A").PurchaseCount);
    }

    [Fact]
    public void Finds_earlier_purchases_for_a_new_line()
    {
        var c = new ProductCatalog([Buy(2, "HP 305A Toner Schwarz", 82.50m, "20.10.2026")], []);
        var hits = c.FindPurchases(1, new InvoiceLine { Description = "Toner HP 305A schwarz", UnitPrice = 97.5m, Unit = "Stk" });
        Assert.Single(hits);
        Assert.Empty(c.FindPurchases(1, new InvoiceLine { Description = "Toner HP 305X schwarz", UnitPrice = 120m, Unit = "Stk" }));
    }
}
