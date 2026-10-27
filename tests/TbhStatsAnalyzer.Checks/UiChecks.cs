using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TbhStatsAnalyzer;

internal static class UiChecks
{
    public static void Run(bool filters = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderDeathTab(filters); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("WPF UI check failed", failure);
        Console.WriteLine(filters ? "UI PASS: column filters, refresh, sorting, selection, reset and WPF bindings. Render: artifacts/verification/column-filters.png" :
            "UI PASS: death selection, totals, breakdown, timeline and WPF bindings. Render: artifacts/verification/deaths-tab.png");
    }
    private static void RenderDeathTab(bool filters)
    {
        var app = new App(); app.InitializeComponent();
        var errors = new StringWriter();
        var trace = new TextWriterTraceListener(errors) { Filter = new EventTypeFilter(SourceLevels.Error) };
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        var window = new MainWindow();
        var meter = (CombatMeter)typeof(MainWindow).GetField("meter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        meter.Reset(new Snapshot { Ready = true, TimeMs = 1000, StageKey = 1 });
        var snapshot = new Snapshot
        {
            Ready = true, TimeMs = 30500, LastSequence = 4, StageKey = 1, Act = 23, StageNo = 6, StageLevel = 90,
            Events =
            [
                new() { Sequence = 1, TimeMs = 25000, TargetHero = true, TargetId = "hero", Target = "Рыцарь", AttackerId = "goblin", Attacker = "Гоблин", Damage = 40, HpLost = 40, HpBefore = 120, HpAfter = 80, Element = 0, DamageType = 1 },
                new() { Sequence = 2, TimeMs = 26000, TargetHero = true, TargetId = "hero", Target = "Рыцарь", AttackerId = "priest", Attacker = "Жрец", Healing = 20, HpBefore = 80, HpAfter = 100 },
                new() { Sequence = 3, TimeMs = 30000, TargetHero = true, TargetId = "hero", Target = "Рыцарь", AttackerId = "elemental", Attacker = "Огненный элементаль", Damage = 160, HpLost = 100, HpBefore = 100, HpAfter = 0, Element = 1, DamageType = 3, Critical = true },
                new() { Sequence = 4, TimeMs = 30000, TargetHero = true, TargetId = "hero", Target = "Рыцарь", AttackerId = "elemental", Attacker = "Огненный элементаль", Death = true, FatalHitSequence = 3, DeathBoundarySequence = 3 }
            ]
        };
        meter.Update(snapshot);
        typeof(MainWindow).GetField("last", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, snapshot);
        typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, null);
        var tabs = (TabControl)window.FindName("Tabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Смерти"));
        var root = (Grid)window.Content;
        root.Background = new SolidColorBrush(Color.FromRgb(11, 19, 32));
        root.Measure(new Size(1432, 850)); root.Arrange(new Rect(0, 0, 1432, 850)); root.UpdateLayout();
        if (((DataGrid)window.FindName("DeathsGrid")).SelectedItem is not DeathRecord { Damage: 200, HpLost: 140, Healing: 20 })
            throw new Exception("Death row and selection do not match the combat data");
        if (((DataGrid)window.FindName("DeathDamageGrid")).Items.Count != 2 || ((DataGrid)window.FindName("DeathTimelineGrid")).Items.Count != 3)
            throw new Exception("Death detail grids are not populated");
        if (!((TextBlock)window.FindName("DeathSummaryLabel")).Text.Contains("160,0"))
            throw new Exception("Fatal hit summary was not rendered");
        if (filters) ColumnFilterChecks.Run(window, meter, snapshot);
        trace.Flush();
        if (!string.IsNullOrWhiteSpace(errors.ToString())) throw new Exception(errors.ToString());
        Directory.CreateDirectory("artifacts/verification");
        var bitmap = new RenderTargetBitmap(1432, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(filters ? "artifacts/verification/column-filters.png" : "artifacts/verification/deaths-tab.png"); png.Save(stream);
        if (filters) { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); return; }
        var timelineTab = (TabItem)LogicalTreeHelper.GetParent((DataGrid)window.FindName("DeathTimelineGrid"));
        timelineTab.IsSelected = true; root.UpdateLayout();
        var timelineBitmap = new RenderTargetBitmap(1432, 850, 96, 96, PixelFormats.Pbgra32); timelineBitmap.Render(root);
        var timelinePng = new PngBitmapEncoder(); timelinePng.Frames.Add(BitmapFrame.Create(timelineBitmap));
        using var timelineStream = File.Create("artifacts/verification/death-timeline.png"); timelinePng.Save(timelineStream);
        trace.Flush();
        if (!string.IsNullOrWhiteSpace(errors.ToString())) throw new Exception(errors.ToString());
        PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
    }
}
