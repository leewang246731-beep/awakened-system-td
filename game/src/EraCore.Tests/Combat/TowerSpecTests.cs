using System.Text.Json;
using EraCore.Combat;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-013] E1 tower definitions deserialize from the registry table
// (game/content/era1/towers.json) into TowerSpec — the single number source
// of C-09 (RL7). Anchors cited per row come from the same SP-1b/v3.2 rows
// that Era1ContentTests pins at the JSON level; these tests pin the loader.
public class TowerSpecTests
{
    [Fact] // legal — the full E1 table loads with all four towers
    public void Era1_towers_load_four_specs_with_classes()
    {
        // [F-013] four E1 towers, classes per SP-1b L36~L39
        var towers = CombatContent.Towers;
        Assert.Equal(4, towers.Count);

        Assert.Equal(TowerClass.Pierce, CombatContent.Tower("era1:tower-wood-arrow").Class);
        Assert.Equal(TowerClass.Physical, CombatContent.Tower("era1:tower-rockfall").Class);
        Assert.Equal(TowerClass.Slow, CombatContent.Tower("era1:tower-spike-trap").Class);
        Assert.Equal(TowerClass.Support, CombatContent.Tower("era1:tower-campfire").Class);
    }

    [Fact] // legal — weapon-carrying tower fields survive the round trip
    public void Wood_arrow_fields_match_registry_values()
    {
        // [F-013] wood arrow row (towers.json): 30 cost / 10 dps / d 10 /
        // 30 ticks reload / 5.0 range / 120 hp / 1 shot per firing
        var spec = CombatContent.Tower("era1:tower-wood-arrow");
        Assert.Equal(30, spec.CostTotal);
        Assert.Equal(10.0, spec.Dps, 6);      // tolerance 1e-6: pure registry read-back
        Assert.Equal(10.0, spec.DamagePerShot, 6);
        Assert.Equal(30, spec.ReloadTicks);
        Assert.Equal(5.0, spec.RangeCells, 6);
        Assert.Equal(120, spec.Hp);
        Assert.Equal("era1:ammo-arrow", spec.AmmoItem);
        Assert.Equal(1, spec.AmmoPerShot);
        Assert.Equal(1, spec.Tier);
        Assert.Equal(2, spec.MaxTier);
    }

    [Fact] // legal — burst wind-up and control/aura traits land typed
    public void Trait_blocks_load_typed_values()
    {
        // [F-013] trait anchors: 落石台 wind-up 45 ticks (SP-1b L37);
        // trap root 60 / slow 30% / 120 ticks / one-shot (SP-1b L38);
        // campfire aura radius 3 + regen 1.0/s (SP-1b L39)
        var rockfall = CombatContent.Tower("era1:tower-rockfall");
        Assert.Equal(45, rockfall.Traits!.BurstWindupTicks!.Value);
        Assert.Null(rockfall.Traits.OneShot);

        var trap = CombatContent.Tower("era1:tower-spike-trap");
        Assert.Null(trap.Traits!.BurstWindupTicks);
        Assert.True(trap.Traits.OneShot);
        Assert.Equal(60, trap.Traits.RootTicks!.Value);
        Assert.Equal(30, trap.Traits.SlowPercent!.Value);
        Assert.Equal(120, trap.Traits.SlowTicks!.Value);

        var campfire = CombatContent.Tower("era1:tower-campfire");
        Assert.Equal(3, campfire.Traits!.AuraRadiusCells!.Value);
        Assert.Equal(1.0, campfire.Traits.AuraRegenHpPerS!.Value, 6);
        Assert.Contains("ally_regen", campfire.Traits.AuraEffects!);
    }

    [Fact] // boundary — control/aura towers carry no weapon cycle
    public void Null_reload_and_zero_ammo_survive_deserialization()
    {
        // [F-013] spike trap / campfire: reload_ticks null, ammo_item null, ammo_per_shot 0
        foreach (var id in new[] { "era1:tower-spike-trap", "era1:tower-campfire" })
        {
            var spec = CombatContent.Tower(id);
            Assert.Null(spec.ReloadTicks);
            Assert.Null(spec.AmmoItem);
            Assert.Equal(0, spec.AmmoPerShot);
            Assert.Equal(0.0, spec.Dps, 6);
        }
    }

    [Fact] // boundary — growth block fields ride along as declared
    public void Growth_block_loads_declared_multipliers()
    {
        // [F-027] tier_growth 2.0 / 3.0 / 1.25 on every E1 row (v3.2 §6)
        foreach (var spec in CombatContent.Towers)
        {
            Assert.NotNull(spec.TierGrowth);
            Assert.Equal(2.0, spec.TierGrowth!.DpsMultiplierPerTier, 9);
            Assert.Equal(3.0, spec.TierGrowth.CostMultiplierPerTier, 9);
            Assert.Equal(1.25, spec.TierGrowth.RangeMultiplierPerTier, 9);
        }
    }

    [Fact] // illegal — damaged payloads are rejected by the loader
    public void Malformed_json_is_rejected()
    {
        // [F-013] strict parse: broken JSON throws JsonException (no silent empty table)
        Assert.Throws<JsonException>(() => TowerSpec.LoadCollection("{\"items\": [ { \"id\": }"));
    }

    [Fact] // illegal — an unknown class string fails deserialization
    public void Unknown_class_string_is_rejected()
    {
        // [F-026] closed enum discipline at the loader level
        Assert.Throws<JsonException>(() => TowerSpec.LoadCollection(
            "{\"items\":[{\"id\":\"x:t\",\"class\":\"arcane\",\"tier\":1,\"max_tier\":2,\"cost_total\":1,\"dps\":1,\"damage_per_shot\":1,\"reload_ticks\":30,\"range_cells\":1,\"hp\":1,\"ammo_per_shot\":1}]}"));
    }
}
