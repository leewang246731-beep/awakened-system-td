using EraCore.Contracts.Actions;
using EraCore.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062] C-03 动作注册表元数据（19 动作/权限档/计价/解锁时代/危险位）
public class ActionCatalogTests
{
    [Fact]
    public void Catalog_covers_every_kind_exactly_once()
    {
        // legal：19 个封闭动词，每个恰好一行（K-3 冻结清单完整性）
        Assert.Equal(19, ActionCatalog.All.Count);
        Assert.Equal(
            Enum.GetValues<SystemActionKind>().Cast<SystemActionKind>().OrderBy(k => (int)k),
            ActionCatalog.All.Select(d => d.Kind).OrderBy(k => (int)k));
        Assert.Equal(ActionCatalog.All.Count, ActionCatalog.All.Select(d => d.Kind).Distinct().Count());
    }

    [Fact]
    public void Verb_wire_names_match_enum_names_in_order()
    {
        // legal：线名=枚举名（TryResolveName 与 RegisteredKinds 的口径一致性）
        foreach (ActionDescriptor descriptor in ActionCatalog.All)
        {
            Assert.Equal(descriptor.Kind.ToString(), descriptor.Verb);
        }
    }

    [Theory] // legal
    [InlineData("LookAround", SystemActionKind.LookAround)]
    [InlineData("SpawnScout", SystemActionKind.SpawnScout)]
    [InlineData("StopTask", SystemActionKind.StopTask)]
    public void TryResolveName_resolves_known_verbs(string name, SystemActionKind expected)
    {
        Assert.True(ActionCatalog.TryResolveName(name, out SystemActionKind kind));
        Assert.Equal(expected, kind);
    }

    [Theory] // illegal：清单外/大小写漂移/空值一律不解析（封闭动词表，RL6）
    [InlineData("SellEverything")]
    [InlineData("lookaround")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveName_refuses_verbs_outside_the_closed_list(string? name)
    {
        Assert.False(ActionCatalog.TryResolveName(name, out _));
    }

    [Theory] // legal：组内权限档与计价（v3.2 §11.4：侦察 0/建造 2/物流 1/战斗 1/巡逻 1）
    [InlineData("LookAround", ActionPermissionTier.Allow, 0)]
    [InlineData("BuildPlan", ActionPermissionTier.Ask, 2)]
    [InlineData("SellTower", ActionPermissionTier.Ask, 2)]
    [InlineData("RerouteLogistics", ActionPermissionTier.Ask, 1)]
    [InlineData("FocusFire", ActionPermissionTier.Ask, 1)]
    [InlineData("SpawnScout", ActionPermissionTier.Allow, 1)]
    [InlineData("ReportToPlayer", ActionPermissionTier.Allow, 0)]
    public void Descriptor_tier_and_price_follow_the_frozen_tariff(string verb, ActionPermissionTier tier, int points)
    {
        ActionDescriptor descriptor = ActionCatalog.Descriptor(
            ActionCatalog.TryResolveName(verb, out SystemActionKind kind) ? kind : throw new InvalidOperationException());
        Assert.Equal(tier, descriptor.Tier);
        Assert.Equal(points, descriptor.PointCost);
    }

    [Theory] // legal：解锁时代列（附录 G.2；SpawnScout=E2 巡逻级 N9/A2）
    [InlineData("LookAround", 1)]
    [InlineData("ReportToPlayer", 1)]
    [InlineData("SetAlarm", 1)]
    [InlineData("BuildPlan", 2)]
    [InlineData("SpawnScout", 2)]
    [InlineData("TodoBoard", 2)]
    public void Descriptor_unlock_era_follows_appendix_g2(string verb, int expectedEra)
    {
        ActionDescriptor descriptor = ActionCatalog.Descriptor(
            ActionCatalog.TryResolveName(verb, out SystemActionKind kind) ? kind : throw new InvalidOperationException());
        Assert.Equal(expectedEra, descriptor.UnlockEra);
    }

    [Fact]
    public void Only_SellTower_carries_the_explicit_confirmation_danger_bit()
    {
        // legal：危险位唯一性（SellTower confirm_sell 永不进自动允许清单，F.4）
        ActionDescriptor sell = ActionCatalog.All.Single(d => d.Kind == SystemActionKind.SellTower);
        Assert.True(sell.RequiresExplicitConfirmation);
        Assert.DoesNotContain(ActionCatalog.All.Where(d => d.Kind != SystemActionKind.SellTower), d => d.RequiresExplicitConfirmation);
    }

    [Fact]
    public void Descriptor_throws_for_values_outside_the_table()
    {
        // illegal：表外枚举值（防御路径，封闭集下不可达但必须显式拒绝）
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionCatalog.Descriptor((SystemActionKind)99));
    }
}
