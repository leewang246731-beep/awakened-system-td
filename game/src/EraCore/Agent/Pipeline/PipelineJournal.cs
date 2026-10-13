using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Journal;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) JSONL 战报侧：五段管线审计载荷 + K-8 落盘写入器（F-069）。
//
// K-8 的 JournalEventKind 冻结为四类（Input/Plan/Validation/Report）——本
// 任务不碰契约（并行纪律：只写 Agent/Pipeline）。六个管线逻辑阶段
// （input/plan/approval/execution/report/clarify）映射进四类事件、由载荷
// 的 Type 字段判别：input→Input；plan+approval→Plan（契约注释本就规定
// Plan 事件携带"方案卡内容+审批结论"）；execution→Validation（动作执行的
// Schema/状态校验结果类）；report+clarify→Report。回放器按 Type 折叠出与
// 原运行一致的决策序列（F-121 的离线前提）。

/// <summary>Payload discriminator of the pipeline journal stages (see file remarks).</summary>
public static class PipelineJournalTypes
{
    /// <summary>Order text entering the pipeline.</summary>
    public const string Input = "input";

    /// <summary>Plan-card content: steps / point+resource cost / risks.</summary>
    public const string Plan = "plan";

    /// <summary>Stage-4 verdict of the approval gate (incl. plan-cost composition and player confirm/refuse).</summary>
    public const string Approval = "approval";

    /// <summary>One execution attempt of one plan step (Schema+state validation outcome, charge, retries).</summary>
    public const string Execution = "execution";

    /// <summary>Final report the player received.</summary>
    public const string Report = "report";

    /// <summary>Clarification card answer (parse failure / reject).</summary>
    public const string Clarify = "clarify";
}

/// <summary>Input-stage payload (Kind=Input).</summary>
public sealed record PipelineInputPayload(string Type, int Order, string OrderText);

/// <summary>Plan-stage payload (Kind=Plan): the plan card the player sees.</summary>
/// <param name="StepVerbs">Planned verbs in order.</param>
/// <param name="StepActionJson">Planned action JSON per step (report step carries the plan-time draft).</param>
/// <param name="StepPoints">System-point price per step (tariff base).</param>
/// <param name="StepResources">Cost-currency resource quote per step.</param>
public sealed record PipelinePlanPayload(
    string Type,
    int Order,
    string OrderText,
    string Intent,
    string Route,
    string[] StepVerbs,
    string[] StepActionJson,
    int[] StepPoints,
    double[] StepResources,
    int EstimatedPoints,
    double EstimatedResources,
    string[] Risks);

/// <summary>Approval-stage payload (Kind=Plan): gate verdict / cost-gate escalation / player decision.</summary>
public sealed record PipelineApprovalPayload(
    string Type,
    int Order,
    string OrderText,
    string Decision,
    string? Reason,
    bool PlanCostForcedAsk);

/// <summary>Execution-stage payload (Kind=Validation): one attempt of one step.</summary>
public sealed record PipelineExecutionPayload(
    string Type,
    int Order,
    string OrderText,
    int StepIndex,
    string Verb,
    string ActionJson,
    string Status,
    string? Error,
    int PointsSpent,
    int Attempt,
    bool SelfCorrected,
    string? RetryAdjustment);

/// <summary>Report-stage payload (Kind=Report).</summary>
/// <param name="Outcome">Terminal outcome of the order (completed/denied/approval_refused) — the replay derives it from here.</param>
public sealed record PipelineReportPayload(
    string Type,
    int Order,
    string OrderText,
    string Outcome,
    string Summary,
    string[] Findings,
    string Verdict);

/// <summary>Clarification-card payload (Kind=Report): T-052 clarifying key + quick wheel.</summary>
public sealed record PipelineClarifyPayload(
    string Type,
    int Order,
    string OrderText,
    string ClarifyingKey,
    string[] QuickWheel);

/// <summary>
/// System.Text.Json source-generation context for the C-13 pipeline payloads
/// and the K-8 journal envelope (D-05 AOT red line: no reflection
/// serialization). Enums as names, no indentation — one JSONL line per record.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalEvent))]
[JsonSerializable(typeof(PipelineInputPayload))]
[JsonSerializable(typeof(PipelinePlanPayload))]
[JsonSerializable(typeof(PipelineApprovalPayload))]
[JsonSerializable(typeof(PipelineExecutionPayload))]
[JsonSerializable(typeof(PipelineReportPayload))]
[JsonSerializable(typeof(PipelineClarifyPayload))]
public sealed partial class PipelineJsonContext : JsonSerializerContext
{
}

/// <summary>
/// K-8 JSONL journal writer (T-053, F-069): appends every
/// <see cref="JournalEvent"/> as exactly one UTF-8 line to the run file,
/// flushed before returning (append-only, synchronous, ordered — D-12). This
/// sink IS the war report (RL10: console output is never a substitute). Line
/// endings are pinned to LF so the same run hashes identically on every
/// platform (determinism gate).
/// </summary>
public sealed class JsonlJournalWriter : IJournalWriter, IDisposable
{
    private readonly string _path;
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>
    /// Creates (or truncates) the run journal file. Parent directories are
    /// created on demand; the file lives under a host-injected data root
    /// (D-03 — never a C-drive default).
    /// </summary>
    public JsonlJournalWriter(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _path = filePath;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false))
        {
            NewLine = "\n",
        };
    }

    /// <summary>The journal file path (one run = one file).</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public void Append(JournalEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(evt);
        _writer.WriteLine(JsonSerializer.Serialize(evt, PipelineJsonContext.Default.JournalEvent));
        _writer.Flush();
    }

    /// <summary>
    /// Reads a journal file back as ordered events (replayer input). The
    /// reader shares write access so a live run's writer keeps its handle.
    /// Blank trailing lines are skipped; a malformed line fails loudly — a
    /// war report that cannot be parsed is a bug, not noise.
    /// </summary>
    public static IReadOnlyList<JournalEvent> ReadAll(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var events = new List<JournalEvent>();
        using var reader = new StreamReader(
            new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            events.Add(
                JsonSerializer.Deserialize(line, PipelineJsonContext.Default.JournalEvent)
                ?? throw new JsonException($"Journal line deserialized to null: {line}"));
        }

        return events;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
    }
}

/// <summary>
/// Sequencing decorator over any K-8 sink (T-053): rewrites every event's
/// <see cref="JournalEvent.Seq"/> onto one strictly increasing counter so
/// pipeline events and the C-14 ledger's audit events share a single ordered
/// stream per run (the ledger owns its own counter — the decorator is the
/// rebinding seam the ledger docs deferred to C-13). Ticks pass through
/// unchanged.
/// </summary>
public sealed class SequencingJournalWriter : IJournalWriter
{
    private readonly IJournalWriter _inner;
    private long _seq;

    /// <param name="inner">The sink that stores the lines (e.g. <see cref="JsonlJournalWriter"/>).</param>
    /// <param name="startSeq">First sequence number (default 1).</param>
    public SequencingJournalWriter(IJournalWriter inner, long startSeq = 1)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _seq = startSeq - 1;
    }

    /// <summary>Sequence of the last appended event (0 before the first).</summary>
    public long LastSeq => _seq;

    /// <inheritdoc />
    public void Append(JournalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _seq++;
        _inner.Append(evt with { Seq = _seq });
    }
}
