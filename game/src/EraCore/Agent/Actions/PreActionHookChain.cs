using EraCore.Contracts.Actions;

namespace EraCore.Agent.Actions;

/// <summary>
/// Input handed to every PreAction hook (T-070, F-075; v3.2 §11.4/F.4:
/// PreAction 钩子可改写坐标/拦截). Hooks run inside
/// <see cref="ActionRegistry.Invoke"/> AFTER the closed-schema gate and
/// the unlock-era gate and BEFORE dispatch: a hook may rewrite the action
/// JSON (e.g. a coordinate offset) or block the action outright.
/// </summary>
/// <param name="Verb">The closed verb of the pending action.</param>
/// <param name="ActionJson">The current payload (a previous hook's rewrite is what later hooks see).</param>
/// <param name="Tick">Current simulation tick (30 Hz domain; -1 before the first step).</param>
/// <param name="WorldView">Read-only world projection; null when the registry was built without one.</param>
public sealed record PreActionContext(
    SystemActionKind Verb,
    string ActionJson,
    long Tick,
    IActionWorldView? WorldView);

/// <summary>Verdict kinds a PreAction hook can return.</summary>
public enum PreActionVerdictKind
{
    /// <summary>Let the action through unchanged.</summary>
    Pass,

    /// <summary>Replace the payload (<see cref="PreActionHookVerdict.RewrittenActionJson"/>) and note why.</summary>
    Rewrite,

    /// <summary>Refuse the action (<see cref="PreActionHookVerdict.BlockReason"/> — surfaces as ErrHookBlocked).</summary>
    Block,
}

/// <summary>
/// One hook's verdict: Pass, Rewrite(json, note) or Block(reason). Pure
/// value; built only through the factory methods so no half-filled
/// verdict can exist.
/// </summary>
public sealed record PreActionHookVerdict(
    PreActionVerdictKind Kind,
    string? RewrittenActionJson = null,
    string? Note = null,
    string? BlockReason = null)
{
    /// <summary>Let the action through unchanged.</summary>
    public static PreActionHookVerdict Pass() => new(PreActionVerdictKind.Pass);

    /// <summary>Replace the action JSON (the rewrite feeds later hooks and the dispatch).</summary>
    /// <param name="actionJson">The rewritten payload (still closed-schema shaped).</param>
    /// <param name="note">Human-readable rewrite notice (提示) recorded in the registry log.</param>
    public static PreActionHookVerdict Rewrite(string actionJson, string note) =>
        new(PreActionVerdictKind.Rewrite, actionJson, note);

    /// <summary>Refuse the action; the registry returns Err with <c>ErrHookBlocked:&lt;id&gt;:&lt;reason&gt;</c>.</summary>
    /// <param name="reason">Structured refusal cause.</param>
    public static PreActionHookVerdict Block(string reason) =>
        new(PreActionVerdictKind.Block, BlockReason: reason);
}

/// <summary>
/// One registered PreAction hook (F-075, 注册制): consulted by the
/// <see cref="PreActionHookChain"/> in registration order. Implementations
/// must be pure with respect to the simulation — world reads go through
/// <see cref="PreActionContext.WorldView"/>, mutations still enter the
/// sim only through the action itself.
/// </summary>
public interface IPreActionHook
{
    /// <summary>Stable hook id used in block reasons and logs.</summary>
    string Id { get; }

    /// <summary>Inspects (and possibly rewrites or blocks) one pending action.</summary>
    /// <param name="context">Verb, current payload, tick and world view.</param>
    PreActionHookVerdict Apply(PreActionContext context);
}

/// <summary>
/// Chain outcome of the PreAction hooks (T-070): the (possibly rewritten)
/// payload plus the accumulated rewrite notes, or the blocking hook id
/// and reason. <see cref="ActionJson"/> is reference-equal to the input
/// when no hook rewrote anything.
/// </summary>
/// <param name="ActionJson">Payload after the chain (rewrites feed forward).</param>
/// <param name="Blocked">True when a hook blocked the action.</param>
/// <param name="BlockedBy">Id of the blocking hook (null unless blocked).</param>
/// <param name="BlockReason">Structured refusal cause (null unless blocked).</param>
/// <param name="Notes">Rewrite notes in application order (提示; empty when none).</param>
public sealed record PreActionChainResult(
    string ActionJson,
    bool Blocked,
    string? BlockedBy,
    string? BlockReason,
    IReadOnlyList<string> Notes);

/// <summary>
/// Ordered PreAction hook chain (F-075, T-070): registration order is the
/// application order; a Rewrite feeds the rewritten payload to every
/// later hook (chained rewrites); the first Block wins and short-circuits
/// the chain. The registry re-validates a rewritten payload against the
/// closed schema before dispatch — a hook can never smuggle a field past
/// RL6. <see cref="Empty"/> installs no hooks (zero behavior drift; the
/// pipeline composition decides which built-ins to register).
/// </summary>
public sealed class PreActionHookChain
{
    /// <summary>Chain with no hooks — every action passes through untouched.</summary>
    public static PreActionHookChain Empty { get; } = new(Array.Empty<IPreActionHook>());

    private readonly IReadOnlyList<IPreActionHook> _hooks;

    /// <param name="hooks">Hooks in application order (first registered runs first).</param>
    public PreActionHookChain(IEnumerable<IPreActionHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _hooks = hooks.ToArray();
    }

    /// <summary>Runs every hook over the pending action: chained rewrites, first block wins.</summary>
    /// <param name="verb">Closed verb of the pending action.</param>
    /// <param name="actionJson">Validated payload entering the chain.</param>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="worldView">Read-only world projection (null when unwired).</param>
    public PreActionChainResult Apply(SystemActionKind verb, string actionJson, long tick, IActionWorldView? worldView)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        if (_hooks.Count == 0)
        {
            return new PreActionChainResult(actionJson, Blocked: false, BlockedBy: null, BlockReason: null, Notes: Array.Empty<string>());
        }

        string current = actionJson;
        var notes = new List<string>();
        foreach (IPreActionHook hook in _hooks)
        {
            PreActionHookVerdict verdict = hook.Apply(new PreActionContext(verb, current, tick, worldView));
            if (verdict.Kind == PreActionVerdictKind.Block)
            {
                return new PreActionChainResult(current, Blocked: true, BlockedBy: hook.Id, BlockReason: verdict.BlockReason, Notes: notes);
            }

            if (verdict.Kind == PreActionVerdictKind.Rewrite
                && !string.IsNullOrEmpty(verdict.RewrittenActionJson))
            {
                current = verdict.RewrittenActionJson;
                notes.Add($"{hook.Id}:{verdict.Note}");
            }
        }

        return new PreActionChainResult(current, Blocked: false, BlockedBy: null, BlockReason: null, Notes: notes);
    }
}
