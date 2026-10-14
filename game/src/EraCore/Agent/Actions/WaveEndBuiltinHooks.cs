namespace EraCore.Agent.Actions;

/// <summary>
/// Built-in WaveEnd hook (T-070, F-075): 守成完成度检查 — a report that
/// claims the wave was HELD (守住) is held back while unpatched leak paths
/// remain (漏怪路径未补): the block reason is fed back so the agent
/// patches the defense first and re-reports. The leak-path counter is
/// injected: pass a provider over the live world (the wave/defense
/// runtime supplies it); the default (null) reports zero leaks — the
/// probe wiring lands with the wave-runtime integration, until then the
/// hook is inert by construction. Non-held reports always pass.
/// </summary>
public sealed class HeldCompletenessHook : IWaveEndHook
{
    private readonly Func<int> _unpatchedLeakPaths;

    /// <param name="unpatchedLeakPaths">
    /// Provider of the current count of leaked-enemy paths that remain
    /// unpatched (漏怪路径未补条数); null = no leak data (0).
    /// </param>
    public HeldCompletenessHook(Func<int>? unpatchedLeakPaths = null)
    {
        _unpatchedLeakPaths = unpatchedLeakPaths ?? (static () => 0);
    }

    /// <inheritdoc />
    public string Id => "held-completeness";

    /// <inheritdoc />
    public WaveEndHookVerdict Review(WaveEndContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.ClaimsHeld)
        {
            return WaveEndHookVerdict.Pass();
        }

        int leaks = _unpatchedLeakPaths();
        if (leaks <= 0)
        {
            return WaveEndHookVerdict.Pass();
        }

        return WaveEndHookVerdict.Block(
            $"守住 汇报暂缓：{leaks} 条漏怪路径未补——先补防线，再汇报守成（WaveEnd 完成度把关，F.4 ≤3 轮）");
    }
}
