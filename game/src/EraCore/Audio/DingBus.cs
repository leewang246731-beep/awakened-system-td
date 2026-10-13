using EraCore.Sim;

namespace EraCore.Audio;

/// <summary>
/// The three ding tiers of v3.2 §10.3 (C-25, F-112): one sound flavor per
/// event class. Tier decides WHICH synthesized cue the player adapter plays;
/// personality timbres swap the synthesis table itself later (T-092).
/// </summary>
public enum DingTier
{
    /// <summary>情报 "叮" — one short sine blip (wave preview, system report).</summary>
    Info = 0,

    /// <summary>奖励 "叮叮" — two quick blips (research done, chest opened).</summary>
    Reward = 1,

    /// <summary>危机 "叮——警告！" — low square-wave alarm (defense line, supply cut).</summary>
    Crisis = 2,
}

/// <summary>
/// Event classes that can request a ding (F-112, v3.2 §10.3). Each value is
/// one observable game event the thin wiring binds to; producers arrive with
/// their owning components (wave scheduler, research, chests, defense
/// monitor) — the bus itself only needs the class id and the tick.
/// </summary>
public enum DingTrigger
{
    /// <summary>情报: wave inbound preview (波次预告).</summary>
    WavePreview = 0,

    /// <summary>情报: system report delivered to the player (系统汇报, K-8 Report stage).</summary>
    SystemReport = 1,

    /// <summary>奖励: research finished (研究完成).</summary>
    ResearchComplete = 2,

    /// <summary>奖励: chest opened (宝箱).</summary>
    ChestOpened = 3,

    /// <summary>危机: defense-line integrity dropped below 40% (防线完整度 &lt; 40%).</summary>
    DefenseLineLow = 4,

    /// <summary>危机: supply bottleneck episode (断供瓶颈, T-048 SupplyBottleneckEvent).</summary>
    SupplyBottleneck = 5,
}

/// <summary>Outcome of one ding request (the smoke asserts on these).</summary>
public enum DingOutcome
{
    /// <summary>Cue raised — the player adapter plays the tier's stream now.</summary>
    Played = 0,

    /// <summary>Same event class fired inside the throttle window — suppressed.</summary>
    Throttled = 1,

    /// <summary>The ding channel is muted — no cue, throttle state untouched.</summary>
    Muted = 2,
}

/// <summary>
/// One audible cue handed to the player adapter. Carries the effective
/// volume snapshot (the independent ding volume switch of v3.2 §10.3/§14)
/// so the adapter never reads bus state at play time.
/// </summary>
/// <param name="Trigger">Event class that fired.</param>
/// <param name="Tier">Tier the class maps to (§10.3).</param>
/// <param name="Tick">Tick of the driving event (K-1 30 Hz domain).</param>
/// <param name="Volume">Linear gain in [0, 1] captured at play time.</param>
public sealed record DingCue(DingTrigger Trigger, DingTier Tier, long Tick, double Volume);

/// <summary>
/// Volume/mute snapshot of the ding channel — the persistence seam the
/// T-055 settings panel reads and writes (独立音量开关, v3.2 §10.3/§14).
/// </summary>
/// <param name="Volume">Linear gain in [0, 1].</param>
/// <param name="Muted">Master mute of the ding channel only (BGM unaffected).</param>
public sealed record DingAudioSettings(double Volume, bool Muted);

/// <summary>
/// Pure ding dispatcher (C-25, T-060, F-112): maps the six event classes to
/// the three tiers of v3.2 §10.3, throttles repeats of the SAME event class
/// inside a 2 s tick window, and applies the ding channel's independent
/// volume/mute switch. Zero engine types (D-01); time is the tick carried by
/// the driving event (K-1 30 Hz domain) so dispatch stays deterministic and
/// replay-safe — the Godot adapter (<c>DingAudioPlayer</c>) subscribes
/// <see cref="CueRaised"/> and plays the matching code-synthesized stream.
/// </summary>
public sealed class DingBus
{
    private static readonly IReadOnlyDictionary<DingTrigger, DingTier> TierMapTable =
        new Dictionary<DingTrigger, DingTier>
        {
            // 情报类 → Info "叮"
            [DingTrigger.WavePreview] = DingTier.Info,
            [DingTrigger.SystemReport] = DingTier.Info,

            // 奖励类 → Reward "叮叮"
            [DingTrigger.ResearchComplete] = DingTier.Reward,
            [DingTrigger.ChestOpened] = DingTier.Reward,

            // 危机类 → Crisis "叮——警告！"
            [DingTrigger.DefenseLineLow] = DingTier.Crisis,
            [DingTrigger.SupplyBottleneck] = DingTier.Crisis,
        };

    private readonly int _throttleTicks;
    private readonly Dictionary<DingTrigger, long> _lastPlayedTick = new();
    private readonly List<DingCue> _cues = new();
    private double _volume = 1.0;

    /// <summary>Creates the bus with the default 2 s throttle window.</summary>
    public DingBus()
        : this(DefaultThrottleTicks)
    {
    }

    /// <summary>
    /// Creates the bus with an explicit per-class throttle window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// throttleTicks below 1 (a zero-tick window would never throttle).
    /// </exception>
    public DingBus(int throttleTicks)
    {
        if (throttleTicks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throttleTicks), throttleTicks, "Throttle window must be at least 1 tick.");
        }

        _throttleTicks = throttleTicks;
    }

    /// <summary>Tick rate of the simulation domain (K-1: 30 Hz, shared with <see cref="SimClock"/>).</summary>
    public const int TicksPerSecond = SimClock.TicksPerSecond;

    /// <summary>Throttle window in seconds (同类事件 2 s 内不重复, T-060 card).</summary>
    public const int ThrottleSeconds = 2;

    /// <summary>Default per-class throttle window in ticks (30 Hz × 2 s).</summary>
    public static int DefaultThrottleTicks => TicksPerSecond * ThrottleSeconds;

    /// <summary>The full event-class → tier map (v3.2 §10.3), for adapters and tests.</summary>
    public static IReadOnlyDictionary<DingTrigger, DingTier> TierMap => TierMapTable;

    /// <summary>Number of mapped event classes (six: 2 info + 2 reward + 2 crisis).</summary>
    public static int MappedTriggerCount => TierMapTable.Count;

    /// <summary>
    /// Tier of one event class. Total by construction — tests assert every
    /// defined <see cref="DingTrigger"/> value maps.
    /// </summary>
    /// <exception cref="ArgumentException">trigger is not a mapped value.</exception>
    public static DingTier TierOf(DingTrigger trigger) =>
        TierMapTable.TryGetValue(trigger, out DingTier tier)
            ? tier
            : throw new ArgumentException(
                $"Ding trigger {trigger} has no tier mapping — extend {nameof(TierMapTable)} first.",
                nameof(trigger));

    /// <summary>Per-class throttle window in ticks (≥ 1).</summary>
    public int ThrottleTicks => _throttleTicks;

    /// <summary>
    /// Linear gain of the ding channel in [0, 1] — the independent volume
    /// switch of v3.2 §10.3/§14 (separate from BGM, which lands with T-092).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// value non-finite or outside [0, 1].
    /// </exception>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Volume is a linear gain in [0, 1].");
            }

            _volume = value;
        }
    }

    /// <summary>
    /// Master mute of the ding channel. Muted requests return
    /// <see cref="DingOutcome.Muted"/> and leave the throttle windows
    /// untouched, so un-muting plays the next real event immediately.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>Cues raised so far, in raise order (observability).</summary>
    public IReadOnlyList<DingCue> Cues => _cues;

    /// <summary>Requests suppressed by the throttle window so far.</summary>
    public int ThrottledCount { get; private set; }

    /// <summary>
    /// Raised once per played cue, inline and synchronously (CS-1.5) — the
    /// Godot adapter relays this to the audio thread via its own scene node.
    /// </summary>
    public event Action<DingCue>? CueRaised;

    /// <summary>
    /// Dispatches one ding request: maps the class to its tier, applies mute
    /// and the per-class throttle window, then (when played) raises
    /// <see cref="CueRaised"/> with the volume snapshot.
    /// </summary>
    /// <param name="trigger">Event class.</param>
    /// <param name="tick">Tick of the driving event (may be -1 outside a run, K-8).</param>
    /// <returns>Played / Throttled / Muted.</returns>
    public DingOutcome Request(DingTrigger trigger, long tick)
    {
        DingTier tier = TierOf(trigger);

        if (Muted)
        {
            return DingOutcome.Muted;
        }

        if (_lastPlayedTick.TryGetValue(trigger, out long last) && tick - last < _throttleTicks)
        {
            ThrottledCount++;
            return DingOutcome.Throttled;
        }

        _lastPlayedTick[trigger] = tick;
        DingCue cue = new(trigger, tier, tick, _volume);
        _cues.Add(cue);
        CueRaised?.Invoke(cue);
        return DingOutcome.Played;
    }

    /// <summary>Current volume/mute snapshot (T-055 settings-panel read path).</summary>
    public DingAudioSettings CurrentSettings => new(_volume, Muted);

    /// <summary>
    /// Applies a settings snapshot atomically (T-055 settings-panel write
    /// path — volume is validated before anything changes).
    /// </summary>
    /// <exception cref="ArgumentNullException">settings is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">settings.Volume outside [0, 1].</exception>
    public void ApplySettings(DingAudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Volume = settings.Volume;
        Muted = settings.Muted;
    }
}
