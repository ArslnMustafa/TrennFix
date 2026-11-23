using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using TrennFix.Core.Export;
using TrennFix.Core.Models;
using TrennFix.Core.Pricing;
using TrennFix.Core.Storage;

namespace TrennFix.App;

public sealed class ProductRow(ProductGroup g, decimal alertPercent)
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public ProductGroup Group => g;
    public string Name => g.Name;
    public string Unit => g.Unit ?? "";
    public int Purchases => g.PurchaseCount;
    public int Suppliers => g.SupplierCount;
    public decimal LastPrice => g.LastPrice;
    public string LastSupplier => g.Last.SupplierName;
    public DateTime LastDate => g.Last.EffectiveDate.ToDateTime(TimeOnly.MinValue);
    public decimal? PreviousPrice => g.Previous?.Line.UnitPrice;
    public string ChangeText => g.ChangePercent is { } p && p != 0 ? p.ToString("+0.0;-0.0", De) + " %" : "";
    public bool IsIncrease => g.ChangePercent > alertPercent;
    public bool IsDecrease => g.ChangePercent < 0;
    public decimal? CheapestPrice => g.PurchaseCount > 1 ? g.Cheapest.Line.UnitPrice : null;
    public string CheapestSupplier => g.PurchaseCount > 1 ? g.Cheapest.SupplierName : "";
    public string AboveCheapestText => g.PurchaseCount > 1 && g.AboveCheapestPercent > 0 ? g.AboveCheapestPercent.ToString("0.0", De) + " %" : "";
    public bool PaidTooMuch => g.AboveCheapestPercent > alertPercent;

    public bool Matches(string term) =>
        string.IsNullOrWhiteSpace(term)
        || g.Purchases.Any(p => p.Line.Description.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)
                                || p.SupplierName.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)
                                || (p.Line.ArticleNumber?.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
}

public sealed class PurchaseRow(PriceHistoryEntry p)
{
    public PriceHistoryEntry Entry => p;
    public DateTime Date => p.EffectiveDate.ToDateTime(TimeOnly.MinValue);
    public string Supplier => p.SupplierName;
    public string Invoice => p.InvoiceNumber ?? "";
    public string Description => p.Line.Description;
    public decimal? Quantity => p.Line.Quantity;
    public string Unit => p.Line.Unit ?? "";
    public decimal? UnitPrice => p.Line.UnitPrice;
    public string VerifiedText => p.Verified ? "✓" : "?";
    public string MemberKey => ProductCatalog.MemberKey(p.SupplierId, p.Line);
}

public partial class PriceComparisonWindow : Window
{
    private readonly InvoiceDatabase _db;
    private readonly decimal _alertPercent;
    private List<ProductRow> _all = [];

    public PriceComparisonWindow(InvoiceDatabase db, decimal alertPercent)
    {
        InitializeComponent();
        _db = db;
        _alertPercent = alertPercent;
        Reload();
    }

    private void Reload()
    {
        var groups = new ProductCatalog(_db.GetPriceHistory(), _db.GetProductOverrides()).Groups();
        _all = groups.Select(g => new ProductRow(g, _alertPercent)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (GroupsGrid is null) return;
        var rows = _all.Where(r =>
                (ShowSingles.IsChecked == true || r.Purchases >= 2)
                && (OnlyIncreases.IsChecked != true || r.IsIncrease)
                && (OnlyCheaper.IsChecked != true || r.PaidTooMuch)
                && r.Matches(SearchBox.Text))
            .ToList();
        GroupsGrid.ItemsSource = rows;
        PurchasesGrid.ItemsSource = null;

        var compared = _all.Count(r => r.Purchases >= 2);
        StatusText.Text = $"{compared} Produkte mehrfach gekauft · {_all.Count(r => r.IsIncrease)} mit Preiserhöhung · " +
                          $"{_all.Count(r => r.PaidTooMuch)} zuletzt teurer als anderswo · {_all.Count(r => r.Purchases == 1)} bisher nur einmal gekauft";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnGroupSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = GroupsGrid.SelectedItems.Cast<ProductRow>().ToList();
        PurchasesGrid.ItemsSource = selected
            .SelectMany(r => r.Group.Purchases)
            .OrderByDescending(p => p.EffectiveDate)
            .Select(p => new PurchaseRow(p))
            .ToList();
    }

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        var selected = GroupsGrid.SelectedItems.Cast<ProductRow>().ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show(this, "Bitte oben mindestens zwei Zeilen markieren (Strg + Klick), die dasselbe Produkt sind.", "Zusammenführen");
            return;
        }
        if (MessageBox.Show(this, "Diese Produkte als dasselbe Produkt behandeln?\n\n" + string.Join("\n", selected.Select(r => "• " + r.Name)),
                "Zusammenführen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _db.MergeProducts(selected.SelectMany(r => r.Group.MemberKeys));
        Reload();
    }

    private void OnSeparate(object sender, RoutedEventArgs e)
    {
        var purchases = PurchasesGrid.SelectedItems.Cast<PurchaseRow>().ToList();
        if (purchases.Count == 0)
        {
            MessageBox.Show(this, "Bitte unten die Einkäufe markieren, die NICHT zu diesem Produkt gehören.", "Trennen");
            return;
        }
        _db.SeparateProducts(purchases.Select(p => p.MemberKey));
        Reload();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel-Datei (*.xlsx)|*.xlsx",
            FileName = $"Preisvergleich_{DateTime.Today:yyyy-MM-dd}.xlsx",
        };
        if (dlg.ShowDialog(this) != true) return;
        PriceListExporter.Export(_all.Select(r => r.Group).ToList(), dlg.FileName, _alertPercent);
        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }
}
