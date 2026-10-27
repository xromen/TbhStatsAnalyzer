using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TbhStatsAnalyzer;

public sealed class FilterableDataGrid : DataGrid
{
    private const string FilterHint = "Поиск по части значения. Для чисел: =0, !=0, >1000, >=1000, <0, <=50. Дроби: 1,5 или 1.5. Esc — очистить поле.";
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(FilterableDataGrid), typeof(FilterableDataGrid));
    public static readonly DependencyProperty FilterTextProperty = DependencyProperty.RegisterAttached(
        "FilterText", typeof(string), typeof(FilterableDataGrid), new PropertyMetadata("", FilterChanged));
    public static readonly DependencyProperty FilterErrorProperty = DependencyProperty.RegisterAttached(
        "FilterError", typeof(bool), typeof(FilterableDataGrid), new PropertyMetadata(false));
    public static readonly DependencyProperty FilterHintProperty = DependencyProperty.RegisterAttached(
        "FilterHint", typeof(string), typeof(FilterableDataGrid), new PropertyMetadata(FilterHint));
    public static string GetFilterText(DependencyObject column) => (string)column.GetValue(FilterTextProperty);
    public static void SetFilterText(DependencyObject column, string value) => column.SetValue(FilterTextProperty, value);
    public static bool GetFilterError(DependencyObject column) => (bool)column.GetValue(FilterErrorProperty);
    public static string GetFilterHint(DependencyObject column) => (string)column.GetValue(FilterHintProperty);

    private readonly List<Predicate<object>> rules = [];
    private ListCollectionView? view;
    private bool clearingFilters;

    public FilterableDataGrid() => Columns.CollectionChanged += ColumnsChanged;

    private void ColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DataGridColumn column in e.OldItems) column.ClearValue(OwnerProperty);
        foreach (var column in Columns) column.SetValue(OwnerProperty, this);
        UpdateFilters();
    }
    private static void FilterChanged(DependencyObject column, DependencyPropertyChangedEventArgs e) =>
        (column.GetValue(OwnerProperty) as FilterableDataGrid)?.UpdateFilters();

    public void SetRows(IEnumerable? rows)
    {
        var sorting = Items.SortDescriptions.ToArray();
        var directions = Columns.Select(column => column.SortDirection).ToArray();
        // Each table owns its view, so filtering never changes a shared source or another table.
        view = rows is null ? null : new ListCollectionView(rows as IList ?? rows.Cast<object>().ToList()) { Filter = Matches };
        ItemsSource = view;
        if (view is null) return;
        using (view.DeferRefresh())
            foreach (var sort in sorting) view.SortDescriptions.Add(sort);
        for (int i = 0; i < Columns.Count; i++) Columns[i].SortDirection = directions[i];
    }

    public void ClearFilters()
    {
        clearingFilters = true;
        try { foreach (var column in Columns) SetFilterText(column, ""); }
        finally { clearingFilters = false; }
        UpdateFilters();
    }
    private bool Matches(object item) => rules.All(rule => rule(item));
    private void UpdateFilters()
    {
        if (clearingFilters) return;
        rules.Clear();
        foreach (var column in Columns)
        {
            column.SetValue(FilterErrorProperty, false);
            column.SetValue(FilterHintProperty, FilterHint);
            string query = GetFilterText(column).Trim();
            if (query.Length == 0 || column is not DataGridBoundColumn { Binding: Binding binding } || binding.Path is null) continue;
            var rule = CreateRule(binding, query, out string? error);
            if (error is not null)
            {
                column.SetValue(FilterErrorProperty, true);
                column.SetValue(FilterHintProperty, error);
            }
            rules.Add(rule);
        }
        var selected = SelectedItem;
        view?.Refresh();
        SelectedItem = selected is not null && Items.Contains(selected) ? selected : Items.Cast<object>().FirstOrDefault();
    }
    private static Predicate<object> CreateRule(Binding binding, string query, out string? error)
    {
        error = null;
        string[] path = binding.Path.Path.Split('.');
        var properties = new Dictionary<(Type, string), PropertyInfo?>();
        object? Read(object row)
        {
            object? value = row;
            foreach (string part in path)
            {
                if (value is null) return null;
                var key = (value.GetType(), part);
                if (!properties.TryGetValue(key, out var property))
                    properties[key] = property = key.Item1.GetProperty(part, BindingFlags.Instance | BindingFlags.Public);
                value = property?.GetValue(value);
            }
            return value;
        }
        string? comparison = new[] { ">=", "<=", "!=", ">", "<", "=" }.FirstOrDefault(query.StartsWith);
        if (comparison is not null)
        {
            if (!TryNumber(query[comparison.Length..], out double expected))
            {
                error = "После оператора нужно число, например >=1000 или <0. Для поиска текста уберите оператор.";
                return _ => false;
            }
            return row =>
            {
                object? value = Read(row);
                if (!IsNumber(value)) return false;
                double actual = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(actual)) return false;
                return comparison switch
                {
                    ">=" => actual >= expected, "<=" => actual <= expected, "!=" => actual != expected,
                    ">" => actual > expected, "<" => actual < expected, _ => actual == expected
                };
            };
        }
        return row =>
        {
            object? value = Read(row);
            if (value is null) return false;
            string text = value is IFormattable formatted ? formatted.ToString(binding.StringFormat, Labels.Russian) : value.ToString() ?? "";
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            return IsNumber(value) && (Convert.ToString(value, Labels.Russian)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(value, CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
        };
    }
    private static bool TryNumber(string text, out double number)
    {
        text = text.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
        return (double.TryParse(text, NumberStyles.Float, Labels.Russian, out number) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number);
    }
    private static bool IsNumber(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
