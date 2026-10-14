namespace EraCore.Audio;

/// <summary>
/// The three independent mixer channels of v3.2 §14 (T-092): BGM / 音效 (the
/// ding bus) / 军师语音 — each with its own volume and mute, so the settings
/// panel exposes three sliders that never reach into each other.
/// </summary>
public enum AudioChannel
{
    /// <summary>BGM 三态循环 (F-135).</summary>
    Bgm = 0,

    /// <summary>音效通道 — the T-060 ding bus (F-112/F-136).</summary>
    Sfx = 1,

    /// <summary>军师人格语音通道 (F-137; 中性旁白降级共用本通道).</summary>
    Voice = 2,
}

/// <summary>Volume/mute snapshot of all three channels — the settings-panel seam.</summary>
/// <param name="BgmVolume">Linear gain of the BGM channel in [0, 1].</param>
/// <param name="BgmMuted">Master mute of the BGM channel.</param>
/// <param name="SfxVolume">Linear gain of the ding channel in [0, 1].</param>
/// <param name="SfxMuted">Master mute of the ding channel.</param>
/// <param name="VoiceVolume">Linear gain of the advisor-voice channel in [0, 1].</param>
/// <param name="VoiceMuted">Master mute of the advisor-voice channel.</param>
public sealed record MixerSettings(
    double BgmVolume, bool BgmMuted,
    double SfxVolume, bool SfxMuted,
    double VoiceVolume, bool VoiceMuted)
{
    /// <summary>Factory defaults: everything at full linear gain, nothing muted.</summary>
    public static MixerSettings Default { get; } = new(1.0, false, 1.0, false, 1.0, false);
}

/// <summary>
/// Three-channel audio mixer (T-092, §14 总线混音器): independent linear
/// volume + mute per channel with effective-gain resolution — the adapter
/// applies <see cref="EffectiveGain"/> to its players once per change; the
/// ding channel stays owned by <see cref="DingBus"/> (the adapter syncs
/// them, presentation stays thin, CS-1.6). Pure state, zero engine types.
/// </summary>
public sealed class AudioMixer
{
    /// <summary>Number of mixer channels (BGM / 音效 / 军师语音).</summary>
    public const int ChannelCount = 3;

    private readonly double[] _volumes = new double[ChannelCount];
    private readonly bool[] _muted = new bool[ChannelCount];

    /// <summary>Creates the mixer at factory defaults (all channels 1.0, unmuted).</summary>
    public AudioMixer()
    {
        Array.Fill(_volumes, 1.0);
    }

    /// <summary>
    /// Linear volume of one channel in [0, 1] — the independent volume
    /// switch of §14 (three sliders).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">channel undefined, value non-finite or outside [0, 1].</exception>
    public void SetVolume(AudioChannel channel, double volume)
    {
        int index = CheckedIndex(channel);
        if (!double.IsFinite(volume) || volume < 0 || volume > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume is a linear gain in [0, 1].");
        }

        _volumes[index] = volume;
    }

    /// <summary>Volume of one channel.</summary>
    /// <exception cref="ArgumentOutOfRangeException">channel undefined.</exception>
    public double Volume(AudioChannel channel) => _volumes[CheckedIndex(channel)];

    /// <summary>Mutes/unmutes one channel (other channels unaffected).</summary>
    /// <exception cref="ArgumentOutOfRangeException">channel undefined.</exception>
    public void SetMuted(AudioChannel channel, bool muted) => _muted[CheckedIndex(channel)] = muted;

    /// <summary>Mute state of one channel.</summary>
    /// <exception cref="ArgumentOutOfRangeException">channel undefined.</exception>
    public bool IsMuted(AudioChannel channel) => _muted[CheckedIndex(channel)];

    /// <summary>Effective gain the adapter applies: 0 when muted, else the channel volume.</summary>
    /// <exception cref="ArgumentOutOfRangeException">channel undefined.</exception>
    public double EffectiveGain(AudioChannel channel) =>
        IsMuted(channel) ? 0.0 : Volume(channel);

    /// <summary>Current snapshot of all three channels.</summary>
    public MixerSettings CurrentSettings => new(
        _volumes[(int)AudioChannel.Bgm], _muted[(int)AudioChannel.Bgm],
        _volumes[(int)AudioChannel.Sfx], _muted[(int)AudioChannel.Sfx],
        _volumes[(int)AudioChannel.Voice], _muted[(int)AudioChannel.Voice]);

    /// <summary>Applies a snapshot atomically (validated before anything changes).</summary>
    /// <exception cref="ArgumentNullException">settings is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">any volume outside [0, 1].</exception>
    public void ApplySettings(MixerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        double[] volumes = { settings.BgmVolume, settings.SfxVolume, settings.VoiceVolume };
        for (int i = 0; i < volumes.Length; i++)
        {
            if (!double.IsFinite(volumes[i]) || volumes[i] < 0 || volumes[i] > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settings), volumes[i], $"Channel {(AudioChannel)i} volume is outside [0, 1].");
            }
        }

        SetVolume(AudioChannel.Bgm, settings.BgmVolume);
        SetVolume(AudioChannel.Sfx, settings.SfxVolume);
        SetVolume(AudioChannel.Voice, settings.VoiceVolume);
        SetMuted(AudioChannel.Bgm, settings.BgmMuted);
        SetMuted(AudioChannel.Sfx, settings.SfxMuted);
        SetMuted(AudioChannel.Voice, settings.VoiceMuted);
    }

    private static int CheckedIndex(AudioChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown mixer channel.");
        }

        return (int)channel;
    }
}
