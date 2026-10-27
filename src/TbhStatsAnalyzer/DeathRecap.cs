namespace TbhStatsAnalyzer;

public sealed record DeathDamageRow(string Attacker, string Element, string Type, string Source,
    double Damage, double HpLost, long Hits, long Crits, double Share);
public sealed record DeathHitRow(double SecondsBeforeDeath, CombatEvent Hit, bool Fatal)
{
    public string FatalLabel => Fatal ? "Смертельный" : "";
}
public sealed record DeathRecord(ulong Sequence, long TimeMs, double SecondsFromStart, string Hero,
    string Killer, string Cause, CombatEvent? FatalHit, bool Partial, double Damage, double HpLost,
    double Healing, IReadOnlyList<DeathDamageRow> Breakdown, IReadOnlyList<DeathHitRow> Timeline)
{
    public string TimeLabel => TimeSpan.FromSeconds(Math.Max(0, SecondsFromStart)).ToString(@"hh\:mm\:ss\.fff");
    public double? FatalDamage => FatalHit?.Damage;
    public double? HpBeforeFatalHit => FatalHit?.HpBefore;
    public string Completeness => Partial ? "Неполная история" : "Полная история";
}

public sealed class DeathRecap
{
    public const int WindowSeconds = 10;
    public IReadOnlyList<DeathRecord> Records => deaths;
    private readonly List<DeathRecord> deaths = [];
    private readonly Dictionary<string, List<CombatEvent>> history = new();
    private long startedAt, lastCaptureErrors;
    private readonly List<long> gaps = [];

    public void Reset(long timeMs, long captureErrors)
    {
        startedAt = timeMs; lastCaptureErrors = captureErrors;
        history.Clear(); deaths.Clear(); gaps.Clear();
    }
    public void NoteGaps(Snapshot snapshot)
    {
        if (snapshot.Dropped > 0 || snapshot.CaptureErrors > lastCaptureErrors) gaps.Add(snapshot.TimeMs);
        lastCaptureErrors = snapshot.CaptureErrors;
    }
    public void Observe(CombatEvent e)
    {
        if (!e.TargetHero) return;
        if (!history.TryGetValue(e.TargetId, out var hits)) history[e.TargetId] = hits = [];
        if (!e.Death)
        {
            if (e.Damage > 0 || e.Healing > 0) hits.Add(e);
            return;
        }
        // Sequence boundary excludes instant-resurrection callbacks emitted before the death hook returns.
        ulong boundary = e.DeathBoundarySequence;
        var before = hits.Where(h => h.Sequence <= boundary && h.TimeMs >= e.TimeMs - WindowSeconds * 1000 && h.TimeMs <= e.TimeMs).ToList();
        var fatal = before.FirstOrDefault(h => h.Sequence == e.FatalHitSequence && h.Damage > 0);
        double damage = before.Sum(h => h.Damage);
        var breakdown = before.Where(h => h.Damage > 0)
            .GroupBy(h => new { h.AttackerId, h.Attacker, h.Element, h.DamageType, h.Source })
            .Select(g => new DeathDamageRow(g.Key.Attacker, Labels.Element(g.Key.Element), Labels.Type(g.Key.DamageType),
                Labels.Source(g.Key.Source), g.Sum(h => h.Damage), g.Sum(h => h.HpLost), g.LongCount(),
                g.LongCount(h => h.Critical), damage > 0 ? 100 * g.Sum(h => h.Damage) / damage : 0))
            .OrderByDescending(g => g.Damage).ToList();
        string killer = fatal?.Attacker ?? (e.AttackerId != "0" ? e.Attacker : "Не определён");
        string cause = fatal is null ? "Гибель подтверждена игрой; смертельный урон не зарегистрирован" :
            $"{fatal.ElementName} · {fatal.TypeName} · {fatal.SourceName}" + (fatal.Critical ? " · крит" : "");
        bool partial = e.TimeMs - startedAt < WindowSeconds * 1000 || fatal is null ||
            gaps.Any(time => time >= e.TimeMs - WindowSeconds * 1000);
        deaths.Add(new(e.Sequence, e.TimeMs, (e.TimeMs - startedAt) / 1000.0, e.Target, killer, cause, fatal,
            partial, damage, before.Sum(h => h.HpLost), before.Sum(h => h.Healing), breakdown,
            before.OrderBy(h => h.Sequence).Select(h => new DeathHitRow(Math.Max(0, (e.TimeMs - h.TimeMs) / 1000.0), h, h.Sequence == e.FatalHitSequence)).ToList()));
        hits.RemoveAll(h => h.Sequence <= boundary);
    }
    public void Trim(long now)
    {
        // Keep callback events briefly: a death event can arrive after a later-sequenced resurrection heal.
        foreach (var hits in history.Values) hits.RemoveAll(h => h.TimeMs < now - (WindowSeconds + 5) * 1000);
        gaps.RemoveAll(t => t < now - (WindowSeconds + 5) * 1000);
    }
}
