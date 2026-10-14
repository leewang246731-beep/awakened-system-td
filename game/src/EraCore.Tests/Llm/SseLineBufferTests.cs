using System.Text;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// SSE 行缓冲（半行拼接）单元测试：字节分片任意切入、CRLF、UTF-8 多字节
/// 半字符、BOM、EOF 尾行。
/// </summary>
// [F-086][F-162] C-16 T-072：SSE 行缓冲内核（方案 §11.7）
public class SseLineBufferTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static IReadOnlyList<string> Drain(params string[] chunks)
    {
        var buffer = new SseLineBuffer();
        var lines = new List<string>();
        foreach (var chunk in chunks)
        {
            lines.AddRange(buffer.Feed(Utf8(chunk)));
        }

        return lines;
    }

    [Fact]
    public void Whole_lines_in_one_chunk_come_back_in_order()
    {
        // legal: 一次到达的完整帧
        var lines = Drain("data: a\n\ndata: b\n\n");

        Assert.Equal(new[] { "data: a", string.Empty, "data: b", string.Empty }, lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void Split_chunks_reassemble_half_lines_exactly_once(int chunkSize)
    {
        // boundary: 半行在任意字节边界切开
        var payload = "data: {\"choices\":[{\"delta\":{\"content\":\"在东门建两座弩塔\"}}]}\n\ndata: [DONE]\n\n";
        var buffer = new SseLineBuffer();
        var lines = new List<string>();
        var bytes = Utf8(payload);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, bytes.Length - offset);
            lines.AddRange(buffer.Feed(bytes.AsSpan(offset, take)));
        }

        Assert.Equal(
            new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"在东门建两座弩塔\"}}]}", string.Empty, "data: [DONE]", string.Empty },
            lines);
    }

    [Fact]
    public void Crlf_line_endings_strip_the_carriage_return()
    {
        // boundary: CRLF 服务器
        var lines = Drain("data: x\r\n\r\n");

        Assert.Equal(new[] { "data: x", string.Empty }, lines);
    }

    [Fact]
    public void Utf8_character_split_across_chunks_decodes_once()
    {
        // boundary: 多字节字符恰好切成两半（东 = 3 字节）
        var bytes = Utf8("data: 东\n");
        var buffer = new SseLineBuffer();
        var head = buffer.Feed(bytes.AsSpan(0, 7));
        var tail = buffer.Feed(bytes.AsSpan(7));

        Assert.Empty(head);
        var line = Assert.Single(tail);
        Assert.Equal("data: 东", line);
    }

    [Fact]
    public void Flush_returns_unterminated_tail_and_empties_the_buffer()
    {
        // boundary: EOF 无换行尾行 + 二次 Flush 幂等
        var buffer = new SseLineBuffer();
        Assert.Empty(buffer.Feed(Utf8("data: tail")));
        Assert.Equal("data: tail", buffer.Flush());
        Assert.Null(buffer.Flush());
    }

    [Fact]
    public void Leading_bom_is_dropped_and_never_becomes_a_line()
    {
        // illegal: BOM 开头的脏流
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var buffer = new SseLineBuffer();
        var lines = new List<string>();
        lines.AddRange(buffer.Feed(bom));
        lines.AddRange(buffer.Feed(Utf8("data: ok\n")));

        var line = Assert.Single(lines);
        Assert.Equal("data: ok", line);
    }

    [Fact]
    public void Invalid_utf8_does_not_throw_and_yields_replacement_chars()
    {
        // illegal: 非法字节序列（截断的 UTF-8）
        var buffer = new SseLineBuffer();
        var lines = buffer.Feed(new byte[] { 0xF0, 0x9F, (byte)'\n' });

        var line = Assert.Single(lines);
        Assert.Contains('\uFFFD', line);
    }

    [Fact]
    public void Empty_chunk_yields_no_lines()
    {
        // boundary: 空读（keep-alive 零字节）
        Assert.Empty(new SseLineBuffer().Feed([]));
    }
}
