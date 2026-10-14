namespace EraCore.Audio;

/// <summary>
/// Crossfade progress snapshot of one query tick (T-092): which state fades
/// out, which fades in, and their linear gains — everything the adapter
/// needs to set player volumes for that tick.
/// </summary>
/// <param name="From">State fading out (equals <paramref name="To"/> when idle).</param>
/// <param name="To">State fading in.</param>
/// <param name="FromGain">Linear gain of <paramref name="From"/> in [0, 1].</param>
/// <param name="ToGain">Linear gain of <paramref name="To"/> in [0, 1].</param>
public sealed record CrossFadeGains(BgmLayerState From, BgmLayerState To, double FromGain, double ToGain);

/// <summary>
/// Pure equal-power crossfade state machine (T-092, F-135, v3.2 §14
/// 三态淡入淡出): moves loudness between the three BGM layer states over a
/// tick window with cos/sin envelopes — <c>from² + to² = 1</c> for the whole
/// fade, the constant-perceived-loudness property of equal-power mixing.
/// Time is the driving event's tick (K-1 30 Hz domain, CS-1.4: no wall
/// clock), so fades are deterministic and replay-safe. A re-target while a
/// fade is still in flight restarts from the DOMINANT state (the side above
/// 0.5 gain): one bounded discontinuity instead of unbounded fade chains —
/// the snap magnitude is at most the faded-out amount (≤ 1 − cos(π/4) at
/// the midpoint).
/// </summary>
public sealed class CrossFadeBus
{
    private readonly int _fadeTicks;
    private BgmLayerState _from;
    private BgmLayerState _to;
    private long _startTick;

    /// <summary>
    /// Creates the bus resting on <paramref name="initial"/> with an
    /// explicit fade window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// fadeTicks below 1 (an instant fade cannot fade) or initial undefined.
    /// </exception>
    public CrossFadeBus(BgmLayerState initial, int fadeTicks)
    {
        if (!Enum.IsDefined(initial))
        {
            throw new ArgumentOutOfRangeException(nameof(initial), initial, "Unknown BGM state.");
        }

        if (fadeTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeTicks), fadeTicks, "Fade window must be at least 1 tick.");
        }

        _from = initial;
        _to = initial;
        _fadeTicks = fadeTicks;
    }

    /// <summary>Fade window in ticks (≥ 1; 30 Hz domain ⇒ 30 ticks = 1 s).</summary>
    public int FadeTicks => _fadeTicks;

    /// <summary>The state that is (or will be, once the fade completes) dominant.</summary>
    public BgmLayerState Current => _to;

    /// <summary>
    /// Requests a transition to <paramref name="target"/> starting at
    /// <paramref name="tick"/>. Same-target requests are no-ops (false); a
    /// re-target mid-fade restarts from the dominant state.
    /// </summary>
    /// <returns>True when a new fade actually started.</returns>
    /// <exception cref="ArgumentOutOfRangeException">target is not a defined state.</exception>
    public bool Request(BgmLayerState target, long tick)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown BGM state.");
        }

        if (target == _to)
        {
            return false; // already there or already fading there
        }

        _from = DominantAt(tick);
        _to = target;
        _startTick = tick;
        return true;
    }

    /// <summary>True while the fade to <see cref="Current"/> is still running at this tick.</summary>
    public bool IsFading(long tick) => _from != _to && Progress(tick) < 1.0;

    /// <summary>
    /// Gain envelope pair at one tick: cos/sin of the progress quarter-circle.
    /// Before the start tick (clock skew) progress clamps to 0.
    /// </summary>
    public CrossFadeGains GainsAt(long tick)
    {
        double angle = Progress(tick) * (Math.PI / 2.0);
        return new CrossFadeGains(_from, _to, Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>Gain of one state at this tick (0 for states not in the fade).</summary>
    public double GainOf(BgmLayerState state, long tick)
    {
        CrossFadeGains gains = GainsAt(tick);
        if (state == gains.To)
        {
            return gains.ToGain;
        }

        return state == gains.From ? gains.FromGain : 0.0;
    }

    /// <summary>
    /// The dominant state at this tick — the fade destination once past the
    /// equal-loudness midpoint, the source before it.
    /// </summary>
    public BgmLayerState DominantAt(long tick) => Progress(tick) >= 0.5 ? _to : _from;

    private double Progress(long tick)
    {
        if (_from == _to)
        {
            return 1.0; // idle bus: target gain fully applied
        }

        long elapsed = tick - _startTick;
        if (elapsed <= 0)
        {
            return 0.0;
        }

        return Math.Min(elapsed / (double)_fadeTicks, 1.0);
    }
}
