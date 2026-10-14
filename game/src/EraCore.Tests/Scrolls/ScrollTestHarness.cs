using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Scrolls;

namespace EraCore.Tests.Scrolls;

// [C-18] T-071 测试基建：脚本化世界视图/记录型动作执行器/回复落地 sink/内存
// K-2 注册表——四件均为跨层端口的测试 Fake（IActionExecutor=K-5 契约端口，
// 同 Agent 测试对 IActionWorldView 的 Fake 先例；被测系统 ScrollEngine
// 本体从不被 mock，CS-3.4）。

/// <summary>Mutable scripted world: tests set facts per tick deterministically.</summary>
internal sealed class ScriptedScrollWorld : IScrollWorldView
{
    public long Tick { get; set; }

    public int CurrentWave { get; set; }

    public List<ScrollEnemyFact> EnemyFacts { get; } = new();

    public List<ScrollTowerFact> TowerFacts { get; } = new();

    public List<ScrollZoneFact> ZoneFacts { get; } = new();

    public Dictionary<string, double> ResourceAmounts { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<ScrollEnemyFact> Enemies => EnemyFacts;

    public IReadOnlyList<ScrollTowerFact> Towers => TowerFacts;

    public IReadOnlyList<ScrollZoneFact> Zones => ZoneFacts.OrderBy(z => z.ZoneId, StringComparer.Ordinal).ToList();

    public double ResourceAmount(ResourceKey resource) =>
        ResourceAmounts.TryGetValue(resource.Value, out double amount) ? amount : 0;

    public ScrollEnemyFact AddEnemy(long id, string? zoneId, int hp, int maxHp, int x = 0, int y = 0)
    {
        var fact = new ScrollEnemyFact(id, zoneId, hp, maxHp, x, y);
        EnemyFacts.Add(fact);
        return fact;
    }

    public ScrollTowerFact AddTower(long id, string? zoneId, TowerClass towerClass = TowerClass.Pierce, int hp = 100, int maxHp = 200, int x = 0, int y = 0)
    {
        var fact = new ScrollTowerFact(id, "era2:tower-arrow", towerClass, zoneId, hp, maxHp, x, y);
        TowerFacts.Add(fact);
        return fact;
    }

    public static ScrollZoneFact Zone(string zoneId, params (int X, int Y)[] cells) =>
        new(zoneId, cells.Select(c => new CellCoord(c.X, c.Y)).ToList());
}

/// <summary>Recording K-5 door: captures every (kind, json) the scroll lane dispatches.</summary>
internal sealed class RecordingExecutor : IActionExecutor
{
    public List<(SystemActionKind Kind, string Json)> Calls { get; } = new();

    public List<ActionResult> Results { get; } = new();

    public Func<SystemActionKind, string, ActionResult>? OnExecute { get; set; }

    public ActionResult Execute(SystemActionKind kind, string actionJson)
    {
        Calls.Add((kind, actionJson));
        ActionResult result = OnExecute?.Invoke(kind, actionJson) ?? new ActionResult(ActionResultStatus.Ok);
        Results.Add(result);
        return result;
    }
}

/// <summary>Recording instant-effect sink (heals).</summary>
internal sealed class RecordingEffectSink : IScrollEffectSink
{
    public List<(long TowerId, int Amount)> Heals { get; } = new();

    public void HealTower(long towerId, int amount) => Heals.Add((towerId, amount));
}

/// <summary>In-memory K-2 registry (IContentRegistry contract fake; sorted-by-id listing per contract).</summary>
internal sealed class TestContentRegistry : IContentRegistry
{
    private readonly Dictionary<(ContentType Type, string Id), IContentItem> _items = new();

    public void Register(IContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_items.TryAdd((item.ContentType, item.Id), item))
        {
            throw new InvalidOperationException($"Duplicate content id '{item.Id}' under {item.ContentType}.");
        }
    }

    public IContentItem Get(ContentType type, string id) =>
        _items.TryGetValue((type, id), out IContentItem? item)
            ? item
            : throw new KeyNotFoundException($"No {type} '{id}'.");

    public IContentItem GetByName(ContentType type, string name) =>
        FindByName(type, name) ?? throw new KeyNotFoundException($"No {type} named '{name}'.");

    public IContentItem? FindByName(ContentType type, string name) =>
        _items.Values
            .Where(i => i.ContentType == type)
            .FirstOrDefault(i => i.Id == name
                || i.Aliases.Contains(name)
                || i.Id.EndsWith(":" + name, StringComparison.Ordinal));

    public IReadOnlyList<IContentItem> List(ContentType type) =>
        _items.Values.Where(i => i.ContentType == type).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
}

/// <summary>Scroll definition builders shared by the rule matrix / determinism suites.</summary>
internal static class ScrollTestDefs
{
    private static int _nextSerial;

    /// <summary>Condition card with a zone-sourced condition (enemy in "z1") and a timed zone effect.</summary>
    public static ScrollDefinition AlwaysTrueZoneScroll(
        string id,
        int priority,
        int cooldownTicks = 0,
        double magnitude = 0.5,
        int durationTicks = 300,
        ScrollEffectKind effect = ScrollEffectKind.ZoneDamageBonus)
    {
        var serial = _nextSerial++;
        return new ScrollDefinition(
            id,
            "SC-T" + serial,
            ScrollCardKind.ConditionCard,
            "测试卷轴" + serial,
            "Test Scroll " + serial,
            new[] { "测试卷轴" + serial },
            priority,
            cooldownTicks,
            UnlockEra: 2,
            Condition: new ScrollCondition(
                ScrollConditionKind.EnemyInZone,
                new[] { new ScrollConditionArg("zone", 0, "z1") }),
            Action: new ScrollAction(
                Verb: null,
                VerbMode: null,
                VerbTarget: null,
                Effect: effect,
                EffectMagnitude: magnitude,
                EffectDurationTicks: durationTicks,
                UntilWaveEnd: false,
                Scope: ScrollTargetScope.TriggerZone,
                TowerClass: null),
            PerTowerPerWaveLimit: 0,
            Acquisition: new ScrollAcquisition("research", "era2:tech-codex-i"));
    }

    /// <summary>Action card with a tower-class attack-speed effect (SC-003 shape).</summary>
    public static ScrollDefinition AttackSpeedActionCard(
        string id,
        int priority,
        int cooldownTicks,
        TowerClass towerClass = TowerClass.Pierce,
        int durationTicks = 450)
    {
        var serial = _nextSerial++;
        return new ScrollDefinition(
            id,
            "SC-T" + serial,
            ScrollCardKind.ActionCard,
            "测试动作卡" + serial,
            "Test Action Card " + serial,
            new[] { "测试动作卡" + serial },
            priority,
            cooldownTicks,
            UnlockEra: 2,
            Condition: null,
            Action: new ScrollAction(
                Verb: null,
                VerbMode: null,
                VerbTarget: null,
                Effect: ScrollEffectKind.TowerClassAttackSpeed,
                EffectMagnitude: 0.25,
                EffectDurationTicks: durationTicks,
                UntilWaveEnd: false,
                Scope: ScrollTargetScope.TowerClass,
                TowerClass: towerClass),
            PerTowerPerWaveLimit: 0,
            Acquisition: new ScrollAcquisition("research", "era2:tech-codex-ii"));
    }

    /// <summary>Condition card with a FocusFire verb step (SC-001 shape).</summary>
    public static ScrollDefinition FocusFireScroll(string id, int priority, int cooldownTicks = 0)
    {
        var serial = _nextSerial++;
        return new ScrollDefinition(
            id,
            "SC-T" + serial,
            ScrollCardKind.ConditionCard,
            "测试警弦" + serial,
            "Test Alarm " + serial,
            new[] { "测试警弦" + serial },
            priority,
            cooldownTicks,
            UnlockEra: 2,
            Condition: new ScrollCondition(
                ScrollConditionKind.EnemyInZone,
                new[] { new ScrollConditionArg("zone", 0, "z1") }),
            Action: new ScrollAction(
                Verb: nameof(SystemActionKind.FocusFire),
                VerbMode: null,
                VerbTarget: ScrollVerbTarget.StrongestInTriggerZone,
                Effect: null,
                EffectMagnitude: 0,
                EffectDurationTicks: 0,
                UntilWaveEnd: false,
                Scope: ScrollTargetScope.TriggerZone,
                TowerClass: null),
            PerTowerPerWaveLimit: 0,
            Acquisition: new ScrollAcquisition("research", "era2:tech-codex-i"));
    }

    /// <summary>Condition card healing the trigger tower (SC-004 shape).</summary>
    public static ScrollDefinition HealScroll(string id, int priority, int perTowerPerWaveLimit = 1)
    {
        var serial = _nextSerial++;
        return new ScrollDefinition(
            id,
            "SC-T" + serial,
            ScrollCardKind.ConditionCard,
            "测试断后" + serial,
            "Test Rearguard " + serial,
            new[] { "测试断后" + serial },
            priority,
            CooldownTicks: 0,
            UnlockEra: 2,
            Condition: new ScrollCondition(
                ScrollConditionKind.TowerHpBelowPercent,
                new[] { new ScrollConditionArg("percent", 0.5, null) }),
            Action: new ScrollAction(
                Verb: null,
                VerbMode: null,
                VerbTarget: null,
                Effect: ScrollEffectKind.TowerHealPercent,
                EffectMagnitude: 0.2,
                EffectDurationTicks: 0,
                UntilWaveEnd: false,
                Scope: ScrollTargetScope.TriggerTower,
                TowerClass: null),
            PerTowerPerWaveLimit: perTowerPerWaveLimit,
            Acquisition: new ScrollAcquisition("research", "era2:tech-codex-iii"));
    }
}
