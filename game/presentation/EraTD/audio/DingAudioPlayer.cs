using System.Text.Json;
using EraCore.Audio;
using EraCore.Contracts.Journal;
using EraCore.Supply;
using Godot;

namespace EraTD.Audio;

/// <summary>
/// T-060 (C-25, F-112) scene-side ding wiring: one node that owns the
/// <see cref="DingBus"/>, renders the three §10.3 cues to
/// <see cref="AudioStreamWav"/> streams at startup (pure code synthesis —
/// no binary audio enters the repository) and plays the tier's stream on
/// every cue. The rendered WAV files are persisted under
/// <c>res://audio/generated/</c> so a human can audition the three cues
/// (可听测) with any player after one run; a <c>.gdignore</c> keeps the
/// Godot import scan away from those runtime artifacts.
/// <para>
/// Headless smoke cannot listen, so <c>_Ready</c> replays the acceptance
/// scenario — one event per §10.3 class, then a same-class repeat inside
/// the 2 s window — and asserts three play calls plus one throttle
/// interception before printing the <c>DING_OK</c> marker.
/// </para>
/// <para>
/// Real-producer seams (event-driven, subscribed by the owning components
/// as they land): <see cref="OnJournalEvent"/> (K-8 Report stage → 情报
/// "叮"), <see cref="OnBottleneck"/> (T-048 SupplyBottleneckEvent → 危机),
/// <see cref="OnWavePreview"/>/<see cref="OnResearchComplete"/>/
/// <see cref="OnChestOpened"/>/<see cref="OnDefenseIntegrity"/> (wave
/// scheduler / research / chests / defense monitor). Volume:
/// <see cref="SetVolume"/>/<see cref="SetMuted"/>/<see cref="ApplySettings"/>
/// is the interface the T-055 settings panel binds to (独立音量开关); the
/// personality timbres of T-092 swap the synthesis table behind the same
/// <see cref="DingTier"/> key.
/// </para>
/// </summary>
public sealed partial class DingAudioPlayer : Node
{
    private const string GeneratedDir = "res://audio/generated";

    /// <summary>§10.3 warning line: 警告！防线完整度低于 40%.</summary>
    private const double DefenseWarnBelow = 0.40;

    private readonly Dictionary<DingTier, AudioStreamPlayer> _players = new();

    private DingBus _bus = null!;
    private int _playCalls;

    public override void _Ready()
    {
        _bus = new DingBus();
        _bus.CueRaised += OnCue;

        // ---- 1) render streams + persist WAV artifacts -------------------
        int filesWritten = 0;
        foreach (DingTier tier in Enum.GetValues<DingTier>())
        {
            byte[] wav = DingSynth.SynthesizeWav(tier);
            if (Persist(tier, wav))
            {
                filesWritten++;
            }

            AudioStreamWav stream = new()
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = DingSynth.SampleRateHz,
                Stereo = false,
                Data = DingSynth.SynthesizePcm(tier),
            };

            AudioStreamPlayer player = new() { Name = $"Ding{tier}", Stream = stream };
            AddChild(player);
            _players[tier] = player;
        }

        // ---- 2) acceptance self-check (headless "audible" → verifiable) ---
        // one event per §10.3 class plays; a same-class repeat one tick
        // later is intercepted by the 2 s throttle.
        DingOutcome info = _bus.Request(DingTrigger.WavePreview, tick: 0);       // 情报"叮"
        DingOutcome reward = _bus.Request(DingTrigger.ResearchComplete, tick: 0); // 奖励"叮叮"
        DingOutcome crisis = _bus.Request(DingTrigger.DefenseLineLow, tick: 0);   // 危机"叮——警告！"
        DingOutcome repeat = _bus.Request(DingTrigger.WavePreview, tick: 1);      // throttled

        int tierCount = DingBus.TierMap.Values.Distinct().Count();
        bool ok = info == DingOutcome.Played
               && reward == DingOutcome.Played
               && crisis == DingOutcome.Played
               && repeat == DingOutcome.Throttled
               && _playCalls == 3
               && _bus.Cues.Count == 3
               && _bus.ThrottledCount == 1
               && tierCount == 3
               && filesWritten == 3;

        if (ok)
        {
            GD.Print(
                $"DING_OK map={tierCount} throttle=on " +
                $"cues={_bus.Cues.Count} blocked={_bus.ThrottledCount} plays={_playCalls} " +
                $"files={filesWritten} rate={DingSynth.SampleRateHz} vol={_bus.Volume:0.00}");
        }
        else
        {
            GD.PushError(
                $"ding wiring self-check failed: outcomes {info}/{reward}/{crisis}/{repeat} " +
                $"plays={_playCalls} cues={_bus.Cues.Count} blocked={_bus.ThrottledCount} " +
                $"tiers={tierCount} files={filesWritten}");
            GD.Print(
                $"DING_FAIL map={tierCount} outcomes={info}/{reward}/{crisis}/{repeat} " +
                $"plays={_playCalls} files={filesWritten}");
        }
    }

    /// <summary>Plays the cue's tier stream at the cue's volume snapshot.</summary>
    private void OnCue(DingCue cue)
    {
        if (!_players.TryGetValue(cue.Tier, out AudioStreamPlayer? player))
        {
            return;
        }

        _playCalls++;
        player.VolumeDb = cue.Volume <= 0.0 ? -80.0f : Mathf.LinearToDb((float)cue.Volume);
        player.Play();
    }

    /// <summary>Writes one rendered cue next to the project so it can be auditioned by hand.</summary>
    private static bool Persist(DingTier tier, byte[] wav)
    {
        DirAccess? root = DirAccess.Open("res://");
        if (root is null)
        {
            return false;
        }

        _ = root.MakeDirRecursive("audio/generated"); // AlreadyExists on later runs — fine

        string path = $"{GeneratedDir}/ding-{tier.ToString().ToLowerInvariant()}.wav";
        Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            return false; // read-only res:// (exported pack): streams still play from memory
        }

        file.StoreBuffer(wav);
        file.Close();
        return true;
    }

    // ---- real-producer seams ------------------------------------------------

    /// <summary>
    /// K-8 journal relay: the final system report the player receives dings
    /// as 情报 (v3.2 §10.3); every other journal stage stays silent (null).
    /// Bind to the JournalWriter callback / bus relay of the pipeline (T-053).
    /// </summary>
    public DingOutcome? OnJournalEvent(JournalEvent evt)
    {
        DingTrigger? trigger = DingJournalMap.Map(evt.Kind, TryPayloadType(evt.PayloadJson));
        return trigger is { } t ? _bus.Request(t, evt.Tick) : null;
    }

    /// <summary>T-048 bottleneck relay: one bottleneck episode dings as 危机.</summary>
    public DingOutcome OnBottleneck(SupplyBottleneckEvent evt) =>
        _bus.Request(DingTrigger.SupplyBottleneck, evt.Tick);

    /// <summary>Wave scheduler relay: inbound wave preview dings as 情报.</summary>
    public DingOutcome OnWavePreview(long tick) => _bus.Request(DingTrigger.WavePreview, tick);

    /// <summary>Research relay: a finished research dings as 奖励.</summary>
    public DingOutcome OnResearchComplete(long tick) => _bus.Request(DingTrigger.ResearchComplete, tick);

    /// <summary>Chest relay: an opened chest dings as 奖励.</summary>
    public DingOutcome OnChestOpened(long tick) => _bus.Request(DingTrigger.ChestOpened, tick);

    /// <summary>Defense monitor relay: crossing below the §10.3 line dings as 危机; above it stays silent (null).</summary>
    public DingOutcome? OnDefenseIntegrity(long tick, double integrity) =>
        integrity < DefenseWarnBelow ? _bus.Request(DingTrigger.DefenseLineLow, tick) : null;

    // ---- T-055 settings-panel interface (独立音量开关) ------------------------

    /// <summary>Sets the ding channel's linear volume in [0, 1] (BGM unaffected).</summary>
    public void SetVolume(double volume) => _bus.Volume = volume;

    /// <summary>Mutes/unmutes the ding channel only.</summary>
    public void SetMuted(bool muted) => _bus.Muted = muted;

    /// <summary>Applies a settings snapshot atomically.</summary>
    public void ApplySettings(DingAudioSettings settings) => _bus.ApplySettings(settings);

    /// <summary>Current volume/mute snapshot for the settings panel to display.</summary>
    public DingAudioSettings CurrentSettings => _bus.CurrentSettings;

    private static string? TryPayloadType(string payloadJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("type", out JsonElement element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null; // unparsable payload is never a report — stay silent
        }
    }
}
