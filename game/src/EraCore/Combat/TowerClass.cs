using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Combat;

/// <summary>
/// String converter binding <see cref="TowerClass"/> to the lowercase JSON
/// spellings of <c>tower.class</c> (camel-case policy: "physical"…; reading
/// stays case-insensitive, integers are rejected — the class set is closed).
/// AOT-safe generic base (07 D-05).
/// </summary>
public sealed class TowerClassJsonConverter : JsonStringEnumConverter<TowerClass>
{
    public TowerClassJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// The six tower classes frozen by T-007 (SP-1a; design v3.2 §6, 07 correction
/// #24): 物理/穿刺/溅射/能量/减速/支援. JSON <c>tower.class</c> uses the
/// lowercase spellings <c>physical/pierce/splash/energy/slow/support</c> —
/// bound to this enum by the string converter below (AOT-safe generic form,
/// 07 D-05). Class duties and DPS validation bands follow SP-1a:
/// splash DPS band = single-target band ×0.8 (AOE −20% applies to DPS only,
/// 07 D-15), slow band ×0.2, support stays out of the DPS band entirely.
/// </summary>
[JsonConverter(typeof(TowerClassJsonConverter))]
public enum TowerClass
{
    /// <summary>钝击/碾压: heavy single hits, low rate — stable into armor (SP-1a 物理).</summary>
    Physical = 0,

    /// <summary>箭矢/速射: low per-shot damage, high rate, cheapest (SP-1a 穿刺).</summary>
    Pierce = 1,

    /// <summary>面伤害: AOE towers, edge floor 40% (SP-1a 溅射; v3.2 §6).</summary>
    Splash = 2,

    /// <summary>高能直射: E4+ tech line, armor-piercing flag interrupts shield regen (SP-1a 能量).</summary>
    Energy = 3,

    /// <summary>控制场: control effects first, damage is secondary (SP-1a 减速).</summary>
    Slow = 4,

    /// <summary>零/象征伤害的增益与阻挡: auras, vision, blocking (SP-1a 支援).</summary>
    Support = 5,
}
