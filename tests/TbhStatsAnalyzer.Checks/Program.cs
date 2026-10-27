using System.Text.Json;
using System.IO;
using TbhStatsAnalyzer;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static CombatEvent Hit(ulong seq, long time, double damage = 100, int element = 1) => new()
{
    Sequence = seq, TimeMs = time, AttackerId = "hero", TargetId = "enemy", Attacker = "Рыцарь", Target = "Гоблин",
    AttackerHero = true, Damage = damage, HpLost = Math.Min(damage, 70), Element = element, DamageType = 1, Critical = true
};
static Snapshot Frame(long time, ulong seq, params CombatEvent[] events) => new()
{
    Protocol = 1, Pid = 123, Ready = true, TimeMs = time, SampleTimeMs = time, LastSequence = seq, StageKey = 10,
    Heroes = [new() { Id = "hero", Name = "Рыцарь", Hp = 100, MaxHp = 200, IsHero = true, Stats = [42, 0.75, 5, 150, 200, 10] }],
    Enemies = [new() { Id = "enemy", Name = "Гоблин", Hp = 70, MaxHp = 70 }], Events = events.ToList()
};

if (args.Contains("--ui-deaths")) { UiChecks.Run(); return; }
if (args.Contains("--ui-filters")) { UiChecks.Run(filters: true); return; }

if (args.Contains("--live") || args.Contains("--live-deaths"))
{
    int samples = args.Contains("--live-deaths") ? 120 : 24;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(samples / 2 + 45));
    var processes = GameConnection.FindGames();
    Check(processes.Length == 1, $"Expected one TaskBarHero, found {processes.Length}");
    int pid = processes[0].Id;
    foreach (var p in processes) p.Dispose();
    await using var connection = new GameConnection();
    await connection.ConnectAsync(pid, timeout.Token);
    var meter = new CombatMeter();
    var first = await connection.ReadAsync(0, timeout.Token);
    Check(first.Ready && first.Heroes.Count > 0, "No live heroes");
    Check(first.Heroes.All(h => h.Stats.Length == 81), "Missing game stats");
    meter.Reset(first);
    var received = new List<CombatEvent>();
    Snapshot last = first;
    for (int i = 0; i < samples; i++)
    {
        await Task.Delay(500, timeout.Token);
        last = await connection.ReadAsync(meter.Cursor, timeout.Token);
        Check(string.IsNullOrEmpty(last.Error), last.Error);
        Check(last.CaptureErrors == 0, "Capture errors");
        Check(last.TimeMs - last.SampleTimeMs < 3000, "Stale snapshot");
        received.AddRange(last.Events); meter.Update(last);
        if (i % 20 == 19) Console.WriteLine($"Live sample: {i + 1}/{samples}, events={received.Count}, deaths={received.Count(e => e.Death)}");
    }
    Check(received.Count > 0, "No events during the 12-second combat sample");
    Check(received.Any(e => e.Damage > 0 && e.AttackerHero), "No outgoing hero damage");
    Check(received.Where(e => e.Damage > 0 && e.Source >= 0).All(e => e.Element is >= 0 and <= 6), "DamageInfo context was not captured");
    Check(received.Any(e => e.TargetHero && e.Damage > 0), "No incoming hero damage in the combat sample");
    Check(last.Heroes.All(h => h.AttackDamage is > 0 && h.Resistances.Length == 4), "Effective combat stats were not read");
    Check(last.Heroes.All(h => h.Hp >= 0 && h.Hp <= h.MaxHp + 0.1), "Invalid live HP");
    Check(meter.LostEvents == 0, "Events lost");
    foreach (var liveDeath in meter.Deaths.Records)
        Check(liveDeath.FatalHit is null || liveDeath.Timeline.Any(row => row.Fatal && row.Hit.Sequence == liveDeath.FatalHit.Sequence), "Live death/fatal-hit linkage");
    Directory.CreateDirectory("artifacts/verification");
    File.WriteAllText("artifacts/verification/live-snapshot.json", JsonSerializer.Serialize(last, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText("artifacts/verification/live-events.json", JsonSerializer.Serialize(received, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText("artifacts/verification/live-meter.csv", CsvExport.Create(meter, last));
    File.WriteAllText("artifacts/verification/live-deaths.json", JsonSerializer.Serialize(meter.Deaths.Records, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"LIVE PASS: PID={pid}, heroes={last.Heroes.Count}, enemies={last.Enemies.Count}, events={received.Count}, deaths={received.Count(e => e.Death)}, damage={meter.TotalDamage:F1}, incoming={meter.TotalTaken:F1}, errors={last.CaptureErrors}");
    return;
}

var m = new CombatMeter();
m.Update(Frame(1000, 10, Hit(10, 900)));
Check(m.TotalDamage == 0 && m.Cursor == 10, "First sample must establish a baseline");
m.Update(Frame(1500, 12, Hit(11, 1200), Hit(12, 1400, 50, 2)));
Check(m.TotalDamage == 150 && m.Dps == 150 && m.AverageDps == 300, "DPS formulas");
Check(m.Heroes.Single().Damage == 150 && m.Heroes.Single().Attack == 42, "Per-hero totals and stats");
var units = Frame(1550, 12);
units.Heroes[0].Stats = new double[81]; units.Heroes[0].Stats[2] = 0.033375; units.Heroes[0].Stats[13] = 53;
m.Update(units);
Check(Math.Abs(m.Heroes[0].CritChance - 3.3375) < 0.00001 && m.Heroes[0].Lightning == 53,
    "Critical chance is a ratio; resistances are already percentage points");
units.Heroes[0].AttackDamage = 8296.578; units.Heroes[0].Resistances = [0, 0, 60, 64];
m.Update(units);
Check(m.Heroes[0].Attack == 8296.578 && m.Heroes[0].Lightning == 60, "Use effective combat stats instead of the base cache");
Check(m.Breakdown.Count == 2 && m.Breakdown.Sum(r => r.Overkill) == 30, "Element split and overkill");
m.Update(Frame(1600, 12, Hit(11, 1200), Hit(12, 1400, 50, 2)));
Check(m.TotalDamage == 150 && m.HitCount == 2, "Deduplicate replayed events");
m.Update(Frame(2200, 12));
Check(m.Dps == 50, "Rolling second must expire its exact left boundary");
m.Update(Frame(2500, 12));
Check(m.Dps == 0 && m.TotalDamage == 150, "Idle DPS must become zero without erasing totals");
var incoming = Hit(13, 2600, 30); incoming.AttackerId = "enemy"; incoming.TargetId = "hero";
incoming.AttackerHero = false; incoming.TargetHero = true; incoming.Attacker = "Гоблин"; incoming.Target = "Рыцарь";
m.Update(Frame(2700, 13, incoming));
Check(m.TotalTaken == 30 && m.IncomingDps == 30 && m.TotalDamage == 150, "Incoming damage must not inflate outgoing damage");
var heal = Hit(14, 2800, 0); heal.Healing = 20; heal.TargetId = "hero"; heal.TargetHero = true;
m.Update(Frame(2900, 14, heal));
Check(m.Healing == 20 && m.HitCount == 3, "Healing is not a damage hit");
var invalid = Hit(15, 2950); invalid.Damage = double.NaN;
m.Update(Frame(3000, 15, invalid));
Check(m.TotalDamage == 150, "Reject nonfinite telemetry");
var lost = Frame(3100, 15); lost.Dropped = 4; m.Update(lost);
Check(m.LostEvents == 4, "Transport loss must be visible");
var csv = CsvExport.Create(m, Frame(3100, 15));
Check(csv.Contains("Рыцарь") && csv.Contains("Избыток") && csv.Contains("Разбивка урона"), "CSV must include Russian results");
var dangerousName = Frame(3200, 15); dangerousName.Heroes[0].Name = "=1+1;\"test\"";
m.Update(dangerousName);
Check(CsvExport.Create(m, dangerousName).Contains("\"'=1+1;\"\"test\"\"\""), "CSV must escape formulas, delimiters and quotes");
var nextMap = Frame(4000, 16, Hit(16, 3900)); nextMap.StageKey = 20;
Check(m.Update(nextMap) && m.TotalDamage == 0 && m.Cursor == 16, "Map change resets old combat history");
m.Reset(Frame(5000, 16)); Check(m.StartedAt == 5000 && m.LostEvents == 0, "Manual reset");
var fixture = JsonSerializer.Deserialize<Snapshot>("{\"protocol\":1,\"ready\":true,\"heroes\":[{\"id\":\"h\",\"name\":\"Рыцарь\",\"stats\":[42]}],\"events\":[]}", Snapshot.JsonOptions);
Check(fixture?.Heroes.Single().Stat(1) == 42, "Native camelCase JSON contract");
Check(Labels.StatNames.Length == 82, "StatType labels must cover all 81 values");

static CombatEvent Received(ulong seq, long time, double damage, int element = 0, string target = "hero") => new()
{
    Sequence = seq, TimeMs = time, AttackerId = "enemy", Attacker = "Гоблин", TargetId = target, Target = "Рыцарь",
    TargetHero = true, Damage = damage, HpLost = damage, HpBefore = damage, HpAfter = 0, Element = element, DamageType = 1
};
static CombatEvent Died(ulong seq, long time, ulong fatal, ulong boundary) => new()
{
    Sequence = seq, TimeMs = time, TargetId = "hero", Target = "Рыцарь", TargetHero = true,
    AttackerId = "enemy", Attacker = "Гоблин", Death = true, FatalHitSequence = fatal, DeathBoundarySequence = boundary
};
var deathMeter = new CombatMeter(); deathMeter.Reset(Frame(1000, 0));
var oldHit = Received(1, 5000, 200);
var physical = Received(2, 10000, 40);
var otherHero = Received(3, 15000, 500, target: "other-hero");
var healing = Received(4, 16000, 0); healing.Healing = 10;
var fatalHit = Received(5, 19000, 60, 1); fatalHit.Critical = true;
var resurrection = Received(6, 19001, 0); resurrection.Healing = 20;
var deathEvent = Died(7, 19000, 5, 5);
deathMeter.Update(Frame(20000, 7, oldHit, physical, otherHero, healing, fatalHit, resurrection, deathEvent));
var death = deathMeter.Deaths.Records.Single();
Check(death.Damage == 100 && death.HpLost == 100 && death.Healing == 10, "Death recap must include only this hero's last 10 seconds");
Check(death.FatalHit?.Sequence == 5 && death.FatalDamage == 60 && death.HpBeforeFatalHit == 60 && death.Cause.Contains("Огонь") && death.Cause.Contains("крит"), "Exact fatal hit, cause and pre-hit HP");
Check(death.Breakdown.Count == 2 && death.Breakdown.Sum(r => r.Share) == 100 && !death.Partial, "Death damage breakdown and completeness");
Check(death.Timeline.Count == 3 && death.Timeline.Single(r => r.Fatal).Hit.Sequence == 5 && death.Timeline.All(r => r.SecondsBeforeDeath >= 0), "Death timeline and fatal marker");
Check(deathMeter.TotalTaken == 800 && deathMeter.HitCount == 4, "Death marker must not double-count damage");
var fatalAgain = Received(8, 21000, 10);
var secondDeath = Died(9, 21000, 8, 8);
var secondFrame = Frame(21500, 9, fatalAgain, secondDeath); deathMeter.Update(secondFrame); deathMeter.Update(secondFrame);
Check(deathMeter.Deaths.Records.Count == 2 && deathMeter.Deaths.Records[1].Damage == 10 && deathMeter.Deaths.Records[1].Healing == 20,
    "Resurrection heal belongs to next life; duplicate death must be ignored");
var unknownDeath = Died(10, 23000, 999, 9); deathMeter.Update(Frame(23500, 10, unknownDeath));
Check(deathMeter.Deaths.Records[^1].Partial && deathMeter.Deaths.Records[^1].FatalHit is null,
    "Never guess a missing fatal hit from an earlier attack");
string deathsCsv = CsvExport.Create(deathMeter, Frame(24000, 10));
Check(deathsCsv.Contains("Смерти — последние 10 секунд") && deathsCsv.Contains("Разбивка урона перед смертью") && deathsCsv.Contains("Огонь"), "Export complete death recaps");
var shortMeter = new CombatMeter(); shortMeter.Reset(Frame(1000, 0));
shortMeter.Update(Frame(2000, 2, Received(1, 1900, 100), Died(2, 1900, 1, 1)));
Check(shortMeter.Deaths.Records.Single().Partial, "A death shortly after connection has incomplete history");
var gapMeter = new CombatMeter(); gapMeter.Reset(Frame(0, 0));
var gapFrame = Frame(20000, 2, Received(1, 19900, 100), Died(2, 19900, 1, 1)); gapFrame.Dropped = 3;
gapMeter.Update(gapFrame); Check(gapMeter.Deaths.Records.Single().Partial, "Mark recaps with lost events as incomplete");
var protectedMeter = new CombatMeter(); protectedMeter.Reset(Frame(0, 0));
var protectedFrame = Frame(12000, 1, Received(1, 11900, 100)); protectedFrame.Heroes[0].Hp = 0;
protectedMeter.Update(protectedFrame);
Check(protectedMeter.Deaths.Records.Count == 0, "Zero HP or lethal damage alone must not register a death-prevention effect as a death");
var afterTenSeconds = new CombatMeter(); afterTenSeconds.Reset(Frame(0, 0));
afterTenSeconds.Update(Frame(20000, 3, Received(1, 9999, 10), Received(2, 20000, 50), Died(3, 20000, 2, 2)));
Check(afterTenSeconds.Deaths.Records.Single().Damage == 50, "Exclude damage older than ten seconds");
deathMeter.Reset(Frame(25000, 10)); Check(deathMeter.Deaths.Records.Count == 0, "Reset death history with the measurement");
var busyMeter = new CombatMeter(); busyMeter.Reset(Frame(0, 0));
var spam = new List<CombatEvent> { Received(1, 10000, 40) };
for (ulong i = 2; i <= 1202; ++i) spam.Add(Hit(i, 10500, 1));
spam.Add(Received(1203, 11000, 60)); spam.Add(Died(1204, 11000, 1203, 1203));
busyMeter.Update(Frame(11500, 1204, spam.ToArray()));
Check(busyMeter.Recent.Count == 1000 && busyMeter.Deaths.Records.Single().Damage == 100,
    "Death history must survive eviction from the global 1000-event journal");
Console.WriteLine("PASS: combat meter, CSV, protocol, effective stats; deaths: causes, exact fatal hit, time window, healing, resurrection ordering, duplicate suppression, missing data, protected death and reset.");
