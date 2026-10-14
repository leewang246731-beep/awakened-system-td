using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-039] 英雄内容装载（T-066）：game/content/era2/heroes.json 两行
// （SP-6 E1+E2 同文件、逐项 era 字段）——锚点=11-spec-tables.md SP-6
// L181~L182+L176（60 s 重生定案值）；秒值按文件 tick_rate_hz=30 换算 ticks。
public class HeroSpecTests
{
    private static HeroSpecCatalog LoadCatalog() => UnitsContent.LoadHeroCatalog();

    [Fact] // legal — 生产装载器吃下两行：id/era/秒→tick 换算
    public void Production_catalog_parses_both_hero_rows_with_tick_conversions()
    {
        HeroSpecCatalog catalog = LoadCatalog();
        Assert.Equal(2, catalog.Count);
        Assert.Equal(30, catalog.TickRateHz);

        HeroSpec e1 = catalog.Get("era1:hero-firstfire");
        Assert.Equal("h1_firstfire", e1.SpId);
        Assert.Equal("era1:era-ancient", e1.Era);
        Assert.Equal(1800, e1.RespawnTicks); // 60 s×30（SP-6 L176 定案）

        HeroSpec e2 = catalog.Get("era2:hero-spearwarden");
        Assert.Equal("h2_spearwarden", e2.SpId);
        Assert.Equal("era2:era-classical", e2.Era);
        Assert.Equal(1800, e2.RespawnTicks);
    }

    [Theory] // legal — SP-6 两行数值锚点（HP/速度/主动/被动）
    [InlineData("era1:hero-firstfire", 700, 1.07, "warcry", "campfire_affinity")]     // L181
    [InlineData("era2:hero-spearwarden", 1400, 1.17, "armor_thrust", "veteran")]     // L182
    public void Heroes_match_SP6_rows(string id, int hp, double speed, string activeKind, string passiveKind)
    {
        HeroSpec spec = LoadCatalog().Get(id);

        Assert.Equal(hp, spec.Hp);
        Assert.Equal(0, spec.Armor); // SP-6 英雄无甲
        Assert.Equal(speed, spec.SpeedPxPerTick, 3);
        Assert.Equal(activeKind, spec.Active.Kind);
        Assert.Equal(passiveKind, spec.Passive.Kind);
    }

    [Fact] // legal — E1 初火守望者主动：半径 3 格减速 30%×5 s、冷却 45 s（SP-6 L181）
    public void Firstfire_warcry_carries_SP6_numbers()
    {
        HeroActiveSkill skill = LoadCatalog().Get("era1:hero-firstfire").Active;
        Assert.Equal(3.0, skill.RadiusCells);
        Assert.Equal(30, skill.SlowPercent);
        Assert.Equal(150, skill.DurationTicks);   // 5 s
        Assert.Equal(1350, skill.CooldownTicks);  // 45 s
        Assert.Equal(0, skill.Damage);
        Assert.Equal(0, skill.ArmorReduction);
    }

    [Fact] // legal — E2 执戈者主动：200 伤+−4 甲×10 s、冷却 40 s、reach 2 格（SP-6 L182）
    public void Spearwarden_thrust_carries_SP6_numbers()
    {
        HeroActiveSkill skill = LoadCatalog().Get("era2:hero-spearwarden").Active;
        Assert.Equal(200, skill.Damage);
        Assert.Equal(4, skill.ArmorReduction);
        Assert.Equal(300, skill.DurationTicks);   // 10 s
        Assert.Equal(1200, skill.CooldownTicks);  // 40 s
        Assert.Equal(2.0, skill.RangeCells);      // [NEW-原型校准]（SP-6 未给距离）
        Assert.Equal(0, skill.SlowPercent);
    }

    [Fact] // legal — 被动：E1 篝火亲和 +50%；E2 身经百战 5 波/+10%/上限 30%（SP-6）
    public void Passives_match_SP6_rows()
    {
        HeroPassive e1 = LoadCatalog().Get("era1:hero-firstfire").Passive;
        Assert.Equal(50, e1.BonusPercent);

        HeroPassive e2 = LoadCatalog().Get("era2:hero-spearwarden").Passive;
        Assert.Equal(5, e2.WavesPerStep);
        Assert.Equal(10, e2.AttackBonusPercentPerStep);
        Assert.Equal(30, e2.CapPercent);
    }

    [Fact] // illegal — 空白文本确实被拒绝
    public void Parse_rejects_blank_json() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse("  "));

    [Fact] // illegal — 错误 content_type 确实被拒绝
    public void Parse_rejects_wrong_content_type() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse(HeroFileFakes.WrongContentType));

    [Fact] // illegal — 缺 active 技能对象确实被拒绝
    public void Parse_rejects_missing_active_skill() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse(HeroFileFakes.MissingActive));

    [Fact] // illegal — 重复 id 确实被拒绝
    public void Parse_rejects_duplicate_ids() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse(HeroFileFakes.DuplicateId));

    [Fact] // illegal — 秒值不能整换算为 tick 的字段确实被拒绝（0.037 s×30=1.11 tick）
    public void Parse_rejects_non_integral_second_conversions() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse(HeroFileFakes.FractionalSeconds));

    [Fact] // illegal — 非正 HP 确实被拒绝
    public void Parse_rejects_non_positive_hp() =>
        Assert.Throws<ArgumentException>(() => HeroSpecCatalog.Parse(HeroFileFakes.ZeroHp));
}

/// <summary>装载器负例夹具（构造在方法体外——测试方法体内禁字面 JSON 花括号）。</summary>
internal static class HeroFileFakes
{
    public static string WrongContentType =>
        "{\"content_type\":\"Enemy\",\"tick_rate_hz\":30,\"items\":[]}";

    public static string MissingActive =>
        "{\"content_type\":\"Hero\",\"tick_rate_hz\":30,\"items\":[{"
        + "\"id\":\"x:hero\",\"content_type\":\"Hero\",\"sp_id\":\"h\",\"era\":\"x:era\","
        + "\"name_zh\":\"n\",\"hp\":100,\"speed_px_per_tick\":1.0,\"respawn_seconds\":60,"
        + "\"passive\":{\"kind\":\"veteran\",\"name_zh\":\"p\"}}]}";

    public static string DuplicateId =>
        "{\"content_type\":\"Hero\",\"tick_rate_hz\":30,\"items\":["
        + HeroRow("x:hero") + "," + HeroRow("x:hero") + "]}";

    public static string FractionalSeconds =>
        "{\"content_type\":\"Hero\",\"tick_rate_hz\":30,\"items\":[{"
        + "\"id\":\"x:hero\",\"content_type\":\"Hero\",\"sp_id\":\"h\",\"era\":\"x:era\","
        + "\"name_zh\":\"n\",\"hp\":100,\"speed_px_per_tick\":1.0,\"respawn_seconds\":0.037,"
        + "\"active\":{\"kind\":\"warcry\",\"name_zh\":\"a\",\"cooldown_s\":45},"
        + "\"passive\":{\"kind\":\"veteran\",\"name_zh\":\"p\"}}]}";

    public static string ZeroHp =>
        "{\"content_type\":\"Hero\",\"tick_rate_hz\":30,\"items\":[{"
        + "\"id\":\"x:hero\",\"content_type\":\"Hero\",\"sp_id\":\"h\",\"era\":\"x:era\","
        + "\"name_zh\":\"n\",\"hp\":0,\"speed_px_per_tick\":1.0,\"respawn_seconds\":60,"
        + "\"active\":{\"kind\":\"warcry\",\"name_zh\":\"a\",\"cooldown_s\":45},"
        + "\"passive\":{\"kind\":\"veteran\",\"name_zh\":\"p\"}}]}";

    private static string HeroRow(string id) =>
        "{\"id\":\"" + id + "\",\"content_type\":\"Hero\",\"sp_id\":\"h\",\"era\":\"x:era\","
        + "\"name_zh\":\"n\",\"hp\":100,\"speed_px_per_tick\":1.0,\"respawn_seconds\":60,"
        + "\"active\":{\"kind\":\"warcry\",\"name_zh\":\"a\",\"cooldown_s\":45},"
        + "\"passive\":{\"kind\":\"veteran\",\"name_zh\":\"p\"}}";
}
