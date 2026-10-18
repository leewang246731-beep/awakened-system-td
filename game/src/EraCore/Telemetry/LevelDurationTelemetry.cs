using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.IO;

namespace EraCore.Telemetry;

// T-155 (F-006) 时长遥测（实测校准批 T-158 的前置）：每关/每局真实用时
// 自动追加写本地 JSONL，玩家零操作。纯逻辑件（D-01 零引擎依赖；墙钟经
// IClock 注入 CS-1.4；文件经 IFileStore 注入根 D-03——发布件任意盘纪律，
// 路径永远是相对路径，根由宿主注入）。
//
// 双口径裁定（REV-20261018-T155 §2，依据=AGENTS 纪律 9 确定性底线）：
//   主口径  PlayTicks = 本坐会话内模拟实际步进的 tick 数（EndTick−StartTick，
//           K-1 30Hz 固定 tick 域）——同 seed+命令流可复算，暂停期不步进故
//           天然扣除，断点重进只计重进后的步进（Resumed=true 行可拼接还原
//           整关）。与 T-140 程序关时长口径（通关刻 tick/30/60）同源可直接
//           对账，T-085 估算器校准读的就是这一列。
//   参考口径 WallMs = EndMs−StartMs（真实墙钟差，含暂停/挂机）——仅供
//           真人节奏参考，不可复算，不参与任何游戏逻辑。
//
// 挂点（零侵入，均非 sim 热路径）：战役关与程序关共用
// CampaignLoop.LevelSession（开始=会话建立/重进，结束=OnVictory/
// DeclareDefeat/SaveAndExitToMenu 三个既有结算钩）；无尽局由表现层
// EndlessTelemetry 组合件在宿主入口调用（BeginRun/Wave表台账/终局
// 三处——EndlessEntry/EndlessRun 自身零改动）。headless 脚本局经 Source 字段分账
// （"script"），供 T-158 比对机器 vs 真人比率。

/// <summary>关卡类型（六类覆盖面的机读列）：战役关（E1~E6 全部）/程序关/无尽局。</summary>
public enum TelemetryLevelKind
{
    /// <summary>战役关（game/content/levels/e1~e6.json 的关卡行，含终章）。</summary>
    Campaign = 0,

    /// <summary>程序关（T-140 Procedural 域生成关卡）。</summary>
    Procedural = 1,

    /// <summary>无尽局（T-118/T-143 Endless 域整局）。</summary>
    Endless = 2,
}

/// <summary>一关/一局的终局结果。</summary>
public enum TelemetryOutcome
{
    /// <summary>胜利（战役/程序关：胜波达成且场清）。</summary>
    Victory = 0,

    /// <summary>失败（战役/程序关：DeclareDefeat；无尽：死亡终局——无尽无胜利终态）。</summary>
    Defeat = 1,

    /// <summary>弃局（本坐中途退出 SaveAndExitToMenu/Abandon；可续的诚实终态）。</summary>
    Abandoned = 2,
}

/// <summary>数据来源分账（T-158 机器 vs 真人比率的判别列）。</summary>
public static class TelemetrySources
{
    /// <summary>真人游玩局（默认）。</summary>
    public const string Player = "player";

    /// <summary>headless 自动局/脚本局（同样入账——命令流驱动的非玩家局）。</summary>
    public const string Script = "script";
}

/// <summary>
/// One telemetry subject declaration (host-supplied identity facts of the
/// level/run being timed): the level kind (六类覆盖面), the level id (or the
/// endless sentinel), the era index, the run seed and the source ledger
/// column. Pure data — the recorder derives every duration fact itself.
/// </summary>
/// <param name="Kind">关卡类型（战役/程序/无尽）。</param>
/// <param name="LevelId">关卡域 id（战役/程序关=关卡 id；无尽=<see cref="EndlessId"/> 哨兵）。</param>
/// <param name="EraIndex">时代（战役=E1~E6 的时代序；无尽=开局时代序；≥1）。</param>
/// <param name="Seed">Run seed（replay key #1——遥测行可对回放）。</param>
/// <param name="Source">来源列（<see cref="TelemetrySources"/>；缺省真人局）。</param>
public sealed record LevelTelemetryScope(
    TelemetryLevelKind Kind,
    string LevelId,
    int EraIndex,
    long Seed,
    string Source = TelemetrySources.Player)
{
    /// <summary>无尽局的 levelId 哨兵（无尽无关卡域 id，seed+开局时代才是身份）。</summary>
    public const string EndlessId = "endless";
}

/// <summary>
/// One JSONL line of the durations ledger (T-155, schema v1): one sitting of
/// one level/run — identity columns, the terminal outcome, the dual
/// duration columns (tick 主口径 + wall 参考口径) and the source column.
/// Immutable record; serialized through <see cref="TelemetryJsonContext"/>
/// (AOT red line D-05: source-generated metadata only).
/// </summary>
/// <param name="Schema">行格式版本（<see cref="LevelDurationTelemetry.CurrentSchema"/>；读侧按此路由）。</param>
/// <param name="Kind">关卡类型（枚举名字符串）。</param>
/// <param name="LevelId">关卡域 id（无尽=endless 哨兵）。</param>
/// <param name="EraIndex">时代序（≥1）。</param>
/// <param name="Seed">Run seed。</param>
/// <param name="Outcome">终局结果（victory/defeat/abandoned）。</param>
/// <param name="Source">来源列（player/script）。</param>
/// <param name="Resumed">本坐是否为断点重进（同 levelId+seed 的相邻行可拼接还原整关）。</param>
/// <param name="StartTick">开始 tick（本坐基线；重进局=退出刻）。</param>
/// <param name="EndTick">结束 tick（战役/程序关=终局刻；无尽=波表 tick 台账）。</param>
/// <param name="PlayTicks">纯游玩 tick 主口径（EndTick−StartTick；暂停天然扣除）。</param>
/// <param name="StartMs">开始墙钟 Unix 毫秒（未注入墙钟=0）。</param>
/// <param name="EndMs">结束墙钟 Unix 毫秒（未注入墙钟=0）。</param>
/// <param name="WallMs">墙钟差参考口径（EndMs−StartMs；含暂停/挂机；墙钟回拨钳 0）。</param>
public sealed record LevelDurationRecord(
    int Schema,
    TelemetryLevelKind Kind,
    string LevelId,
    int EraIndex,
    long Seed,
    TelemetryOutcome Outcome,
    string Source,
    bool Resumed,
    long StartTick,
    long EndTick,
    long PlayTicks,
    long StartMs,
    long EndMs,
    long WallMs);

/// <summary>
/// System.Text.Json source-generation context for the durations ledger
/// (D-05 AOT red line: no reflection serialization). Enums as names, one
/// compact UTF-8 line per record — same conventions as the K-8 journal
/// context (<c>PipelineJsonContext</c>).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LevelDurationRecord))]
public sealed partial class TelemetryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The duration telemetry recorder (T-155): hands out one
/// <see cref="LevelDurationTracker"/> per sitting via <see cref="Begin"/>
/// and owns the JSONL append path — every finished tracker appends exactly
/// one LF-terminated line to <c>&lt;injected root&gt;/telemetry/
/// durations.jsonl</c> (players do nothing; headless script runs record the
/// same way with <see cref="TelemetrySources.Script"/>). The file is
/// cumulative across runs (append-only ledger, never truncated); line
/// endings are pinned to LF so the same history is byte-stable on every
/// platform (K-8 journal discipline).
/// </summary>
public sealed class LevelDurationTelemetry
{
    /// <summary>
    /// Default ledger path relative to the injected <see cref="IFileStore"/>
    /// root (D-03: dev = data 根, release = exe 旁注入根, Web = browser FS —
    /// 发布件任意盘纪律：本类永不含绝对路径)。
    /// </summary>
    public const string DefaultRelativePath = "telemetry/durations.jsonl";

    /// <summary>Current ledger line schema version.</summary>
    public const int CurrentSchema = 1;

    private readonly IFileStore _files;
    private readonly IClock? _wallClock;
    private readonly string _relativePath;
    private readonly object _gate = new();

    /// <param name="files">Root-injected file access (D-03); owns every path.</param>
    /// <param name="wallClock">
    /// Optional wall-clock source (CS-1.4: wall time enters EraCore only
    /// through <see cref="IClock.NowMs"/>). Must report Unix-epoch
    /// milliseconds (the LlmGateway call-log precedent); null keeps the wall
    /// columns at 0 — the tick 主口径 records either way.
    /// </param>
    /// <param name="relativePath">
    /// Ledger path override (null = <see cref="DefaultRelativePath"/>; a
    /// non-null blank is rejected — an explicit empty path is a bug).
    /// </param>
    /// <exception cref="ArgumentNullException">files is null.</exception>
    /// <exception cref="ArgumentException">relativePath is blank (non-null).</exception>
    public LevelDurationTelemetry(IFileStore files, IClock? wallClock = null, string? relativePath = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (relativePath is not null && string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A relative ledger path is required.", nameof(relativePath));
        }

        _wallClock = wallClock;
        _relativePath = relativePath ?? DefaultRelativePath;
    }

    /// <summary>Ledger path relative to the injected root.</summary>
    public string RelativePath => _relativePath;

    /// <summary>Wall-clock now in Unix ms through the injected clock (0 when absent).</summary>
    internal long WallNowMs() => _wallClock?.NowMs ?? 0;

    /// <summary>
    /// Starts timing one sitting of the scoped level/run: freezes the tick
    /// baseline and the wall-clock start. The tracker appends its line when
    /// the sitting ends (victory/defeat/abandoned) — one Begin = at most one
    /// ledger line.
    /// </summary>
    /// <param name="scope">Host-declared identity of the level/run (六类覆盖面的判别列).</param>
    /// <param name="startTick">Tick baseline of this sitting (fresh=0；重进=退出刻).</param>
    /// <param name="resumed">True when this sitting continues an earlier mid-level exit.</param>
    /// <exception cref="ArgumentNullException">scope is null.</exception>
    /// <exception cref="ArgumentException">Invalid scope (blank level id, era below 1, blank source).</exception>
    public LevelDurationTracker Begin(LevelTelemetryScope scope, long startTick, bool resumed = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.LevelId))
        {
            throw new ArgumentException("A level id is required.", nameof(scope));
        }

        if (scope.EraIndex < 1)
        {
            throw new ArgumentException($"Era index must be at least 1 (got {scope.EraIndex}).", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(scope.Source))
        {
            throw new ArgumentException("A source column is required.", nameof(scope));
        }

        if (startTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startTick), startTick, "Ticks are non-negative.");
        }

        return new LevelDurationTracker(this, scope, startTick, resumed, _wallClock?.NowMs ?? 0);
    }

    /// <summary>
    /// Appends one finished record as exactly one LF-terminated UTF-8 line.
    /// The append is read-modify-write under a gate (IFileStore has no
    /// append API; one line per sitting makes the race window irrelevant);
    /// a pre-existing file missing its trailing newline is repaired before
    /// the new line so line counts never smear.
    /// </summary>
    /// <exception cref="ArgumentNullException">record is null.</exception>
    public void Append(LevelDurationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(
            record, TelemetryJsonContext.Default.LevelDurationRecord);

        lock (_gate)
        {
            byte[] existing = _files.Exists(_relativePath)
                ? _files.ReadAllBytes(_relativePath)
                : Array.Empty<byte>();
            bool repairNewline = existing.Length > 0 && existing[^1] != (byte)'\n';
            var buffer = new byte[existing.Length + (repairNewline ? 1 : 0) + line.Length + 1];
            existing.CopyTo(buffer, 0);
            int position = existing.Length;
            if (repairNewline)
            {
                buffer[position++] = (byte)'\n';
            }

            line.CopyTo(buffer, position);
            buffer[^1] = (byte)'\n';
            // 先写固定名滚动临时件再写正本（审查 W9 批 1 #2）：K-1 端口只有
            // Exists/Read/Write 无 Delete/改名，故 .tmp 不删除、每次追加时复写，
            // 常驻充当恢复副本——正本写到一半崩溃截断时，完整新账仍在 .tmp，
            // 恢复=覆盖回正本（统计性数据最坏丢尾部若干行，特此留档）。
            _files.WriteAllBytes(_relativePath + ".tmp", buffer);
            _files.WriteAllBytes(_relativePath, buffer);
        }
    }

    /// <summary>
    /// Reads the whole ledger back as ordered records (T-158 校准脚本的消费
    /// 入口). Blank lines are skipped; a malformed line fails loudly — a
    /// durations ledger that cannot be parsed is a bug, not noise (K-8
    /// journal read discipline). Missing file = empty ledger.
    /// </summary>
    /// <exception cref="ArgumentNullException">files or relativePath is null/blank.</exception>
    public static IReadOnlyList<LevelDurationRecord> ReadAll(
        IFileStore files, string relativePath = DefaultRelativePath)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A relative ledger path is required.", nameof(relativePath));
        }

        if (!files.Exists(relativePath))
        {
            return Array.Empty<LevelDurationRecord>();
        }

        var records = new List<LevelDurationRecord>();
        string text = Encoding.UTF8.GetString(files.ReadAllBytes(relativePath));
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            records.Add(
                JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.LevelDurationRecord)
                ?? throw new JsonException($"Durations ledger line deserialized to null: {line}"));
        }

        return records;
    }
}

/// <summary>
/// One sitting being timed (T-155): froze the scope, the tick baseline, the
/// resumed flag and the wall start at <see cref="LevelDurationTelemetry.Begin"/>;
/// <see cref="End"/> computes the dual duration columns and appends exactly
/// one ledger line. Double end is refused — one sitting yields at most one
/// line (the flow machine's single-terminal legality backs this on the
/// LevelSession hook).
/// </summary>
public sealed class LevelDurationTracker
{
    private readonly LevelDurationTelemetry _owner;
    private readonly LevelTelemetryScope _scope;
    private readonly long _startMs;

    internal LevelDurationTracker(
        LevelDurationTelemetry owner,
        LevelTelemetryScope scope,
        long startTick,
        bool resumed,
        long startMs)
    {
        _owner = owner;
        _scope = scope;
        _startMs = startMs;
        StartTick = startTick;
        Resumed = resumed;
    }

    /// <summary>The identity this sitting was declared with.</summary>
    public LevelTelemetryScope Scope => _scope;

    /// <summary>Tick baseline of this sitting (fresh=0；重进=退出刻).</summary>
    public long StartTick { get; }

    /// <summary>True when this sitting continues an earlier mid-level exit.</summary>
    public bool Resumed { get; }

    /// <summary>True once <see cref="End"/> appended the ledger line (terminal).</summary>
    public bool Ended { get; private set; }

    /// <summary>
    /// Ends the sitting: freezes the outcome, computes
    /// <c>PlayTicks = endTick − StartTick</c> (主口径) and the wall delta
    /// (参考口径; a backwards wall clock clamps to 0 — the raw columns stay
    /// in the line for audit) and appends the record.
    /// </summary>
    /// <param name="outcome">Terminal outcome of the sitting.</param>
    /// <param name="endTick">
    /// Tick at the terminal (战役/程序关=终局刻的 <c>Sim.CurrentTick</c>；
    /// 无尽=<see cref="EndlessTelemetry.WaveLedgerTicks"/> 波表台账).
    /// </param>
    /// <exception cref="InvalidOperationException">Already ended.</exception>
    /// <exception cref="ArgumentOutOfRangeException">endTick below StartTick.</exception>
    public LevelDurationRecord End(TelemetryOutcome outcome, long endTick)
    {
        if (Ended)
        {
            throw new InvalidOperationException("This sitting already recorded its ledger line.");
        }

        if (endTick < StartTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endTick), endTick, $"End tick cannot precede the start tick {StartTick}.");
        }

        long endMs = _owner.WallNowMs();
        var record = new LevelDurationRecord(
            LevelDurationTelemetry.CurrentSchema,
            _scope.Kind,
            _scope.LevelId,
            _scope.EraIndex,
            _scope.Seed,
            outcome,
            _scope.Source,
            Resumed,
            StartTick,
            endTick,
            endTick - StartTick,
            _startMs,
            endMs,
            endMs >= _startMs ? endMs - _startMs : 0);
        Ended = true;
        _owner.Append(record);
        return record;
    }
}

/// <summary>
/// Endless-domain telemetry binding (T-155 六类覆盖面的无尽挂点): the
/// Endless domain has no simulation clock of its own (wave planning +
/// tallies; the real-time pacing belongs to the host), so the duration
/// record is wired at ENTRY COMPOSITION instead of inside
/// <c>Endless.EndlessEntry.Begin</c> — the host calls <see cref="BeginRun"/>
/// when it starts a run, feeds every begun wave plan through
/// <see cref="WaveLedgerTicks"/> (the deterministic tick ledger:
/// Σ IntervalSeconds×30 of the waves actually begun), and closes the
/// tracker with <see cref="LevelDurationTracker.End"/> right after the
/// session settles (<c>Die</c> → <see cref="TelemetryOutcome.Defeat"/>)
/// or is walked away from (<c>Abandon</c> → Abandoned). Same dual-口径
/// ruling as the level domain: wave-ledger ticks are the deterministic
/// 主口径 (T-140 时长口径同源), wall ms the reference column.
/// </summary>
public static class EndlessTelemetry
{
    /// <summary>
    /// Starts timing one endless run: scope = kind Endless, the sentinel
    /// level id, the locked opening era and the run seed.
    /// </summary>
    /// <exception cref="ArgumentNullException">telemetry is null.</exception>
    public static LevelDurationTracker BeginRun(
        LevelDurationTelemetry telemetry,
        int startEraIndex,
        long seed,
        string source = TelemetrySources.Player) =>
        telemetry.Begin(
            new LevelTelemetryScope(TelemetryLevelKind.Endless, LevelTelemetryScope.EndlessId, startEraIndex, seed, source),
            startTick: 0);

    /// <summary>
    /// Deterministic tick contribution of one begun wave:
    /// <see cref="EndlessWavePlan.IntervalSeconds"/> × K-1 tick rate
    /// (AwayFromZero). The host accumulates this over the waves it drove —
    /// same seed + same wave stream ⇒ same ledger.
    /// </summary>
    public static long WaveLedgerTicks(EndlessWavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return (long)Math.Round(
            plan.IntervalSeconds * EraCore.Sim.SimClock.TicksPerSecond, MidpointRounding.AwayFromZero);
    }
}
