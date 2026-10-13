using System.Buffers.Binary;
using System.Text;

namespace EraCore.Secrets;

/// <summary>
/// Compact length-prefixed container for the provider→secret map used by
/// both <see cref="ISecretStore"/> implementations (C-17, T-054). Binary on
/// purpose: no JSON pretty-printing of key material, no reflection-based
/// serialization (D-05), one small parser to audit. Layout v1:
/// magic "ERASEC\0\0" (8 bytes) + version(4) + count(4) + per entry
/// idLen/id/valLen/val (little-endian int32 lengths, UTF-8 text). Corrupt
/// input fails loudly with <see cref="IOException"/> — a secrets file is
/// never "best-effort" parsed.
/// </summary>
public static class SecretsContainer
{
    /// <summary>File magic (exactly 8 bytes so the header stays 16-aligned).</summary>
    public static ReadOnlySpan<byte> Magic => "ERASEC\0\0"u8;

    /// <summary>Container format version.</summary>
    public const int Version = 1;

    /// <summary>Sanity bound that stops garbage headers from allocating wild sizes.</summary>
    public const int MaxEntries = 4096;

    /// <summary>Upper bound accepted for one encoded string (provider ids and keys stay far below).</summary>
    public const int MaxStringLength = 8 * 1024;

    /// <summary>
    /// Reads the container at <paramref name="relativePath"/> through the
    /// injected <see cref="EraCore.IO.IFileStore"/> root. A missing file is
    /// an empty map; anything present but unreadable throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(
        EraCore.IO.IFileStore files, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.Exists(relativePath))
        {
            return new Dictionary<string, string>();
        }

        return Decode(files.ReadAllBytes(relativePath));
    }

    /// <summary>
    /// Writes the map (even when empty — the D-03 port has no delete) so an
    /// empty container is a stable on-disk representation of "all cleared".
    /// </summary>
    public static void Write(
        EraCore.IO.IFileStore files, string relativePath,
        IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(entries);
        files.WriteAllBytes(relativePath, Encode(entries));
    }

    /// <summary>Encodes the map; deterministic order (sorted) for byte-stable output.</summary>
    public static byte[] Encode(IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var size = 8 + 4 + 4;
        foreach (var (id, value) in ordered)
        {
            size += 4 + Encoding.UTF8.GetByteCount(id) + 4 + Encoding.UTF8.GetByteCount(value);
        }

        var buffer = new byte[size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), ordered.Count);
        var offset = 16;
        foreach (var (id, value) in ordered)
        {
            offset = PutString(buffer, offset, id);
            offset = PutString(buffer, offset, value);
        }

        return buffer;
    }

    /// <summary>Decodes a container; throws <see cref="IOException"/> on any malformed byte.</summary>
    public static IReadOnlyDictionary<string, string> Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 16 || !Magic.SequenceEqual(bytes.AsSpan(0, 8)))
        {
            throw Corrupted("bad magic/header");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (version != Version)
        {
            throw Corrupted($"unsupported version {version}");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (count < 0 || count > MaxEntries)
        {
            throw Corrupted($"entry count {count} out of bounds");
        }

        var result = new Dictionary<string, string>(count, StringComparer.Ordinal);
        var offset = 16;
        for (var i = 0; i < count; i++)
        {
            var id = TakeString(bytes, ref offset);
            var value = TakeString(bytes, ref offset);
            if (!result.TryAdd(id, value))
            {
                throw Corrupted($"duplicate provider entry '{id}'");
            }
        }

        if (offset != bytes.Length)
        {
            throw Corrupted($"{bytes.Length - offset} trailing bytes");
        }

        return result;
    }

    private static int PutString(byte[] buffer, int offset, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), byteCount);
        Encoding.UTF8.GetBytes(value, buffer.AsSpan(offset + 4, byteCount));
        return offset + 4 + byteCount;
    }

    private static string TakeString(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length)
        {
            throw Corrupted("truncated length prefix");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (length < 0 || length > MaxStringLength)
        {
            throw Corrupted($"string length {length} out of bounds");
        }

        offset += 4;
        if (offset + length > bytes.Length)
        {
            throw Corrupted("truncated string body");
        }

        var value = Encoding.UTF8.GetString(bytes, offset, length);
        offset += length;
        return value;
    }

    private static IOException Corrupted(string reason) =>
        new($"Secrets container corrupted ({reason}); refusing to parse.");
}
