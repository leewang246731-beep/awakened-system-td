namespace EraCore.Agent.Actions;

/// <summary>
/// Input reviewed by every WaveEnd hook (T-070, F-075; v3.2 §11.4/F.4:
/// WaveEnd 钩子守成检查 ≤3 轮). The chain runs BEFORE a wave-end report
/// ships: a hook may block the pending "守住" (held) verdict and feed a
/// reason back to the agent so it patches the defense first — at most
/// <see cref="WaveEndHookChain.MaxReviewRounds"/> blocking rounds per
/// wave, then the report ships regardless (honest reporting, capped
/// looping).
/// </summary>
/// <param name="Wave">Wave number the report closes (1-based).</param>
/// <param name="ClaimsHeld">True when the pending report claims the wave was held (守住).</param>
/// <param name="ReportJson">The pending report payload (ReportToPlayer action JSON).</param>
/// <param name="WorldView">Read-only world projection; null when unwired.</param>
public sealed record WaveEndContext(
    int Wave,
    bool ClaimsHeld,
    string ReportJson,
    IActionWorldView? WorldView);

/// <summary>Verdict kinds a WaveEnd hook can return.</summary>
public enum WaveEndVerdictKind
{
    /// <summary>Let the report ship.</summary>
    Pass,

    /// <summary>Hold the report back and feed the reason to the agent (<see cref="WaveEndHookVerdict.Feedback"/>).</summary>
    Block,
}

/// <summary>
/// One hook's verdict: Pass, or Block(feedback) — the feedback string is
/// the reason the agent sees (回喂). Built only through the factory
/// methods.
/// </summary>
public sealed record WaveEndHookVerdict(WaveEndVerdictKind Kind, string? Feedback = null)
{
    /// <summary>Let the report ship.</summary>
    public static WaveEndHookVerdict Pass() => new(WaveEndVerdictKind.Pass);

    /// <summary>Hold the report and feed the reason back (≤3 rounds per wave).</summary>
    /// <param name="feedback">Actionable reason the agent should act on before re-reporting.</param>
    public static WaveEndHookVerdict Block(string feedback) => new(WaveEndVerdictKind.Block, feedback);
}

/// <summary>
/// One registered WaveEnd hook (F-075, 注册制): consulted by the
/// <see cref="WaveEndHookChain"/> in registration order.
/// </summary>
public interface IWaveEndHook
{
    /// <summary>Stable hook id used in blocked results and logs.</summary>
    string Id { get; }

    /// <summary>Reviews one pending wave-end report.</summary>
    /// <param name="context">Wave, held claim, pending report and world view.</param>
    WaveEndHookVerdict Review(WaveEndContext context);
}

/// <summary>
/// Chain outcome of the WaveEnd review (T-070): ship / blocked+feedback,
/// plus the round bookkeeping.
/// </summary>
/// <param name="Ship">True when the report may ship (all hooks passed, or the round cap opened the gate).</param>
/// <param name="RoundCapped">True when the ≤3-round cap forced the gate open (the report ships but the cap is visible to the caller).</param>
/// <param name="BlockedBy">Id of the blocking hook (null unless blocked).</param>
/// <param name="Feedback">Reason fed back to the agent (null unless blocked).</param>
/// <param name="Round">Review rounds already consumed for this wave (0-based at pass, 1..3 while blocking).</param>
public sealed record WaveEndReviewResult(
    bool Ship,
    bool RoundCapped,
    string? BlockedBy,
    string? Feedback,
    int Round);

/// <summary>
/// Ordered WaveEnd completeness chain (F-075, T-070): runs the hooks over
/// every pending wave-end report; the first Block holds the report and
/// feeds its reason back (守成检查拦截 ≤3 轮, F.4). Round bookkeeping is
/// per wave: a wave change resets the counter, and once a wave has
/// consumed <see cref="MaxReviewRounds"/> blocking rounds every further
/// review of that wave ships with <see cref="WaveEndReviewResult.RoundCapped"/>
/// = true — the report always gets out, looping never exceeds the cap.
/// </summary>
public sealed class WaveEndHookChain
{
    /// <summary>Maximum blocking rounds per wave (F.4: 守成检查 ≤3 轮).</summary>
    public const int MaxReviewRounds = 3;

    private readonly IReadOnlyList<IWaveEndHook> _hooks;

    private int _wave;
    private int _reviewRounds;

    /// <param name="hooks">Hooks in application order (first registered runs first).</param>
    public WaveEndHookChain(IEnumerable<IWaveEndHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _hooks = hooks.ToArray();
    }

    /// <summary>
    /// Reviews one pending wave-end report: first blocking hook wins;
    /// after <see cref="MaxReviewRounds"/> blocked rounds the report of
    /// the same wave ships with the round-cap flag.
    /// </summary>
    /// <param name="context">Wave, held claim, pending report and world view.</param>
    public WaveEndReviewResult Review(WaveEndContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Wave != _wave)
        {
            _wave = context.Wave;
            _reviewRounds = 0;
        }

        if (_reviewRounds >= MaxReviewRounds)
        {
            return new WaveEndReviewResult(Ship: true, RoundCapped: true, BlockedBy: null, Feedback: null, Round: _reviewRounds);
        }

        foreach (IWaveEndHook hook in _hooks)
        {
            WaveEndHookVerdict verdict = hook.Review(context);
            if (verdict.Kind == WaveEndVerdictKind.Block)
            {
                _reviewRounds++;
                return new WaveEndReviewResult(
                    Ship: false, RoundCapped: false, BlockedBy: hook.Id, Feedback: verdict.Feedback, Round: _reviewRounds);
            }
        }

        return new WaveEndReviewResult(Ship: true, RoundCapped: false, BlockedBy: null, Feedback: null, Round: _reviewRounds);
    }
}
