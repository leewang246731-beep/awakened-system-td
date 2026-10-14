using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the three-channel mixer (T-092, §14 总线混音器): channel
/// independence, effective-gain resolution, snapshot round-trips, and the
/// validation guards.
/// </summary>
// [C-25][F-135][F-137] 三通道混音总线（BGM/音效/军师语音）
public class AudioMixerTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — factory defaults: full gain, nothing muted
    public void Defaults_are_full_gain()
    {
        var mixer = new AudioMixer();

        foreach (AudioChannel channel in Enum.GetValues<AudioChannel>())
        {
            Assert.Equal(1.0, mixer.Volume(channel));
            Assert.False(mixer.IsMuted(channel));
            Assert.Equal(1.0, mixer.EffectiveGain(channel));
        }

        Assert.Equal(AudioMixer.ChannelCount, Enum.GetValues<AudioChannel>().Length);
        Assert.Equal(3, AudioMixer.ChannelCount);
    }

    [Fact] // legal — volume on one channel never leaks into the others
    public void Volume_is_independent_per_channel()
    {
        var mixer = new AudioMixer();

        mixer.SetVolume(AudioChannel.Bgm, 0.8);
        mixer.SetVolume(AudioChannel.Sfx, 0.6);
        mixer.SetVolume(AudioChannel.Voice, 0.9);

        Assert.Equal(0.8, mixer.EffectiveGain(AudioChannel.Bgm));
        Assert.Equal(0.6, mixer.EffectiveGain(AudioChannel.Sfx));
        Assert.Equal(0.9, mixer.EffectiveGain(AudioChannel.Voice));
    }

    [Fact] // legal — mute zeroes the effective gain and unmute restores the remembered volume
    public void Mute_zeros_gain_without_losing_volume()
    {
        var mixer = new AudioMixer();
        mixer.SetVolume(AudioChannel.Voice, 0.7);

        mixer.SetMuted(AudioChannel.Voice, true);

        Assert.Equal(0.0, mixer.EffectiveGain(AudioChannel.Voice));
        Assert.Equal(0.7, mixer.Volume(AudioChannel.Voice)); // remembered
        Assert.Equal(1.0, mixer.EffectiveGain(AudioChannel.Bgm)); // others untouched

        mixer.SetMuted(AudioChannel.Voice, false);
        Assert.Equal(0.7, mixer.EffectiveGain(AudioChannel.Voice));
    }

    [Fact] // legal — settings snapshots round-trip
    public void Settings_roundtrip()
    {
        var mixer = new AudioMixer();
        var settings = new MixerSettings(0.5, true, 0.75, false, 0.25, true);

        mixer.ApplySettings(settings);

        Assert.Equal(settings, mixer.CurrentSettings);
    }

    // -------------------------------------------------------------- boundary

    [Theory] // boundary — volume endpoints 0 and 1 are legal gains
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Volume_endpoints_legal(double volume)
    {
        var mixer = new AudioMixer();

        mixer.SetVolume(AudioChannel.Bgm, volume);

        Assert.Equal(volume, mixer.Volume(AudioChannel.Bgm));
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — volumes one step outside the gain range
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Illegal_volume_rejected(double volume)
    {
        var mixer = new AudioMixer();

        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetVolume(AudioChannel.Bgm, volume));
    }

    [Fact] // illegal — undefined channel ids are rejected on every accessor
    public void Illegal_channel_rejected()
    {
        var mixer = new AudioMixer();
        var channel = (AudioChannel)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetVolume(channel, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.Volume(channel));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetMuted(channel, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.IsMuted(channel));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.EffectiveGain(channel));
    }

    [Fact] // illegal — ApplySettings validates BEFORE mutating (no partial application)
    public void Illegal_settings_reject_atomically()
    {
        var mixer = new AudioMixer();
        mixer.SetVolume(AudioChannel.Bgm, 0.4);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => mixer.ApplySettings(new MixerSettings(0.9, false, 0.5, false, 2.0, false)));

        Assert.Equal(0.4, mixer.Volume(AudioChannel.Bgm)); // untouched by the failed apply
        Assert.Equal(1.0, mixer.Volume(AudioChannel.Sfx));
    }

    [Fact] // illegal — null settings snapshot is rejected
    public void Null_settings_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioMixer().ApplySettings(null!));
    }
}
