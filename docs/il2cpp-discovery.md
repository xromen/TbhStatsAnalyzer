# Найденные точки чтения TaskbarHero

Исследование выполнено 27 октября 2026 через установленный IL2CPP.Mcp автора [takiy_veshi](https://t.me/takiy_veshi). Процесс: TaskBarHero x64, Unity 6000.0.72.12005885, сборка Assembly-CSharp.dll. Все приведённые смещения — наблюдения этой версии; приложение получает их заново из метаданных.

| Данные | Класс / поле / метод | Найдено |
| --- | --- | --- |
| Текущая команда | TaskbarHero.StageManager.HeroList | массив из 3 Hero, поле +104 |
| Текущая карта | StageManager.bgvz → xo.StageCache.bizr | StageInfoData: StageKey, Act, StageNo, StageLevel |
| Состояние боя | StageManager.stageState, _stageRunState | состояние волны и запуска карты |
| Живые враги | TaskbarHero.Manager.MonsterSpawnManager.MonsterList / SummonedMonsterList | List<Unit> |
| Название юнита | виртуальный Unit.gyk() | локализованное имя: Рыцарь, Жрец, Следопыт |
| Расчётный кэш | виртуальный Unit.gzg() → xz.<bjfr>k__BackingField | объект bcp |
| Характеристики | bcp.mpj(StatType) | текущие значения из bjyc; 81 числовой параметр |
| Контроллер здоровья | Unit.UnitHealthController | pp, +176; производные pl у героя, pn у монстра |
| HP | pp.bgaz / hif() | +64, текущая величина |
| Максимум HP | pp.bgbd / hig() | +80 |
| Контекст попадания | Unit.hal(DamageInfo, bool) | общий путь обработки атак; Hero.hal вызывает базовый метод |
| Применение изменения HP | pp.hby(float, Unit) | положительная величина лечит, отрицательная наносит урон; значение после расчёта защиты |
| Изменение HP героя | pl.hby(float, Unit) | отдельная реализация без вызова pp.hby; учитывается её защитный флаг Hero.bfxg |
| Боевые характеристики | Unit.hdw / hdx / gyo / hea / heb / hdy | урон атаки, защита, скорость атаки, шанс крита, множитель крита, скорость движения |
| Боевые сопротивления | Unit.hew(EDamageAttribute) | сумма сопротивления стихии и общего стихийного бонуса с ограничением сверху из bfzy; для хаоса общий стихийный бонус не добавляется |
| Подтверждение смерти героя | Hero.hbd(Unit) → bool | вызван из pl.hby при HP ≤ 0; true на пути настоящей смерти, false при предотвращении смерти/защитном сценарии |

`DamageInfo` — структура размером 48 байт без заголовка boxed-объекта. MCP показывает поля boxed-значения со смещением +16. При перехвате используется смещение поля минус 16, проверяемое при подключении.

| Поле DamageInfo | Смещение в структуре |
| --- | --- |
| Attacker | 0 |
| OriginDamage | 8 |
| IsCritical / CanCritical | 12 / 13 |
| DamageAttribute / DamageType | 16 / 20 |
| HitEffects | 32 |
| IsRedistributedDamage / IsBaseAttackDamage | 40 / 41 |
| DamageSource | 44 |

`EDamageAttribute`: Physical=0, Fire=1, Cold=2, Lightning=3, Chaos=4, AllElement=5, None=6.

`EDamageType`: None=0, Melee=1, Projectile=2, AOE=3, Summon=4, DOT=5, Trap=6.

`EDamageSource`: Direct=0, StatusEffect=1, Reflect=2, SpecialDamage=3, ExtraDamage=4.

Ключевые StatType: AttackDamage=1, AttackSpeed=2, CriticalChance=3, CriticalDamage=4, MaxHp=5, Armor=6, FireResistance=12, ColdResistance=13, LightningResistance=14, ChaosResistance=15. Полное сопоставление записано в `Labels.StatNames`.

При проверке `CriticalChance` у Рыцаря был 0.033375002 (3.3375%), LightningResistance — 53 (53 процентных пункта). Поэтому эти поля нельзя нормализовать одним множителем.

Команды MCP, использованные для проверки: list_games, agent_inject, list_images, dump_metadata, find_instances, array_read, read_field, invoke только проверенных getter-методов, enum_values, method_params, api_call/api_call_batch и read_memory. Машинный код путей изменения HP дополнительно просмотрен локально для подтверждения знака delta, ABI структуры и отсутствия двойного учёта базового и переопределённого методов.

Сырой дамп метаданных и временные инструменты остаются в игнорируемой папке research; для сборки они не нужны. Абсолютные адреса из исследовательской сессии в приложение не включены.

Для вкладки смертей обработчик Hero.hbd был дополнительно проверен через MCP: указатель виртуального метода из пути pl.hby сопоставлен с именем через `il2cpp_method_get_name`, проверены параметр Unit и возвращаемый Boolean. Просмотрены обе ветви возвращаемого значения. Наблюдатель не вызывает этот метод самостоятельно: он передаёт исходный вызов игре и регистрирует только успешный результат. Порядковый номер исходного изменения HP сохраняется в thread-local контексте до вызова обработчика. Отдельная граница последовательности до обработчика исключает попадание мгновенного воскрешения в предсмертное лечение.
