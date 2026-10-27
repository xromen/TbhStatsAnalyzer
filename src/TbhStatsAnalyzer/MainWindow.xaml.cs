using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TbhStatsAnalyzer;

public partial class MainWindow : Window
{
    private readonly CombatMeter meter = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource lifetime = new();
    private GameConnection? connection;
    private Snapshot? last;
    private bool busy, closing, rebuilding;
    public sealed record GameChoice(int Pid, string Label);
    public MainWindow()
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage("ru-RU");
        timer.Tick += Poll;
    }
    private void WindowLoaded(object sender, RoutedEventArgs e) => RefreshGames();
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshGames();
    private void RefreshGames()
    {
        try
        {
            var games = GameConnection.FindGames();
            ProcessPicker.ItemsSource = games.Select(p => new GameChoice(p.Id, $"TaskBarHero · PID {p.Id}")).ToList();
            foreach (var p in games) p.Dispose();
            if (ProcessPicker.Items.Count > 0) ProcessPicker.SelectedIndex = 0;
            else StatusLabel.Text = "TaskBarHero не запущена. Запустите игру и нажмите «Обновить».";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if (connection is not null)
            {
                await DisconnectAsync();
                StatusLabel.Text = "Отключено. Сбор остановлен; итоги последнего замера сохранены.";
                return;
            }
            if (ProcessPicker.SelectedItem is not GameChoice choice)
            {
                RefreshGames();
                if (ProcessPicker.SelectedItem is not GameChoice) return;
                choice = (GameChoice)ProcessPicker.SelectedItem;
            }
            StatusLabel.Text = "Подключение к игре и проверка совместимости…";
            var candidate = new GameConnection();
            connection = candidate;
            await candidate.ConnectAsync(choice.Pid, lifetime.Token);
            last = await candidate.ReadAsync(0, lifetime.Token);
            meter.Reset(last);
            Render(); timer.Start();
            ConnectButton.Content = "Отключиться";
            ProcessPicker.IsEnabled = false;
            ResetButton.IsEnabled = ExportButton.IsEnabled = true;
            ConnectionLabel.Text = $"ПОДКЛЮЧЕНО · PID {choice.Pid}";
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            await DisconnectAsync(); StatusLabel.Text = ex.Message;
            ConnectionLabel.Text = "НЕ УДАЛОСЬ ПОДКЛЮЧИТЬСЯ";
        }
        finally { busy = false; ConnectButton.IsEnabled = true; }
    }
    private async void Poll(object? sender, EventArgs e)
    {
        if (busy || connection is null || closing) return;
        busy = true;
        try
        {
            var snapshot = await connection.ReadAsync(meter.Cursor, lifetime.Token);
            if (!string.IsNullOrEmpty(snapshot.Error)) throw new InvalidOperationException(snapshot.Error);
            if (!snapshot.Ready || snapshot.TimeMs - snapshot.SampleTimeMs > 5000)
                throw new InvalidOperationException("Данные игры не обновляются. Возобновите игру и подключитесь снова.");
            last = snapshot; meter.Update(snapshot); Render();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            await DisconnectAsync(); StatusLabel.Text = ex.Message;
        }
        finally { busy = false; }
    }
    private async Task DisconnectAsync()
    {
        timer.Stop();
        var previous = connection; connection = null;
        if (previous is not null)
        {
            try { await previous.DisposeAsync(); }
            catch (Exception ex) { StatusLabel.Text = $"Отключение: {ex.Message}"; }
        }
        ConnectButton.Content = "Подключиться";
        ProcessPicker.IsEnabled = true; ResetButton.IsEnabled = false;
        ConnectionLabel.Text = "ОТКЛЮЧЕНО";
    }
    private void Render()
    {
        if (last is null) return;
        DpsLabel.Text = Labels.N(meter.Dps); AverageLabel.Text = $"Средний DPS: {Labels.N(meter.AverageDps)}";
        DamageLabel.Text = Labels.N(meter.TotalDamage); TakenLabel.Text = Labels.N(meter.TotalTaken);
        TakenDpsLabel.Text = $"Входящий DPS: {Labels.N(meter.IncomingDps)}";
        HealingLabel.Text = Labels.N(meter.Healing); EnemiesLabel.Text = $"Врагов на карте: {meter.Enemies.Count}";
        HitsLabel.Text = $"Попаданий: {meter.HitCount:N0} · потеря HP: {Labels.N(meter.TotalHpLost)}";
        var elapsed = TimeSpan.FromSeconds(meter.ElapsedSeconds);
        TimerLabel.Text = $"Замер {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        string state = last.StageState switch { 1 => "Появление врагов", 2 => "Бой", 3 => "Перестроение", _ => "Ожидание" };
        MapLabel.Text = $"Акт {last.Act} · карта {last.StageNo} · уровень {last.StageLevel} · {state}";
        rebuilding = true;
        UpdateActors(HeroesGrid, meter.Heroes); UpdateActors(EnemiesGrid, meter.Enemies);
        rebuilding = false;
        ShowDetails(HeroesGrid); ShowDetails(EnemiesGrid); FilterBreakdown();
        EventsGrid.SetRows(meter.Recent.Reverse().ToList());
        RenderDeaths();
        StatusLabel.Text = $"Данные {DateTime.Now:HH:mm:ss} · обновление 0,5 с · пропущено событий: {meter.LostEvents} · ошибок чтения: {last.CaptureErrors} · смена карты начинает новый замер";
    }
    private static void UpdateActors(FilterableDataGrid grid, List<ActorRow> rows)
    {
        string? selected = (grid.SelectedItem as ActorRow)?.Id;
        grid.SetRows(rows);
        grid.SelectedItem = grid.Items.OfType<ActorRow>().FirstOrDefault(r => r.Id == selected) ?? grid.Items.OfType<ActorRow>().FirstOrDefault();
    }
    private void UnitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rebuilding && sender is DataGrid grid) ShowDetails(grid);
    }
    private void ShowDetails(DataGrid grid)
    {
        bool hero = grid == HeroesGrid;
        var detail = hero ? HeroStatsGrid : EnemyStatsGrid;
        var title = hero ? HeroDetailTitle : EnemyDetailTitle;
        if (grid.SelectedItem is not ActorRow row) { detail.SetRows(null); return; }
        title.Text = row.Name;
        StatRow[] effective = [new("В бою · урон атаки", row.Attack), new("В бою · защита", row.Armor),
            new("В бою · максимум HP", row.Unit.MaxHp), new("В бою · скорость атаки", row.AttackSpeed),
            new("В бою · шанс крита, %", row.CritChance), new("В бою · критический множитель", row.Unit.CriticalDamage ?? row.Unit.Stat(4)),
            new("В бою · сопротивление огню, %", row.Fire), new("В бою · сопротивление холоду, %", row.Cold),
            new("В бою · сопротивление молнии, %", row.Lightning), new("В бою · сопротивление хаосу, %", row.Chaos)];
        detail.SetRows(effective.Concat(Enumerable.Range(1, Math.Min(row.Unit.Stats.Length, Labels.StatNames.Length - 1))
            .Select(i => new StatRow("Кэш · " + Labels.StatNames[i], row.Unit.Stat(i)))).ToList());
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) FilterBreakdown();
    }
    private void FilterBreakdown()
    {
        if (BreakdownGrid is null || DirectionPicker is null) return;
        string? direction = (DirectionPicker.SelectedItem as ComboBoxItem)?.Content?.ToString();
        BreakdownGrid.SetRows(meter.Breakdown.Where(r => direction == "Все направления" || r.Direction == direction).ToList());
    }
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (last is null || busy) return;
        meter.Reset(last); Render();
    }
    private void RenderDeaths()
    {
        var selected = DeathsGrid.SelectedItem as DeathRecord;
        bool followLatest = DeathsGrid.SelectedIndex <= 0;
        var rows = meter.Deaths.Records.Reverse().ToList();
        rebuilding = true;
        DeathsGrid.SetRows(rows);
        var visible = DeathsGrid.Items.OfType<DeathRecord>();
        DeathsGrid.SelectedItem = followLatest ? visible.FirstOrDefault() :
            visible.FirstOrDefault(r => r.Sequence == selected?.Sequence) ?? visible.FirstOrDefault();
        rebuilding = false;
        ShowDeathDetails();
    }
    private void DeathSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rebuilding) ShowDeathDetails();
    }
    private void ShowDeathDetails()
    {
        if (DeathsGrid.SelectedItem is not DeathRecord death)
        {
            DeathDetailTitle.Text = "Смерти пока не зарегистрированы";
            DeathSummaryLabel.Text = "Выберите зарегистрированную смерть для разбора последних 10 секунд текущей жизни. Смерти до подключения не восстанавливаются.";
            DeathCompletenessLabel.Text = "";
            DeathDamageGrid.SetRows(null); DeathTimelineGrid.SetRows(null);
            return;
        }
        DeathDetailTitle.Text = $"{death.Hero} · {death.TimeLabel} · добил: {death.Killer}";
        DeathSummaryLabel.Text = $"Перед смертью: {Labels.N(death.Damage)} урона, {Labels.N(death.HpLost)} потери HP, {Labels.N(death.Healing)} лечения. " +
            (death.FatalHit is { } fatal ? $"Последний удар: {Labels.N(fatal.Damage)} · HP до удара: {Labels.N(fatal.HpBefore)} · {death.Cause}." : death.Cause + ".");
        DeathCompletenessLabel.Text = death.Partial ?
            "История неполная: замер начат менее 10 секунд назад, были пропуски/ошибки чтения или отсутствует связанный смертельный удар. Показан только зарегистрированный урон." : "";
        DeathDamageGrid.SetRows(death.Breakdown);
        DeathTimelineGrid.SetRows(death.Timeline);
    }
    private void ClearFiltersClick(object sender, RoutedEventArgs e)
    {
        foreach (var grid in new[] { HeroesGrid, HeroStatsGrid, BreakdownGrid, EnemiesGrid, EnemyStatsGrid,
            EventsGrid, DeathsGrid, DeathDamageGrid, DeathTimelineGrid }) grid.ClearFilters();
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"TaskbarHero-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, CsvExport.Create(meter, last), new UTF8Encoding(true));
            StatusLabel.Text = $"Замер сохранён: {dialog.FileName}";
        }
        catch (Exception ex) { StatusLabel.Text = $"Не удалось сохранить CSV: {ex.Message}"; }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; lifetime.Cancel();
        timer.Stop();
        // Poll or Connect owns the connection until its async operation unwinds.
        while (busy) await Task.Delay(50);
        await DisconnectAsync(); lifetime.Dispose(); Close();
    }
}
