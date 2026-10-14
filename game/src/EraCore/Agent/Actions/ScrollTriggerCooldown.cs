namespace EraCore.Agent.Actions;

/// <summary>
/// TriggerScroll 3 s cooldown state (T-070, DEBT-3; v3.2 §11.4/F.1
/// arbitration: TriggerScroll=1 点+3 s 冷却，不占常驻激活上限). One window
/// per registry instance: a SUCCESSFUL trigger starts the cooldown, any
/// TriggerScroll arriving inside the window is refused with the remaining
/// tick budget, and failed attempts never consume the window (the K-3
/// "failed actions never bill" discipline applied to cooldown). The state
/// lives here in the execution layer — the C-03 registry owns an instance
/// and consults it around dispatch, so the rule holds with or without a
/// live scroll handler wired (handler delivery lands with the scroll
/// runtime). Time base = simulation ticks (K-1 IClock 30 Hz domain).
/// <para>
/// Layering note (parallel lanes T-068/T-070): this is the VERB-level
/// cooldown of §11.4/F.1 ("TriggerScroll=…+3 s 冷却" — one window per verb
/// call regardless of scroll id). The G.2 wording "卷轴 3 s 内部冷却"
/// (per-scroll internal cooldown) is enforced separately and later in the
/// handler-tier ledger (<c>AgentActionState.TryTriggerScroll</c>,
/// per scroll_id). Both layers stand: the verb gate is the stricter
/// outer door, the per-scroll ledger is the runtime bookkeeping the
/// scroll runtime (C-18) consumes.
/// </para>
/// </summary>
public sealed class ScrollTriggerCooldown
{
    /// <summary>Cooldown window: 90 ticks = 3 s at the frozen 30 Hz tick (K-1).</summary>
    public const int CooldownTicks = 90; // ci-allow: R7 T-070 卷轴 3s 冷却=90 tick@30Hz（SP-2 #12/F.1 冻结域常量）

    private long? _lastAcceptedTick;

    /// <summary>
    /// Whether a trigger at <paramref name="tick"/> would land inside the
    /// active cooldown window. Boundary semantics: triggering at tick T
    /// blocks ticks T..T+89 and frees T+90 (the full 3 s has elapsed).
    /// A tick at or before the last accepted tick (replay/reorder anomaly)
    /// reads as off-cooldown — the window only counts forward.
    /// </summary>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="remainingTicks">Ticks left until the window closes (≥1 when on cooldown).</param>
    public bool OnCooldown(long tick, out int remainingTicks)
    {
        remainingTicks = 0;
        if (_lastAcceptedTick is not { } last)
        {
            return false;
        }

        long delta = tick - last;
        if (delta < 0 || delta >= CooldownTicks)
        {
            return false;
        }

        remainingTicks = (int)(CooldownTicks - delta);
        return true;
    }

    /// <summary>
    /// Records a successful trigger at <paramref name="tick"/> and opens
    /// the window. Called only after the dispatcher returned Ok — failed
    /// triggers never consume the cooldown.
    /// </summary>
    /// <param name="tick">Tick of the accepted trigger.</param>
    public void Accept(long tick)
    {
        _lastAcceptedTick = tick;
    }
}
