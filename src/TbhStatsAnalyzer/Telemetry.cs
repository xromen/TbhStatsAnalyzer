using System.Globalization;
using System.Text.Json;

namespace TbhStatsAnalyzer;

public sealed class Snapshot
{
    public int Protocol { get; set; }
    public int Pid { get; set; }
    public long TimeMs { get; set; }
    public long SampleTimeMs { get; set; }
    public ulong LastSequence { get; set; }
    public long Dropped { get; set; }
    public long CaptureErrors { get; set; }
    public string Error { get; set; } = "";
    public bool Ready { get; set; }
    public int StageKey { get; set; }
    public int Act { get; set; }
    public int StageNo { get; set; }
    public int StageLevel { get; set; }
    public int StageState { get; set; }
    public int RunState { get; set; }
    public List<UnitSnapshot> Heroes { get; set; } = [];
    public List<UnitSnapshot> Enemies { get; set; } = [];
    public List<CombatEvent> Events { get; set; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

public sealed class UnitSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsHero { get; set; }
    public int HeroKey { get; set; }
    public int State { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double? AttackDamage { get; set; }
    public double? Armor { get; set; }
    public double? AttackSpeed { get; set; }
    public double? CriticalChance { get; set; }
    public double? CriticalDamage { get; set; }
    public double? MovementSpeed { get; set; }
    public double[] Resistances { get; set; } = [];
    public double[] Stats { get; set; } = [];
    public double Stat(int index) => index > 0 && index <= Stats.Length ? Stats[index - 1] : 0;
}

public sealed class CombatEvent
{
    public ulong Sequence { get; set; }
    public long TimeMs { get; set; }
    public string AttackerId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Attacker { get; set; } = "";
    public string Target { get; set; } = "";
    public bool AttackerHero { get; set; }
    public bool TargetHero { get; set; }
    public double Damage { get; set; }
    public double HpLost { get; set; }
    public double Healing { get; set; }
    public double OriginDamage { get; set; }
    public bool Critical { get; set; }
    public int Element { get; set; }
    public int DamageType { get; set; }
    public int Source { get; set; }
    public bool BasicAttack { get; set; }
    public bool Redistributed { get; set; }
    public double HpBefore { get; set; }
    public double HpAfter { get; set; }
    public bool Death { get; set; }
    public ulong FatalHitSequence { get; set; }
    public ulong DeathBoundarySequence { get; set; }
    public string EventKindLabel => Death ? "Смерть" : Healing > 0 ? "Лечение" : "Урон";
    public string ElementName => Labels.Element(Element);
    public string TypeName => Labels.Type(DamageType);
    public string SourceName => Healing > 0 ? "Лечение" : Labels.Source(Source);
    public string CritLabel => Critical ? "Да" : "";
    public string BasicLabel => BasicAttack ? "Базовая" : "Навык / эффект";
    public string RedirectLabel => Redistributed ? "Да" : "";
    public double Overkill => Math.Max(0, Damage - HpLost);
}

public static class Labels
{
    public static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static string N(double value) => value.ToString("N1", Russian);
    public static string Element(int value) => value switch
    {
        0 => "Физический", 1 => "Огонь", 2 => "Холод", 3 => "Молния", 4 => "Хаос",
        5 => "Все стихии", 6 => "Без стихии", _ => "Не определена"
    };
    public static string Type(int value) => value switch
    {
        0 => "Без типа", 1 => "Ближний бой", 2 => "Снаряд", 3 => "Область",
        4 => "Призыв", 5 => "Периодический", 6 => "Ловушка", _ => $"Тип {value}"
    };
    public static string Source(int value) => value switch
    {
        0 => "Прямой", 1 => "Статусный эффект", 2 => "Отражение",
        3 => "Особый", 4 => "Дополнительный", _ => "Источник без DamageInfo"
    };
    // StatType values were obtained with the MCP enum_values tool, not guessed.
    public static readonly string[] StatNames =
    [
        "Нет", "Урон атаки", "Скорость атаки", "Шанс крита", "Критический урон", "Максимум HP", "Защита",
        "Скорость передвижения", "Размер области", "Сокращение числа базовых атак", "Сокращение перезарядки", "Дальность навыков",
        "Сопротивление огню", "Сопротивление холоду", "Сопротивление молнии", "Сопротивление хаосу", "Уклонение", "Блок",
        "Максимум уклонения", "Максимум блока", "Мультиудар", "Похищение здоровья", "Количество снарядов", "Регенерация HP/с",
        "Физический урон, %", "Урон огнём, %", "Урон холодом, %", "Урон молнией, %", "Урон хаосом, %",
        "Макс. сопротивление огню", "Макс. сопротивление холоду", "Макс. сопротивление молнии", "Макс. сопротивление хаосу",
        "HP за попадание", "Снижение всего урона", "Снижение физического урона", "Снижение урона огнём", "Снижение урона холодом",
        "Снижение урона молнией", "Снижение урона хаосом", "Поглощение урона", "Добавленный урон", "Добавленный физический урон",
        "Добавленный урон огнём", "Добавленный урон холодом", "Добавленный урон молнией", "Добавленный урон хаосом",
        "Увеличение опыта", "Дополнительный опыт", "Скорость сотворения", "Усиление лечения навыков", "Длительность навыков",
        "Все стихийные сопротивления", "Урон снарядов", "Урон ближнего боя", "Урон по области", "Урон призывов", "Скорость снарядов",
        "HP за убийство", "Уровни всех навыков", "Стихийный блок", "Стихийное уклонение", "Макс. стихийный блок",
        "Макс. стихийное уклонение", "Макс. всех стихийных сопротивлений", "Шанс крита по полной HP", "Скорость атак и сотворения",
        "Урон по боссам", "Урон по полной HP", "Урон по низкой HP", "Снижение сопротивления огню", "Скорость в начале волны",
        "Снижение сопротивления холоду", "Сокращение контроля", "Золото", "Снижение урона при попадании", "Снижение урона при низкой HP",
        "Снижение сопротивления молнии", "Сокращение перезарядки воскрешения", "Урон после воскрешения",
        "Снижение сопротивления хаосу", "Снижение урона атаки"
    ];
}

public sealed record StatRow(string Name, double Value);
public sealed record ActorRow(string Id, string Name, string Health, double HpPercent, double Attack, double Armor,
    double Fire, double Cold, double Lightning, double Chaos, double AttackSpeed, double CritChance,
    double Damage, double Dps, double AverageDps, double Taken, double TakenDps, double Healing, UnitSnapshot Unit);
public sealed record BreakdownRow(string Actor, string Direction, string Element, string Type, string Source,
    double Damage, double HpLost, double Overkill, double Dps, double AverageDps, long Hits, double CritPercent, double Share);

public sealed class CombatMeter
{
    public ulong Cursor { get; private set; }
    public long StartedAt { get; private set; }
    public long LostEvents { get; private set; }
    public long HitCount { get; private set; }
    public double TotalDamage { get; private set; }
    public double TotalTaken { get; private set; }
    public double TotalHpLost { get; private set; }
    public double Healing { get; private set; }
    public double Dps { get; private set; }
    public double IncomingDps { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double AverageDps => ElapsedSeconds > 0 ? TotalDamage / ElapsedSeconds : 0;
    public List<ActorRow> Heroes { get; private set; } = [];
    public List<ActorRow> Enemies { get; private set; } = [];
    public List<BreakdownRow> Breakdown { get; private set; } = [];
    public IReadOnlyCollection<CombatEvent> Recent => recent;
    public DeathRecap Deaths { get; } = new();
    private readonly Queue<CombatEvent> recent = new();
    private readonly Queue<CombatEvent> window = new();
    private readonly Dictionary<string, Totals> actors = new();
    private readonly Dictionary<GroupKey, Totals> groups = new();
    private bool initialized;
    private int stageKey;
    private sealed class Totals
    {
        public double Damage, Taken, HpLost, Healing;
        public long Hits, Crits;
    }
    private sealed record GroupKey(string Id, string Actor, string Direction, int Element, int Type, int Source);
    public void Reset(Snapshot snapshot)
    {
        Cursor = snapshot.LastSequence; StartedAt = snapshot.TimeMs; stageKey = snapshot.StageKey;
        TotalDamage = TotalTaken = TotalHpLost = Healing = Dps = IncomingDps = ElapsedSeconds = 0;
        HitCount = LostEvents = 0;
        actors.Clear(); groups.Clear(); window.Clear(); recent.Clear(); initialized = true;
        Deaths.Reset(snapshot.TimeMs, snapshot.CaptureErrors);
        BuildRows(snapshot);
    }
    public bool Update(Snapshot snapshot)
    {
        if (!snapshot.Ready) return false;
        bool reset = !initialized || (stageKey != 0 && snapshot.StageKey != stageKey);
        if (reset) Reset(snapshot);
        Deaths.NoteGaps(snapshot);
        foreach (var e in snapshot.Events.OrderBy(e => e.Sequence))
        {
            if (e.Sequence <= Cursor) continue;
            Cursor = e.Sequence;
            if (!Valid(e)) continue;
            Deaths.Observe(e);
            window.Enqueue(e); recent.Enqueue(e);
            while (recent.Count > 1000) recent.Dequeue();
            if (e.Damage > 0) HitCount++;
            if (e.AttackerHero) TotalDamage += e.Damage;
            if (e.TargetHero) { TotalTaken += e.Damage; TotalHpLost += e.HpLost; Healing += e.Healing; }
            Actor(e.AttackerId).Damage += e.Damage;
            var target = Actor(e.TargetId); target.Taken += e.Damage; target.Healing += e.Healing;
            if (e.Damage <= 0) continue;
            AddGroup(new(e.AttackerId, e.Attacker, e.AttackerHero ? "Нанесено героями" : "Нанесено врагами", e.Element, e.DamageType, e.Source), e);
            if (e.TargetHero) AddGroup(new(e.TargetId, e.Target, "Получено героями", e.Element, e.DamageType, e.Source), e);
        }
        Cursor = Math.Max(Cursor, snapshot.LastSequence);
        if (!reset) LostEvents += snapshot.Dropped;
        ElapsedSeconds = Math.Max(0, (snapshot.TimeMs - StartedAt) / 1000.0);
        Deaths.Trim(snapshot.TimeMs);
        while (window.TryPeek(out var e) && e.TimeMs <= snapshot.TimeMs - 1000) window.Dequeue();
        Dps = window.Where(e => e.AttackerHero).Sum(e => e.Damage);
        IncomingDps = window.Where(e => e.TargetHero).Sum(e => e.Damage);
        BuildRows(snapshot);
        return reset;
    }
    private static bool Valid(CombatEvent e) => double.IsFinite(e.Damage) && e.Damage >= 0 &&
        double.IsFinite(e.HpLost) && e.HpLost >= 0 && e.HpLost <= e.Damage + 0.01 &&
        double.IsFinite(e.Healing) && e.Healing >= 0;
    private Totals Actor(string id)
    {
        if (!actors.TryGetValue(id, out var t)) actors[id] = t = new();
        return t;
    }
    private void AddGroup(GroupKey key, CombatEvent e)
    {
        if (!groups.TryGetValue(key, out var t)) groups[key] = t = new();
        t.Damage += e.Damage; t.HpLost += e.HpLost; t.Hits++; if (e.Critical) t.Crits++;
    }
    private void BuildRows(Snapshot s)
    {
        ActorRow Row(UnitSnapshot u)
        {
            var t = Actor(u.Id);
            return new(u.Id, u.Name, $"{Labels.N(u.Hp)} / {Labels.N(u.MaxHp)}", u.MaxHp > 0 ? Math.Clamp(100 * u.Hp / u.MaxHp, 0, 100) : 0,
                u.AttackDamage ?? u.Stat(1), u.Armor ?? u.Stat(6),
                u.Resistances.Length == 4 ? u.Resistances[0] : u.Stat(12),
                u.Resistances.Length == 4 ? u.Resistances[1] : u.Stat(13),
                u.Resistances.Length == 4 ? u.Resistances[2] : u.Stat(14),
                u.Resistances.Length == 4 ? u.Resistances[3] : u.Stat(15),
                u.AttackSpeed ?? u.Stat(2), (u.CriticalChance ?? u.Stat(3)) * 100,
                t.Damage, window.Where(e => e.AttackerId == u.Id).Sum(e => e.Damage),
                ElapsedSeconds > 0 ? t.Damage / ElapsedSeconds : 0, t.Taken,
                window.Where(e => e.TargetId == u.Id).Sum(e => e.Damage), t.Healing, u);
        }
        Heroes = s.Heroes.Select(Row).ToList(); Enemies = s.Enemies.Select(Row).ToList();
        Breakdown = groups.Select(g =>
        {
            var k = g.Key; var t = g.Value;
            double now = window.Where(e => (k.Direction == "Получено героями" ? e.TargetId : e.AttackerId) == k.Id &&
                e.Element == k.Element && e.DamageType == k.Type && e.Source == k.Source).Sum(e => e.Damage);
            double total = groups.Where(pair => pair.Key.Direction == k.Direction).Sum(pair => pair.Value.Damage);
            return new BreakdownRow(k.Actor, k.Direction, Labels.Element(k.Element), Labels.Type(k.Type), Labels.Source(k.Source),
                t.Damage, t.HpLost, Math.Max(0, t.Damage - t.HpLost), now,
                ElapsedSeconds > 0 ? t.Damage / ElapsedSeconds : 0, t.Hits, t.Hits > 0 ? 100.0 * t.Crits / t.Hits : 0,
                total > 0 ? 100 * t.Damage / total : 0);
        }).OrderByDescending(x => x.Damage).ToList();
    }
}
