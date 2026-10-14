using System.Text.Json;
using EraCore.Contracts.Foundation;

namespace EraCore.Units;

/// <summary>
/// Acknowledgement payload of one accepted HeroOrder (C-03 registry
/// outcome payload; the pipeline reports it to the player).
/// </summary>
/// <param name="Order">The accepted verb: move / skill / retreat.</param>
/// <param name="Stance">Hero stance after the order.</param>
/// <param name="SkillStatus">Skill settlement when the order was 'skill'; null otherwise.</param>
public sealed record HeroOrderAck(string Order, HeroStance Stance, HeroSkillStatus? SkillStatus);

/// <summary>
/// Typed outcome of one HeroOrder invocation (the registry layer wraps it
/// into its own <c>ActionResult</c>).
/// </summary>
/// <param name="Ok">Whether the order executed.</param>
/// <param name="Ack">Payload on success.</param>
/// <param name="Reason">Structured refusal reason on failure.</param>
public sealed record HeroOrderResult(bool Ok, HeroOrderAck? Ack, string? Reason);

/// <summary>
/// The HeroOrder handler (C-10 full, T-066; F-039; SP-2/G.2 row
/// <c>HeroOrder: order: move|skill|retreat</c> — the closed wire schema
/// carries the order verb alone, so every parameter resolves engine-side):
/// move aims the hero at the engagement cell handed in at construction,
/// retreat aims it home, skill settles the hero's single active. Lives in
/// the unit system (it owns hero state) and plugs into the C-03 action
/// registry as the fourth real handler — the three order values move from
/// the not-implemented tier to real behavior here.
/// <para>
/// Optionality (F-039 acceptance: 关闭英雄不影响通关): the hero provider
/// may return null — every order then refuses explicitly with
/// <c>hero-not-deployed</c>, never crashes, and a level that never wires a
/// hero plays identically without one.
/// </para>
/// </summary>
public sealed class HeroOrderAction
{
    private const string Verb = "HeroOrder";

    private readonly Func<Hero?> _hero;
    private readonly Func<CellCoord> _advanceCell;
    private readonly Func<IReadOnlyList<Unit>> _enemies;
    private readonly int _cellSizePx;

    /// <summary>
    /// Tick the skill verb settles against — the sim wires its IClock
    /// here; the deterministic default is 0.
    /// </summary>
    public Func<long> TickSource { get; init; } = static () => 0;

    /// <param name="hero">Live hero supplier; null = no hero deployed (optional feature).</param>
    /// <param name="advanceCell">
    /// Engagement cell the move verb aims at (engine-side default
    /// destination — the K-3 schema carries no coordinates; the sim wires
    /// the wave-entry cell here).
    /// </param>
    /// <param name="enemies">Live enemy roster supplier (skill targeting).</param>
    /// <param name="cellSizePx">Cell size in px (K-1: 32).</param>
    /// <exception cref="ArgumentNullException">Any delegate is null.</exception>
    /// <exception cref="ArgumentException">cellSizePx not positive.</exception>
    public HeroOrderAction(Func<Hero?> hero, Func<CellCoord> advanceCell, Func<IReadOnlyList<Unit>> enemies, int cellSizePx)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(advanceCell);
        ArgumentNullException.ThrowIfNull(enemies);
        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"CellSizePx must be positive (got {cellSizePx}).", nameof(cellSizePx));
        }

        _hero = hero;
        _advanceCell = advanceCell;
        _enemies = enemies;
        _cellSizePx = cellSizePx;
    }

    /// <summary>
    /// Executes one HeroOrder payload. The registry validated the closed
    /// schema beforehand; this entry re-checks the verb defensively so a
    /// direct call cannot smuggle a bad order through.
    /// </summary>
    /// <exception cref="ArgumentException">Payload is not a parseable HeroOrder object.</exception>
    public HeroOrderResult Invoke(string actionJson)
    {
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            throw new ArgumentException("HeroOrder payload must not be null or blank.", nameof(actionJson));
        }

        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("action", out JsonElement verbElement)
            || verbElement.GetString() != Verb)
        {
            throw new ArgumentException("Payload is not a HeroOrder action.", nameof(actionJson));
        }

        if (!root.TryGetProperty("order", out JsonElement orderElement)
            || orderElement.ValueKind != JsonValueKind.String)
        {
            return Refuse("order-missing:HeroOrder needs an 'order' of move|skill|retreat (K-3 schema)");
        }

        string order = orderElement.GetString()!;
        Hero? hero = _hero();
        if (hero is null)
        {
            return Refuse(
                "hero-not-deployed:the hero feature is optional — no hero is on the field, the order is refused explicitly (F-039)");
        }

        switch (order)
        {
            case "move":
                hero.OrderMove(_advanceCell(), _cellSizePx);
                return new HeroOrderResult(true, new HeroOrderAck(order, hero.Stance, null), null);

            case "retreat":
                hero.OrderRetreat();
                return new HeroOrderResult(true, new HeroOrderAck(order, hero.Stance, null), null);

            case "skill":
                HeroSkillStatus status = hero.TryUseSkill(ClockTick(), _enemies(), _cellSizePx);
                if (status != HeroSkillStatus.Fired)
                {
                    return Refuse($"skill-not-fired:{status}:the cooldown/target gate refused the skill; nothing was consumed");
                }

                return new HeroOrderResult(true, new HeroOrderAck(order, hero.Stance, status), null);

            default:
                return Refuse($"order-unknown:'{order}' is outside move|skill|retreat (K-3 schema)");
        }
    }

    private long ClockTick() => TickSource();

    private static HeroOrderResult Refuse(string reason) => new(false, null, reason);
}
