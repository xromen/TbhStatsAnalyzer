using System.Globalization;
using System.Text;

namespace TbhStatsAnalyzer;
public static class CsvExport
{
    public static string Create(CombatMeter meter, Snapshot? snapshot)
    {
        var b = new StringBuilder();
        void Line(params object?[] cells) => b.AppendLine(string.Join(";", cells.Select(Cell)));
        Line("TaskbarHero — анализ билда", "Акт", snapshot?.Act, "Карта", snapshot?.StageNo);
        Line("Время замера, с", meter.ElapsedSeconds, "Потеряно событий", meter.LostEvents);
        Line("Команда");
        Line("Персонаж", "HP", "Урон атаки", "Защита", "Огонь", "Холод", "Молния", "Хаос", "Нанесено", "DPS за 1 с", "DPS средний", "Получено", "Лечение");
        foreach (var r in meter.Heroes) Line(r.Name, r.Health, r.Attack, r.Armor, r.Fire, r.Cold, r.Lightning, r.Chaos, r.Damage, r.Dps, r.AverageDps, r.Taken, r.Healing);
        Line(); Line("Живые враги"); Line("Враг", "HP", "Урон атаки", "Защита", "Огонь", "Холод", "Молния", "Хаос", "Нанёс", "Получил");
        foreach (var r in meter.Enemies) Line(r.Name, r.Health, r.Attack, r.Armor, r.Fire, r.Cold, r.Lightning, r.Chaos, r.Damage, r.Taken);
        Line(); Line("Разбивка урона");
        Line("Персонаж", "Направление", "Стихия", "Тип", "Источник", "Урон", "Потеря HP", "Избыток", "DPS за 1 с", "DPS средний", "Удары", "Криты %", "Доля %");
        foreach (var r in meter.Breakdown) Line(r.Actor, r.Direction, r.Element, r.Type, r.Source, r.Damage, r.HpLost, r.Overkill, r.Dps, r.AverageDps, r.Hits, r.CritPercent, r.Share);
        Line(); Line("Характеристики — значения игры"); Line("Юнит", "Параметр", "Значение");
        foreach (var r in meter.Heroes.Concat(meter.Enemies))
            for (int i = 1; i < Labels.StatNames.Length; i++) Line(r.Name, Labels.StatNames[i], r.Unit.Stat(i));
        Line(); Line("Смерти — последние 10 секунд текущей жизни");
        Line("Номер смерти", "Время замера", "Персонаж", "Кто добил", "Причина", "Последний удар", "HP до последнего удара", "Урон", "Потеря HP", "Лечение", "История");
        foreach (var d in meter.Deaths.Records) Line(d.Sequence, d.TimeLabel, d.Hero, d.Killer, d.Cause, d.FatalDamage, d.HpBeforeFatalHit, d.Damage, d.HpLost, d.Healing, d.Completeness);
        Line(); Line("Разбивка урона перед смертью");
        Line("Номер смерти", "Персонаж", "Атакующий", "Стихия", "Тип", "Источник", "Урон", "Потеря HP", "Попадания", "Криты", "Доля %");
        foreach (var d in meter.Deaths.Records)
            foreach (var r in d.Breakdown) Line(d.Sequence, d.Hero, r.Attacker, r.Element, r.Type, r.Source, r.Damage, r.HpLost, r.Hits, r.Crits, r.Share);
        Line(); Line("Попадания перед смертью");
        Line("Номер смерти", "Персонаж", "За сколько секунд", "Событие", "Атакующий", "Стихия", "Тип", "Источник", "Урон", "Потеря HP", "Лечение", "HP до", "HP после", "Крит", "Смертельный");
        foreach (var d in meter.Deaths.Records)
            foreach (var r in d.Timeline) Line(d.Sequence, d.Hero, r.SecondsBeforeDeath, r.Hit.EventKindLabel, r.Hit.Attacker, r.Hit.ElementName, r.Hit.TypeName, r.Hit.SourceName,
                r.Hit.Damage, r.Hit.HpLost, r.Hit.Healing, r.Hit.HpBefore, r.Hit.HpAfter, r.Hit.Critical, r.Fatal);
        Line(); Line("Последние 1000 событий");
        Line("Номер", "Миллисекунды от начала замера", "Событие", "Атакующий", "Цель", "OriginDamage", "Урон", "Потеря HP", "Лечение", "HP до", "HP после", "Стихия", "Тип", "Источник", "Крит", "Базовая атака", "Перераспределение");
        foreach (var r in meter.Recent) Line(r.Sequence, r.TimeMs - meter.StartedAt, r.EventKindLabel, r.Attacker, r.Target, r.OriginDamage, r.Damage, r.HpLost, r.Healing, r.HpBefore, r.HpAfter, r.ElementName, r.TypeName, r.SourceName, r.Critical, r.BasicAttack, r.Redistributed);
        return b.ToString();
    }
    private static string Cell(object? value)
    {
        string s = value is double n ? n.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) : value?.ToString() ?? "";
        // CSV formula injection can come from game-localized names; keep them as literal text.
        if (value is string && s.TrimStart().StartsWithAny('=', '+', '-', '@')) s = "'" + s;
        return '"' + s.Replace("\"", "\"\"") + '"';
    }
    private static bool StartsWithAny(this string s, params char[] values) => s.Length > 0 && values.Contains(s[0]);
}
