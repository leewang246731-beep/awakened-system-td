using EraCore.Scrolls;
using Xunit;

namespace EraCore.Tests.Scrolls;

// [C-18][F-038] T-071 卷轴数据模型：卡型纪律与条件参数访问器的三行行为
// （构造合法/边界默认/非法访问）。数值语义锚点（冷却/持续=ticks@30Hz）在
// ScrollDataLoaderTests 与集成测试中对照，此处只测模型自身不变式。
public class ScrollDefinitionTests
{
    private static ScrollAction SampleAction(ScrollEffectKind? effect = ScrollEffectKind.ZoneDamageBonus) => new(
        Verb: null, VerbMode: null, VerbTarget: null,
        Effect: effect, EffectMagnitude: 0.5, EffectDurationTicks: 300,
        UntilWaveEnd: false, Scope: ScrollTargetScope.TriggerZone, TowerClass: null);

    private static ScrollCondition SampleCondition() => new(
        ScrollConditionKind.ZoneEnemyCountAtLeast,
        new[]
        {
            new ScrollConditionArg("zone", 0, "barbican"),
            new ScrollConditionArg("count", 5, null),
        });

    // legal
    [Fact]
    public void Definition_roundtrips_its_data()
    {
        var scroll = new ScrollDefinition(
            "era2:scroll-barbican-retort", "SC-002", ScrollCardKind.ConditionCard,
            "瓮城反击", "Barbican Retort", new[] { "瓮城" },
            Priority: 60, CooldownTicks: 1350, UnlockEra: 2,
            Condition: SampleCondition(), Action: SampleAction(),
            PerTowerPerWaveLimit: 0, Acquisition: new ScrollAcquisition("boss_drop", "era2:enemy-boss"));

        Assert.Equal("SC-002", scroll.SpId);
        Assert.Equal(60, scroll.Priority);
        Assert.Equal(1350, scroll.CooldownTicks);
        Assert.Equal(2, scroll.UnlockEra);
        Assert.Equal("boss_drop", scroll.Acquisition.Kind);
        Assert.Equal("era2:enemy-boss", scroll.Acquisition.Ref);
        Assert.Equal("barbican", scroll.Condition!.Text("zone"));
        Assert.Equal(5, scroll.Condition.Number("count"), 3);
        Assert.Equal(ScrollEffectKind.ZoneDamageBonus, scroll.Action.Effect);
    }

    // boundary
    [Fact]
    public void Zero_cooldown_and_zero_limit_are_legal_finite_values()
    {
        var scroll = new ScrollDefinition(
            "era2:scroll-rearguard", "SC-004", ScrollCardKind.ConditionCard,
            "断后", "Rearguard", Array.Empty<string>(),
            Priority: 70, CooldownTicks: 0, UnlockEra: 2,
            Condition: SampleCondition(), Action: SampleAction(ScrollEffectKind.TowerHealPercent),
            PerTowerPerWaveLimit: 1, Acquisition: new ScrollAcquisition("research", "era2:tech-codex-iii"));

        Assert.Equal(0, scroll.CooldownTicks);
        Assert.Equal(1, scroll.PerTowerPerWaveLimit);
    }

    // illegal
    [Fact]
    public void Condition_accessors_throw_on_missing_or_mistyped_args()
    {
        ScrollCondition condition = SampleCondition();
        _ = Assert.Throws<InvalidOperationException>(() => condition.Number("zone")); // text arg via Number
        _ = Assert.Throws<InvalidOperationException>(() => condition.Text("count")); // numeric arg via Text
        _ = Assert.Throws<InvalidOperationException>(() => condition.Text("no-such-arg"));
    }

    // illegal
    [Fact]
    public void Null_definition_is_rejected_by_the_content_wrapper()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new ScrollContentItem(null!));
    }

    // legal
    [Fact]
    public void Content_wrapper_exposes_the_registry_identity()
    {
        var scroll = new ScrollDefinition(
            "era2:scroll-x", "SC-X", ScrollCardKind.ActionCard,
            "X", "X", new[] { "甲卷" },
            Priority: 1, CooldownTicks: 0, UnlockEra: 2,
            Condition: null, Action: SampleAction(),
            PerTowerPerWaveLimit: 0, Acquisition: new ScrollAcquisition("research", "r"));

        ScrollContentItem item = new(scroll);
        Assert.Equal("era2:scroll-x", item.Id);
        Assert.Equal(EraCore.Contracts.Content.ContentType.Scroll, item.ContentType);
        Assert.Equal(new[] { "甲卷" }, item.Aliases);
        Assert.Same(scroll, item.Definition);
    }
}
