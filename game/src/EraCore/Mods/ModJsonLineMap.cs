using System.Text;
using System.Text.Json;

namespace EraCore.Mods;

/// <summary>
/// Byte-offset → 1-based-line resolver for mod content files (T-103, F-165).
/// One <see cref="Utf8JsonReader"/> pre-pass records the byte offset
/// (<see cref="Utf8JsonReader.TokenStartIndex"/>) of every value token under
/// its JSON pointer ("" root, "/items", "/items/2", "/items/2/dps", …); a
/// second linear scan of newline positions resolves each offset to a 1-based
/// source line — the same technique as the K-3 validator's line index
/// (T-051, <c>JsonSchemaValidator.JsonLineIndex</c>), factored out so the
/// C-02 mod loader can cite <c>file:line</c> for every structured load error
/// (坏模组弹错不崩 + 行级报错, v2.1 §16 item 3 / recovered architecture
/// G.1 item 3). Malformed JSON keeps whatever was indexed before the parse
/// error — the caller reports the parser's own line for that case.
/// </summary>
public static class ModJsonLineMap
{
    /// <summary>
    /// Indexes <paramref name="json"/>: pointer → 1-based line of the value
    /// token. Pointers use the standard RFC 6901 shape with array indices as
    /// path segments (<c>/items/3/traits</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, int> Index(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(json),
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (reader.Read())
            {
                IndexValue(ref reader, offsets, string.Empty);
            }
        }
        catch (JsonException)
        {
            // Malformed payloads: keep the tokens indexed so far; the
            // JsonDocument parse error (with its own line) is reported first.
        }

        // Resolve byte offsets to 1-based lines: line 1 starts at byte 0,
        // every '\n' opens the next line (linear scan once).
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var lineStarts = new List<long> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lineStarts.Add(i + 1L);
            }
        }

        var lines = new Dictionary<string, int>(offsets.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, long> entry in offsets)
        {
            int line = lineStarts.BinarySearch(entry.Value);
            if (line < 0)
            {
                line = ~line - 1; // last line start <= offset
            }

            lines[entry.Key] = line + 1;
        }

        return lines;
    }

    /// <summary>
    /// Line of one pointer, 0 when unknown (never indexed / malformed before
    /// the token). 0 mirrors the K-3 contract for unknown positions.
    /// </summary>
    public static int LineOf(IReadOnlyDictionary<string, int> lines, string pointer) =>
        lines.TryGetValue(pointer, out int line) ? line : 0;

    private static void IndexValue(ref Utf8JsonReader reader, Dictionary<string, long> offsets, string pointer)
    {
        long offset = reader.TokenStartIndex;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                if (!offsets.ContainsKey(pointer))
                {
                    offsets[pointer] = offset;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString() ?? string.Empty;
                    string childPointer = pointer + "/" + name;
                    offsets[childPointer] = reader.TokenStartIndex;
                    if (!reader.Read())
                    {
                        return;
                    }

                    IndexValue(ref reader, offsets, childPointer);
                }

                break;

            case JsonTokenType.StartArray:
                if (!offsets.ContainsKey(pointer))
                {
                    offsets[pointer] = offset;
                }

                int index = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    string childPointer = pointer + "/" + index;
                    offsets[childPointer] = reader.TokenStartIndex;
                    IndexValue(ref reader, offsets, childPointer);
                    index++;
                }

                break;

            default:
                offsets[pointer] = offset;
                break;
        }
    }
}
