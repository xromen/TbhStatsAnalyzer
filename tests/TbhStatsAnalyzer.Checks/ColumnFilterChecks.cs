using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using TbhStatsAnalyzer;

internal static class ColumnFilterChecks
{
    public static void Run(MainWindow window, CombatMeter meter, Snapshot snapshot)
    {
        FilterableDataGrid Grid(string name) => (FilterableDataGrid)window.FindName(name);
        void Render() => typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, null);
        void Reset() => typeof(MainWindow).GetMethod("ClearFiltersClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        var grids = new[] { "HeroesGrid", "HeroStatsGrid", "BreakdownGrid", "EnemiesGrid", "EnemyStatsGrid", "EventsGrid", "DeathsGrid", "DeathDamageGrid", "DeathTimelineGrid" }.Select(Grid).ToArray();
        var stats = Grid("HeroStatsGrid");
        var enemies = Grid("EnemyStatsGrid");
        var source = new ObservableCollection<StatRow> { new("Огонь", 1234.5), new("Огонь: бонус", 50), new("Холод", -10) };
        stats.SetRows(source); enemies.SetRows(source);
        Set(stats, "Name", "ОГОНЬ"); Rows(stats, 2);
        Set(stats, "Value", ">=1000"); Rows(stats, 1);
        Rows(enemies, 3); Check(source.Count == 3, "Filters must not modify the source or other tables");
        foreach (string query in new[] { "=1 234,5", "=1234.5", ">50", ">=1234,5", "!=50" })
        { Set(stats, "Value", query); Rows(stats, 1); }
        Set(stats, "Name", ""); Set(stats, "Value", "<0"); Rows(stats, 1);
        Set(stats, "Value", "<=50"); Rows(stats, 2);
        Set(stats, "Value", "1234.5"); Rows(stats, 1);
        Set(stats, "Value", "1\u00A0234,500"); Rows(stats, 1);
        var valueColumn = Set(stats, "Value", ">=oops"); Rows(stats, 0);
        Check(FilterableDataGrid.GetFilterError(valueColumn), "Invalid numeric filter must be marked");
        foreach (string query in new[] { "=NaN", "=Infinity", ">=", "==10" }) { Set(stats, "Value", query); Rows(stats, 0); }
        Set(stats, "Value", ">0"); Check(!FilterableDataGrid.GetFilterError(valueColumn), "Corrected filter must clear the error");
        source.Add(new("Огонь: новый", 100)); Rows(stats, 3);
        stats.Items.SortDescriptions.Add(new(nameof(StatRow.Value), ListSortDirection.Descending));
        stats.Columns[1].SortDirection = ListSortDirection.Descending;
        stats.SetRows(new List<StatRow> { new("Новый", 10), new("Другой", 100), new("Скрытый", -5) });
        Rows(stats, 2); Check(((StatRow)stats.Items[0]).Value == 100, "Sorting and filter must survive source replacement");
        Check(stats.Columns[1].SortDirection == ListSortDirection.Descending, "Sort indicator must survive source replacement");
        stats.ClearFilters(); Rows(stats, 3);

        var timeline = Grid("DeathTimelineGrid");
        Set(timeline, "Hit.ElementName", "огонь"); Set(timeline, "Hit.Damage", ">100"); Rows(timeline, 1);
        Check(((DeathHitRow)timeline.Items[0]).Fatal, "Nested filters must select the fatal fire hit");
        Set(Grid("DeathDamageGrid"), "Element", "огонь"); Rows(Grid("DeathDamageGrid"), 1);
        var deathGrid = Grid("DeathsGrid");
        var death = (DeathRecord)deathGrid.Items[0];
        deathGrid.SetRows(new[] { death, death with { Sequence = 99, FatalHit = null } });
        Set(deathGrid, "FatalDamage", "=0"); Rows(deathGrid, 0);
        Set(deathGrid, "FatalDamage", ">100"); Rows(deathGrid, 1);
        Set(deathGrid, "Hero", "Нет такого героя"); Rows(deathGrid, 0);
        Render(); Rows(deathGrid, 0); Rows(timeline, 0);
        Check(deathGrid.SelectedItem is null, "Filtered-out deaths must not remain selected");
        Set(deathGrid, "Hero", "рыцарь"); Rows(deathGrid, 1); Rows(timeline, 1);
        Check(deathGrid.SelectedItem is DeathRecord, "Returning a death must restore its filtered details");
        var direction = (ComboBox)window.FindName("DirectionPicker");
        var breakdown = Grid("BreakdownGrid");
        direction.SelectedIndex = 1;
        Set(breakdown, "Element", "огонь"); Set(breakdown, "Damage", ">100"); Rows(breakdown, 1);
        direction.SelectedIndex = 0; Rows(breakdown, 0);
        direction.SelectedIndex = 3; Render(); Rows(breakdown, 2);
        direction.SelectedIndex = 0;

        snapshot.Heroes = [new() { Id = "hero", Name = "Рыцарь", IsHero = true, Hp = 100, MaxHp = 200, AttackDamage = 1234.5, Stats = new double[81] },
            new() { Id = "priest", Name = "Жрец", IsHero = true, Hp = 50, MaxHp = 150, AttackDamage = 50, Stats = new double[81] }];
        snapshot.Enemies = [new() { Id = "goblin", Name = "Гоблин", Hp = 70, MaxHp = 70, Stats = new double[81] }];
        meter.Update(snapshot); Reset(); Render();
        var heroes = Grid("HeroesGrid");
        Set(heroes, "Name", "жрец"); Rows(heroes, 1);
        Set(stats, "Name", "защита"); Set(stats, "Value", ">=0");
        Render(); Rows(heroes, 1);
        Check(((ActorRow)heroes.SelectedItem).Name == "Жрец" && ((TextBlock)window.FindName("HeroDetailTitle")).Text == "Жрец", "Selection must stay inside the filtered team");
        Check(stats.Items.Count > 0 && stats.Items.OfType<StatRow>().All(r => r.Name.Contains("защита", StringComparison.OrdinalIgnoreCase) && r.Value >= 0), "Detail filters must survive actor updates");
        Set(heroes, "Name", "Не существует"); Render(); Rows(heroes, 0); Rows(stats, 0);
        Set(Grid("EnemiesGrid"), "Name", "Не существует"); Render(); Rows(enemies, 0);
        Set(Grid("EventsGrid"), "ElementName", "огонь"); Rows(Grid("EventsGrid"), 1);
        Render(); Rows(Grid("EventsGrid"), 1);
        string csv = CsvExport.Create(meter, snapshot);
        Check(csv.Contains("Жрец") && csv.Contains("Рыцарь") && meter.TotalTaken == 200, "Filtering must not alter the measurement or CSV");
        Reset(); Render(); Rows(heroes, 2); Rows(Grid("EnemiesGrid"), 1); Rows(Grid("EventsGrid"), 4);
        Check(grids.All(g => g.Columns.All(c => FilterableDataGrid.GetFilterText(c) == "")), "Reset must clear every column in every table");

        // Render the real header template and type through its WPF binding.
        var tabs = (TabControl)window.FindName("Tabs"); tabs.SelectedIndex = 0;
        var root = (System.Windows.Controls.Grid)window.Content;
        root.Measure(new Size(1432, 850)); root.Arrange(new Rect(0, 0, 1432, 850)); root.UpdateLayout();
        var header = Children<DataGridColumnHeader>(heroes).Single(h => h.Column == heroes.Columns[0]);
        var input = Children<TextBox>(header).Single();
        input.Text = "РЫЦ"; Rows(heroes, 1);
        Check(FilterableDataGrid.GetFilterText(heroes.Columns[0]) == "РЫЦ", "Header input must update the column filter");
        Render(); root.UpdateLayout(); Rows(heroes, 1);
        Check(input.Text == "РЫЦ" && input.ActualHeight == 24 && header.ActualHeight >= 60, "Header must preserve entered text and fit its filter field");
        Reset(); Check(input.Text == "", "Reset must update the visible input");
        input.Text = "РЫЦ";
        Set(stats, "Name", "сопротивление"); Set(stats, "Value", ">=0");
        // Offscreen star sizing has no window viewport; use the viewport width for the preview.
        stats.Columns[0].Width = Math.Max(300, stats.ActualWidth - 150);
        root.UpdateLayout();
    }
    private static DataGridColumn Set(FilterableDataGrid grid, string path, string query)
    {
        var column = grid.Columns.OfType<DataGridBoundColumn>().Single(c => c.Binding is Binding binding && binding.Path.Path == path);
        FilterableDataGrid.SetFilterText(column, query); return column;
    }
    private static void Rows(FilterableDataGrid grid, int expected) => Check(grid.Items.Count == expected, $"{grid.Name}: expected {expected} visible rows, got {grid.Items.Count}");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
