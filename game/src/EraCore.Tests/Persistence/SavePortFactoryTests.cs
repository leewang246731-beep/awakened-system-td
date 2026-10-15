using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.IO;
using EraCore.Persistence;
using EraCore.Tests.Secrets;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束：C-04 存档后端选择（T-100 / 07 D-32 / R-E2）——运行时
// 探测 e_sqlite3 可用性，失败回落 JSON 并经 ILogSink 记录；ERA_SAVE_BACKEND
// 环境开关供 headless 桌面模拟"无 SQLite 的 Web 环境"。
public sealed class SavePortFactoryTests : IDisposable
{
    public SavePortFactoryTests()
    {
        Root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        Files = new DevFileStore(Root);
        _previousOverride = Environment.GetEnvironmentVariable(
            SavePortFactory.BackendOverrideVariable);
    }

    private string Root { get; }

    private DevFileStore Files { get; }

    private readonly string? _previousOverride;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            SavePortFactory.BackendOverrideVariable, _previousOverride);
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void SetOverride(string? value) =>
        Environment.SetEnvironmentVariable(SavePortFactory.BackendOverrideVariable, value);

    // ------------------------------------------------------------ legal

    [Fact]
    public void Factory_prefers_sqlite_when_the_real_probe_passes()
    {
        var sink = new CaptureLogSink();

        var port = SavePortFactory.Create(Files, sink);

        // 本开发机/CI（Windows，e_sqlite3 随包可用）：真探针通过 → SQLite。
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<SqliteSavePort>(port);
            Assert.Contains(sink.Lines, line =>
                line.Contains("backend=sqlite", StringComparison.Ordinal)
                && line.Contains("reason=native-e_sqlite3-available", StringComparison.Ordinal));

            // 端到端可用性：工厂产物直接完成一次真实往返。
            port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(31337), SaveVersion.Current);
            Assert.Equal(31337, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        }
        else
        {
            // 非 Windows 仍可能装载 e_sqlite3（SQLitePCLRaw 有 linux/osx 原生
            // 包）——探针说了算，不强断言平台，只断言选择与日志一致。
            Assert.Contains(sink.Lines, line =>
                line.Contains("factory=select backend=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Probe_failure_falls_back_to_json_with_a_logged_reason()
    {
        var sink = new CaptureLogSink();

        var port = SavePortFactory.Create(Files, sink, sqliteProbe: () => false);

        // 逻辑回落路径（全平台可跑）：e_sqlite3 装载失败 → JSON 后备 + Warn 原因。
        Assert.IsType<JsonFileSavePort>(port);
        Assert.Contains(sink.Lines, line =>
            line.Contains("backend=json-fallback", StringComparison.Ordinal)
            && line.Contains("reason=sqlite probe returned false", StringComparison.Ordinal));

        // 回落产物同样满足端口契约（存取可用）。
        port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current);
        Assert.Equal(10, port.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Backend_override_json_simulates_the_web_environment_under_headless()
    {
        // Web 场景模拟（任务卡 T-100）：headless 桌面上强制 JSON 的环境开关。
        SetOverride(SavePortFactory.JsonBackendValue);
        var sink = new CaptureLogSink();

        var port = SavePortFactory.Create(Files, sink);

        Assert.IsType<JsonFileSavePort>(port);
        Assert.Contains(sink.Lines, line =>
            line.Contains("backend=json", StringComparison.Ordinal)
            && line.Contains("reason=env-override", StringComparison.Ordinal));

        // 模拟出的"Web 环境"完整可用：campaign + level 双域往返。
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(600613), SaveVersion.Current);
        port.WriteLevel("level.e1.m01", SqliteSavePortTestBase.MarkerBlob(1, 1, 11), SaveVersion.Current);
        Assert.Equal(600613, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        Assert.Equal(11, port.ReadLevel("level.e1.m01", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Backend_override_sqlite_beats_a_failing_probe()
    {
        // 显式操作员覆盖优先于探针（Info 而非 Warn——这是刻意选择不是降级）。
        SetOverride(SavePortFactory.SqliteBackendValue);
        var sink = new CaptureLogSink();

        var port = SavePortFactory.Create(Files, sink, sqliteProbe: () => false);

        Assert.IsType<SqliteSavePort>(port);
        Assert.Contains(sink.Lines, line =>
            line.Contains("backend=sqlite", StringComparison.Ordinal)
            && line.Contains("reason=env-override", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Blank_override_value_falls_through_to_the_probe()
    {
        SetOverride("   ");
        var sink = new CaptureLogSink();

        var port = SavePortFactory.Create(Files, sink, sqliteProbe: () => false);

        Assert.IsType<JsonFileSavePort>(port);
        Assert.Contains(sink.Lines, line =>
            line.Contains("backend=json-fallback", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var sink = new CaptureLogSink();
        Assert.Throws<ArgumentNullException>(() => SavePortFactory.Create(null!, sink));
        Assert.Throws<ArgumentNullException>(() => SavePortFactory.Create(Files, null!));
    }

    [Fact]
    public void Unknown_override_value_fails_loudly()
    {
        // 拼错的开关值静默落到探针会把操作员意图吞掉——按项目文化大声失败。
        SetOverride("jsonn");
        var sink = new CaptureLogSink();

        var ex = Assert.Throws<ArgumentException>(
            () => SavePortFactory.Create(Files, sink));

        Assert.Contains(SavePortFactory.BackendOverrideVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("jsonn", ex.Message, StringComparison.Ordinal);
    }
}
