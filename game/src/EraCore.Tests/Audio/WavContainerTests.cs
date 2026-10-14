using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the canonical PCM16-mono WAV container (T-092): header math on
/// two rates and the malformed-payload guards.
/// </summary>
// [C-25] WAV 容器写入：头部数学与非法载荷守卫
public class WavContainerTests
{
    [Theory] // legal — canonical header at two rates, payload verbatim
    [InlineData(16000)]
    [InlineData(22050)]
    public void Header_is_canonical(int rate)
    {
        byte[] pcm = new byte[4000]; // 2000 samples — content irrelevant to the container
        new Random(42).NextBytes(pcm);

        byte[] wav = WavContainer.Wrap(rate, pcm);

        Assert.Equal(44 + 4000, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 4000, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // mono
        Assert.Equal(rate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(rate * 2, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));       // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(4000, BitConverter.ToInt32(wav, 40));
        Assert.True(wav.AsSpan(44).SequenceEqual(pcm));
    }

    [Theory] // illegal — odd payload length and sub-8000 rates are rejected
    [InlineData(7999)]
    [InlineData(0)]
    public void Illegal_rate_rejected(int rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavContainer.Wrap(rate, new byte[100]));
    }

    [Fact] // illegal — a truncated sample cannot be wrapped
    public void Odd_payload_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavContainer.Wrap(16000, new byte[101]));
    }

    [Fact] // illegal — null payload is rejected
    public void Null_payload_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => WavContainer.Wrap(16000, null!));
    }
}
