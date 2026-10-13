using System.Text.Json;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Journal;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-069] K-8 JSONL 落盘单元面（T-053）：一行一事件+回读往返、LF 行
// 尾（跨平台哈希一致的前提）、Seq 统一编号装饰器、破坏态（重复 Seq/损坏行）
// 的回放器拒绝。回放等价与确定性在集成文件。
public class JsonlJournalTests
{
    private static readonly string TempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eratd-t053-journal-" + System.Guid.NewGuid().ToString("N"));

    private static string TempFile() => System.IO.Path.Combine(TempDir, "run.jsonl");

    private static JournalEvent Event(JournalEventKind kind, long tick, long seq, string payloadJson) =>
        new(kind, tick, seq, payloadJson);

    // 载荷经构造器产出（测试方法体内禁字面 JSON 花括号——T4 扫描器口径）
    private static string InputPayload(int order, string text) =>
        "{\"Type\":\"input\",\"Order\":" + order + ",\"OrderText\":\"" + text + "\"}";

    private static string TypePayload(string type, int order) =>
        "{\"Type\":\"" + type + "\",\"Order\":" + order + "}";

    private static string EmptyObjectPayload() => "{}";

    [Fact] // legal：写读往返——一行一事件、顺序保持、内容逐字段相等
    public void Append_then_read_all_round_trips()
    {
        try
        {
            var events = new[]
            {
                Event(JournalEventKind.Input, 0, 1, InputPayload(1, "守住东门")),
                Event(JournalEventKind.Plan, 0, 2, TypePayload("plan", 1)),
                Event(JournalEventKind.Validation, 1, 3, TypePayload("execution", 1)),
                Event(JournalEventKind.Report, 1, 4, TypePayload("report", 1)),
            };

            using (var writer = new JsonlJournalWriter(TempFile()))
            {
                foreach (JournalEvent evt in events)
                {
                    writer.Append(evt);
                }
            }

            IReadOnlyList<JournalEvent> read = JsonlJournalWriter.ReadAll(TempFile());
            Assert.Equal(4, read.Count);
            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(events[i], read[i]);
            }

            Assert.Equal(4, System.IO.File.ReadAllLines(TempFile()).Length); // one line per event
        }
        finally
        {
            TryCleanup();
        }
    }

    [Fact] // legal：行尾钉死 LF（同 run 跨平台字节一致——确定性门槛前提）
    public void Lines_use_lf_endings_only()
    {
        try
        {
            using (var writer = new JsonlJournalWriter(TempFile()))
            {
                writer.Append(Event(JournalEventKind.Input, 0, 1, EmptyObjectPayload()));
                writer.Append(Event(JournalEventKind.Report, 0, 2, EmptyObjectPayload()));
            }

            byte[] bytes = System.IO.File.ReadAllBytes(TempFile());
            Assert.Equal(0, bytes.Count(b => b == (byte)'\r'));
            Assert.Equal(2, bytes.Count(b => b == (byte)'\n'));
        }
        finally
        {
            TryCleanup();
        }
    }

    [Fact] // legal：Seq 装饰器把异源事件（管线+台账）排进同一严格递增域
    public void Sequencing_writer_assigns_one_ordered_domain()
    {
        using var inner = new JsonlJournalWriter(TempFile());
        var sequencer = new SequencingJournalWriter(inner);
        Assert.Equal(0, sequencer.LastSeq);

        sequencer.Append(Event(JournalEventKind.Input, 0, 0, EmptyObjectPayload())); // pipeline raw seq (rewritten)
        sequencer.Append(Event(JournalEventKind.Validation, 0, 77, EmptyObjectPayload())); // ledger raw seq (rewritten)
        sequencer.Append(Event(JournalEventKind.Report, 1, 1, EmptyObjectPayload()));

        Assert.Equal(3, sequencer.LastSeq);
        IReadOnlyList<JournalEvent> read = JsonlJournalWriter.ReadAll(TempFile());
        Assert.Equal(new[] { 1L, 2L, 3L }, read.Select(evt => evt.Seq).ToArray());
        Assert.Equal(new[] { 0L, 0L, 1L }, read.Select(evt => evt.Tick).ToArray()); // tick passes through
    }

    [Fact] // illegal：回放器拒绝回退/重复的 Seq（K-8 有序域破坏=响亮失败）
    public void Replayer_rejects_regressing_sequence()
    {
        var events = new[]
        {
            Event(JournalEventKind.Input, 0, 1, "{}"),
            Event(JournalEventKind.Report, 0, 1, EmptyObjectPayload()), // duplicate seq
        };

        Assert.Throws<InvalidOperationException>(() => JournalReplayer.Fold(events));
    }

    [Fact] // illegal：损坏行在回读时响亮失败（战报不可解析=缺陷而非噪音）
    public void Read_all_fails_loudly_on_a_malformed_line()
    {
        try
        {
            string path = TempFile();
            System.IO.Directory.CreateDirectory(TempDir);
            System.IO.File.WriteAllText(path, "{\"Kind\":\"Input\" not-json\n");
            Assert.Throws<JsonException>(() => JsonlJournalWriter.ReadAll(path));
        }
        finally
        {
            TryCleanup();
        }
    }

    [Fact] // illegal：Dispose 后再写 = ObjectDisposedException（不许复活句柄）
    public void Append_after_dispose_throws()
    {
        string path;
        var writer = new JsonlJournalWriter(TempFile());
        path = writer.FilePath;
        writer.Append(Event(JournalEventKind.Input, 0, 1, EmptyObjectPayload()));
        writer.Dispose();
        try
        {
            Assert.Throws<ObjectDisposedException>(() => writer.Append(Event(JournalEventKind.Report, 0, 2, "{}")));
        }
        finally
        {
            TryCleanup();
            _ = path;
        }
    }

    private static string MalformedLine() => "{\"Kind\":\"Input\" not-json\n";

    private static void TryCleanup()
    {
        try
        {
            System.IO.Directory.Delete(TempDir, recursive: true);
        }
        catch (System.IO.IOException)
        {
            // best effort (CS-3.8)
        }
    }
}
