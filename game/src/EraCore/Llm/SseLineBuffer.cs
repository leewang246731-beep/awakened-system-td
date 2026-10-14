using System.Runtime.InteropServices;
using System.Text;

namespace EraCore.Llm;

/// <summary>
/// Byte-chunk→text-line assembly for Server-Sent Events streams (方案
/// §11.7 SSE 行缓冲). Feed it raw chunks in arrival order; it returns the
/// complete lines seen so far and retains the unterminated tail until more
/// bytes arrive (半行拼接). Lines split on LF with a trailing CR stripped,
/// so both LF and CRLF servers parse identically. Buffering happens on
/// BYTES, so a multi-byte UTF-8 character split across two chunks is
/// decoded once, correctly.
/// </summary>
public sealed class SseLineBuffer
{
    private readonly List<byte> _pending = [];
    private bool _sawAnyByte;

    /// <summary>
    /// Accepts one chunk and returns every complete line it completed
    /// (decoded UTF-8, trailing CR removed, LF dropped). A line that began
    /// in an earlier chunk is prefixed with the buffered half line. Never
    /// throws on malformed UTF-8 — invalid sequences decode to U+FFFD like
    /// <c>Encoding.UTF8</c>'s default replacement fallback.
    /// </summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<string>();
        if (chunk.Length == 0)
        {
            return lines;
        }

        if (!_sawAnyByte)
        {
            // A leading UTF-8 BOM is noise from misconfigured relays, not data.
            _sawAnyByte = true;
            if (chunk.Length >= 3 && chunk[0] == 0xEF && chunk[1] == 0xBB && chunk[2] == 0xBF)
            {
                chunk = chunk[3..];
                if (chunk.Length == 0)
                {
                    return lines;
                }
            }
        }

        var start = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != (byte)'\n')
            {
                continue;
            }

            var lineSpan = chunk[start..i];
            if (lineSpan.Length > 0 && lineSpan[^1] == (byte)'\r')
            {
                lineSpan = lineSpan[..^1];
            }

            if (_pending.Count > 0)
            {
                // Half line from a previous chunk: prefix, emit, clear.
                _pending.AddRange(lineSpan);
                lines.Add(Decode(CollectionsMarshal.AsSpan(_pending)));
                _pending.Clear();
            }
            else
            {
                lines.Add(Decode(lineSpan));
            }

            start = i + 1;
        }

        _pending.AddRange(chunk[start..]);
        return lines;
    }

    /// <summary>
    /// Returns the still-unterminated tail at end-of-stream (SSE tolerates
    /// a final line without LF), or null when nothing is pending. The
    /// buffer is emptied.
    /// </summary>
    public string? Flush()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var tail = Decode(CollectionsMarshal.AsSpan(_pending));
        _pending.Clear();
        return tail.Length == 0 ? null : tail;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }
}
