using System.Text;

namespace EraCore.Audio;

/// <summary>
/// Canonical PCM16-mono WAV container writer (T-092): the 44-byte
/// RIFF/WAVE header every player opens, shared by the BGM loop and persona
/// voice renderers so the on-disk audition artifacts (可听测) of both share
/// one container definition. Pure bytes in, pure bytes out.
/// </summary>
public static class WavContainer
{
    /// <summary>Wraps raw little-endian PCM16 mono samples into a WAV file image.</summary>
    /// <param name="sampleRateHz">Stream sample rate (e.g. 16000/22050).</param>
    /// <param name="pcm">16-bit little-endian mono sample payload.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// sampleRateHz below 8000, or <paramref name="pcm"/> not a whole number of samples.
    /// </exception>
    /// <exception cref="ArgumentNullException">pcm is null.</exception>
    public static byte[] Wrap(int sampleRateHz, byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRateHz < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Sample rate below 8000 Hz is not a WAV-legal rate here.");
        }

        if (pcm.Length % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pcm), pcm.Length, "PCM16 payload must have an even byte length.");
        }

        using MemoryStream stream = new(44 + pcm.Length);
        using BinaryWriter writer = new(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);                      // fmt chunk size
        writer.Write((short)1);                // PCM
        writer.Write((short)1);                // mono
        writer.Write(sampleRateHz);
        writer.Write(sampleRateHz * 2);        // byte rate = rate × 1 ch × 2 B
        writer.Write((short)2);                // block align
        writer.Write((short)16);               // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return stream.ToArray();
    }
}
