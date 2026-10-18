using System.Text.Json;
using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.IO;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Telemetry;

// [C-19][F-006] T-155 时长遥测记录器单元面：JSONL 追加语义/双口径（纯游玩
// tick 主口径+墙钟参考口径）/弃局记录/来源分账/发布件数据目录纪律（注入根
// 任意盘——路径相对、越界拒收）。挂点链路（六类全覆盖）见
// LevelDurationTelemetryChainTests。
public class LevelDurationTelemetryTests : IDisposable
{
    // CS-3.8：临时数据入 .tmp-tests 沙盒（pinned dev 根下，gitignored），Dispose 清理。
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", "telemetry-unit", Guid.NewGuid().ToString("N"));

    private readonly MutableWallClock _wall = new();

    private DevFileStore Files() => new(_root);

    private LevelDurationTelemetry Recorder() => new(Files(), _wall);

    private static LevelTelemetryScope CampaignScope(
        string levelId = "campaign:level-e1-01",
        int era = 1,
        long seed = 77,
        string source = TelemetrySources.Player) =>
        new(TelemetryLevelKind.Campaign, levelId, era, seed, source);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- legal

    // [F-006] legal：一坐一行追加落盘（零操作自动记）；读回与记录全等
    [Fact]
    public void End_appends_exactly_one_line_per_sitting_and_reads_back()
    {
        LevelDurationTelemetry recorder = Recorder();
        var tracker = recorder.Begin(CampaignScope(), startTick: 0);
        _wall.NowMs = 1_000;
        LevelDurationRecord written = tracker.End(TelemetryOutcome.Victory, endTick: 9_000);

        var readBack = LevelDurationTelemetry.ReadAll(Files());
        LevelDurationRecord line = Assert.Single(readBack);
        Assert.Equal(written, line);
        Assert.Equal(LevelDurationTelemetry.CurrentSchema, line.Schema);
        Assert.Equal(TelemetryLevelKind.Campaign, line.Kind);
        Assert.Equal("campaign:level-e1-01", line.LevelId);
        Assert.Equal(TelemetryOutcome.Victory, line.Outcome);
    }

    // [F-006] legal：累计账本——两坐两行，行序=落盘序（追加不截断）
    [Fact]
    public void Ledger_accumulates_lines_across_sittings()
    {
        LevelDurationTelemetry recorder = Recorder();

        recorder.Begin(CampaignScope(), startTick: 0).End(TelemetryOutcome.Victory, 4_500);
        recorder.Begin(CampaignScope(), startTick: 0).End(TelemetryOutcome.Abandoned, 1_200);

        Assert.Equal(2, LevelDurationTelemetry.ReadAll(Files()).Count);
        // 物理行数=2（LF 结尾，一行一记录——JSONL 语义）
        string text = File.ReadAllText(Path.Combine(_root, LevelDurationTelemetry.DefaultRelativePath));
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\n", text);
    }

    // [F-006] legal：双口径——tick 主口径=tick 差（暂停天然扣除的口径基元），
    // 墙钟参考口径=注入墙钟差；两口径互不污染
    [Fact]
    public void Dual_columns_tick_delta_and_wall_delta_are_recorded_independently()
    {
        LevelDurationTelemetry recorder = Recorder();
        _wall.NowMs = 50_000;
        var tracker = recorder.Begin(CampaignScope(seed: 9), startTick: 300); // 重进局基线=退出刻 300

        _wall.NowMs = 50_000 + 250_000; // 墙钟走 250 s（含假想暂停 200 s）
        LevelDurationRecord line = tracker.End(TelemetryOutcome.Victory, endTick: 300 + 1_500); // 只步进 1500 tick=50 s

        Assert.Equal(1_500, line.PlayTicks); // 主口径只认步进 tick
        Assert.Equal(250_000, line.WallMs); // 参考口径照记墙钟差（含暂停）
        Assert.Equal(300, line.StartTick);
        Assert.Equal(1_800, line.EndTick);
    }

    // [F-006] legal：弃局终态入账（中途退出=Abandoned 诚实行）
    [Fact]
    public void Abandoned_outcome_is_recorded()
    {
        Recorder().Begin(CampaignScope(), 0).End(TelemetryOutcome.Abandoned, 600);

        LevelDurationRecord line = Assert.Single(LevelDurationTelemetry.ReadAll(Files()));
        Assert.Equal(TelemetryOutcome.Abandoned, line.Outcome);
        Assert.Equal(600, line.PlayTicks);
    }

    // [F-006] legal：来源分账——脚本局（headless 自动局）照记且 Source=script
    // （T-158 机器 vs 真人比率的判别列）
    [Fact]
    public void Script_source_column_separates_headless_runs()
    {
        Recorder().Begin(CampaignScope(source: TelemetrySources.Script), 0)
            .End(TelemetryOutcome.Victory, 2_700);

        LevelDurationRecord line = Assert.Single(LevelDurationTelemetry.ReadAll(Files()));
        Assert.Equal(TelemetrySources.Script, line.Source);
    }

    // [F-006] legal：无注入墙钟=墙钟三列置 0（tick 主口径不受影响——确定性
    // 友好：缺钟也能记）
    [Fact]
    public void Missing_wall_clock_zeroes_only_the_wall_columns()
    {
        var recorder = new LevelDurationTelemetry(Files());
        LevelDurationRecord line = recorder.Begin(CampaignScope(), 0)
            .End(TelemetryOutcome.Defeat, 8_100);

        Assert.Equal(0, line.StartMs);
        Assert.Equal(0, line.EndMs);
        Assert.Equal(0, line.WallMs);
        Assert.Equal(8_100, line.PlayTicks);
    }

    // [F-006] legal：JSONL 行是合法单行 JSON 且枚举落名为字符串（T-158 消费
    // 侧的格式契约）
    [Fact]
    public void Line_serializes_as_single_json_object_with_string_enums()
    {
        Recorder().Begin(CampaignScope(), 0).End(TelemetryOutcome.Victory, 100);

        string text = File.ReadAllText(Path.Combine(_root, LevelDurationTelemetry.DefaultRelativePath));
        using var doc = JsonDocument.Parse(text.TrimEnd('\n'));
        Assert.Equal("Campaign", doc.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("Victory", doc.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("player", doc.RootElement.GetProperty("Source").GetString());
    }

    // [F-006] legal：发布件数据目录纪律——文件落在注入根的 telemetry/
    // durations.jsonl（相对路径；任意注入盘皆然，无绝对路径字面）
    [Fact]
    public void Ledger_lands_under_the_injected_root_only()
    {
        Recorder().Begin(CampaignScope(), 0).End(TelemetryOutcome.Victory, 100);

        string expected = Path.Combine(_root, "telemetry", "durations.jsonl");
        Assert.True(File.Exists(expected), $"遥测账本应落在注入根下：{expected}");

        // 注入根隔离性：另一注入根的记录器读不到本根数据（根注入=物理分界）
        var otherRoot = Path.Combine(_root, "other-root");
        Assert.Empty(LevelDurationTelemetry.ReadAll(new DevFileStore(otherRoot)));
    }

    // [F-006] legal：残缺尾行修复——无尾换行的既有文件先补 LF 再追加，行数
    // 不糊（外部编辑过的账本不产生拼接脏行）
    [Fact]
    public void Append_repairs_a_missing_trailing_newline()
    {
        var files = Files();
        var recorder = new LevelDurationTelemetry(files, _wall);
        recorder.Begin(CampaignScope(), 0).End(TelemetryOutcome.Victory, 100);
        string path = Path.Combine(_root, LevelDurationTelemetry.DefaultRelativePath);
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\n')); // 制造残缺尾行

        recorder.Begin(CampaignScope(), 0).End(TelemetryOutcome.Defeat, 200);

        Assert.Equal(2, LevelDurationTelemetry.ReadAll(files).Count);
    }

    // [F-006] legal：LevelSession 挂点——注入服务+声明 scope 的会话在失败终局
    // 自动落一行（玩家零操作面；config 无声明/无服务=零写入）
    [Fact]
    public void LevelSession_with_telemetry_writes_on_defeat_and_stays_silent_without_scope()
    {
        var session = new LevelSession(
            new LevelSessionConfig(SimE1Fixture.Config(), "telemetry.e1", 0.0, SimE1Fixture.Seed)
            {
                Telemetry = CampaignScope("telemetry.e1"),
            },
            new SimRng(SimE1Fixture.Seed),
            new EraCore.Tests.Endless.InMemorySavePort(),
            durationTelemetry: Recorder());
        session.Machine.StartLevel();
        session.RunFor(10);
        session.DeclareDefeat(firstDefeat: true);

        LevelDurationRecord line = Assert.Single(LevelDurationTelemetry.ReadAll(Files()));
        Assert.Equal(TelemetryOutcome.Defeat, line.Outcome);
        Assert.Equal("telemetry.e1", line.LevelId);
        Assert.Equal(10, line.PlayTicks);

        // 对照：无 scope 声明的既有会话（存量调用面）零写入——缺省关死
        var silent = new LevelSession(
            new LevelSessionConfig(SimE1Fixture.Config(), "telemetry.silent", 0.0, SimE1Fixture.Seed),
            new SimRng(SimE1Fixture.Seed),
            new EraCore.Tests.Endless.InMemorySavePort(),
            durationTelemetry: Recorder());
        silent.Machine.StartLevel();
        silent.RunFor(5);
        silent.DeclareDefeat(firstDefeat: true);
        Assert.Single(LevelDurationTelemetry.ReadAll(Files()));
    }

    // ------------------------------------------------------------- boundary

    // [F-006] boundary：零 tick 终局（瞬间弃局）合法——PlayTicks=0 是真实读数
    [Fact]
    public void Zero_length_sitting_is_a_legal_zero_tick_record()
    {
        LevelDurationRecord line = Recorder().Begin(CampaignScope(), 0)
            .End(TelemetryOutcome.Abandoned, 0);

        Assert.Equal(0, line.PlayTicks);
        Assert.NotNull(Assert.Single(LevelDurationTelemetry.ReadAll(Files())));
    }

    // [F-006] boundary：墙钟回拨钳 0（原始列保留供审计）
    [Fact]
    public void Backwards_wall_clock_clamps_wall_delta_to_zero()
    {
        _wall.NowMs = 90_000;
        var tracker = Recorder().Begin(CampaignScope(), 0);
        _wall.NowMs = 40_000; // 回拨 50 s

        LevelDurationRecord line = tracker.End(TelemetryOutcome.Victory, 100);

        Assert.Equal(0, line.WallMs);
        Assert.Equal(90_000, line.StartMs);
        Assert.Equal(40_000, line.EndMs);
    }

    // [F-006] boundary：读侧缺文件=空账本（首玩玩家无账本是常态不是错误）
    [Fact]
    public void ReadAll_of_a_missing_ledger_is_empty()
    {
        Assert.Empty(LevelDurationTelemetry.ReadAll(Files()));
    }

    // ------------------------------------------------------------- illegal

    // [F-006] illegal：一坐双终局拒绝（一行=一坐的单 terminal 语义）
    [Fact]
    public void Double_end_is_refused()
    {
        var tracker = Recorder().Begin(CampaignScope(), 0);
        tracker.End(TelemetryOutcome.Victory, 100);

        Assert.Throws<InvalidOperationException>(() => tracker.End(TelemetryOutcome.Defeat, 200));
        Assert.Single(LevelDurationTelemetry.ReadAll(Files()));
    }

    // [F-006] illegal：endTick 早于基线拒绝（时钟单调性口径）
    [Fact]
    public void End_tick_below_the_baseline_is_refused()
    {
        var tracker = Recorder().Begin(CampaignScope(), startTick: 500);

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.End(TelemetryOutcome.Victory, 499));
        Assert.Empty(LevelDurationTelemetry.ReadAll(Files())); // 拒绝=零写入
    }

    // [F-006] illegal：空白 levelId / 时代序 <1 / 空白来源拒绝（身份列是判别面）
    [Fact]
    public void Invalid_scopes_are_refused()
    {
        LevelDurationTelemetry recorder = Recorder();
        Assert.Throws<ArgumentException>(
            () => recorder.Begin(new LevelTelemetryScope(TelemetryLevelKind.Campaign, "  ", 1, 1), 0));
        Assert.Throws<ArgumentException>(
            () => recorder.Begin(new LevelTelemetryScope(TelemetryLevelKind.Campaign, "x", 0, 1), 0));
        Assert.Throws<ArgumentException>(
            () => recorder.Begin(new LevelTelemetryScope(TelemetryLevelKind.Endless, "endless", 1, 1, " "), 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => recorder.Begin(CampaignScope(), startTick: -1));
        Assert.Empty(LevelDurationTelemetry.ReadAll(Files()));
    }

    // [F-006] illegal：账本路径越出注入根拒收（发布件任意盘纪律——D-03 越界
    // 即 UnauthorizedAccessException，与存档面同口径）
    [Fact]
    public void Ledger_path_escaping_the_injected_root_is_rejected()
    {
        var recorder = new LevelDurationTelemetry(Files(), _wall, relativePath: "../outside.json");

        var tracker = recorder.Begin(CampaignScope(), 0);
        Assert.Throws<UnauthorizedAccessException>(() => tracker.End(TelemetryOutcome.Victory, 100));
    }

    // [F-006] illegal：坏行读侧拒绝（账本不可解析=bug 不是噪声——K-8 读纪律）
    [Fact]
    public void Malformed_ledger_line_fails_loudly_on_read()
    {
        var files = Files();
        files.WriteAllBytes(LevelDurationTelemetry.DefaultRelativePath, "not-json\n"u8.ToArray());

        Assert.Throws<JsonException>(() => LevelDurationTelemetry.ReadAll(files));
    }

    /// <summary>可拨墙钟（CS-1.4 端口 Fake——遥测墙钟参考列的时间源）。</summary>
    private sealed class MutableWallClock : IClock
    {
        public long Tick { get; set; }

        public long NowMs { get; set; }
    }
}
