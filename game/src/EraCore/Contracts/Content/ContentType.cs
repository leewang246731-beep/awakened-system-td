// T2: pure contract — K-2 content registry, T-020, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Content;

/// <summary>
/// Closed taxonomy of content kinds (K-2). Every registered content item
/// carries exactly one kind; modded and first-party content pass through the
/// same registry pipeline (CODE_STANDARDS CS-2.5). The 15-kind list first
/// appears in docs/engineering/12-contracts-frozen.md (K-2 registration),
/// expanded from the compressed one-line wording of recovered v2.1 §16
/// item 4 — "ContentType 枚举含'系统（军师）动作参数空间'" (v3.2 appendix
/// G.1 item 4; docs/engineering/10-recovered-v21-architecture.md §一 item 4).
/// </summary>
public enum ContentType
{
    /// <summary>Resource definitions: ids, display names, economy roles.</summary>
    Resource,

    /// <summary>Buildings: towers, production, storage, walls (six tower classes per T-007).</summary>
    Building,

    /// <summary>Projectile kinds fired by towers.</summary>
    Projectile,

    /// <summary>Status effects on units: slow, burn, shield-break, boss auras.</summary>
    StatusEffect,

    /// <summary>Enemy definitions: HP/armor/speed/specials and wave-roster membership.</summary>
    Enemy,

    /// <summary>Wave groups: spawn tables referenced by level schedules.</summary>
    WaveGroup,

    /// <summary>Era definitions: E1–E5 plus the finale (era index 6; six eras total, no double counting).</summary>
    Era,

    /// <summary>Technology tree nodes (triple gate + instalments, design §7).</summary>
    Tech,

    /// <summary>Wonders: the six fixed wonders (appendix F.3).</summary>
    Wonder,

    /// <summary>Hero definitions: the optional player avatar (design §6).</summary>
    Hero,

    /// <summary>Guardian scrolls: resident action cards (appendix F.1).</summary>
    Scroll,

    /// <summary>Loot tables: chest rarity distribution 60/30/10% within the box (N8).</summary>
    DropTable,

    /// <summary>Handcrafted level definitions: campaign nodes and layouts.</summary>
    Level,

    /// <summary>Localization tables: zh/en UI strings and grammar-shared aliases.</summary>
    Localization,

    /// <summary>
    /// Advisor action parameter space (e.g. the <c>tower_id</c> enum of
    /// BuildPlan). Mods may extend the parameter space; the action verb list
    /// itself is NEVER mod-extensible (v2.1 §16 item 4; K-3).
    /// </summary>
    ActionParamSpace,
}
