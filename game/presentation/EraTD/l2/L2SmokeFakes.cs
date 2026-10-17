using EraCore.Llm.L2;
using Godot;

namespace EraTD.L2;

// T-137 G4 冒烟支撑（presentation 侧共享假件）：假进程/假探针/假填充口/
// 拒绝 wire——供 SettingsPanelUi 与 Main 的 headless 冒烟复用。绝不真拉
// llama-server、绝不下真模型（验收纪律：G4 不得依赖真模型/真进程）。

/// <summary>Fake server process: spawn flips a flag, Kill clears it; never touches the OS.</summary>
public sealed class SmokeServerProcess : ILlamaServerProcess
{
    /// <summary>True between Start and Kill/ExitNow.</summary>
    public bool Running { get; private set; }

    /// <summary>Force the exited state (process-death smoke lane).</summary>
    public void ExitNow()
    {
        Running = false;
        ForcedExit = true;
    }

    /// <summary>True after <see cref="ExitNow"/> (distinguishes death from never-started).</summary>
    public bool ForcedExit { get; private set; }

    /// <inheritdoc />
    public bool Start(string executablePath, string[] arguments, out string error)
    {
        Running = true;
        ForcedExit = false;
        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public bool HasExited => !Running;

    /// <inheritdoc />
    public int ExitCode => 1;

    /// <inheritdoc />
    public void Kill() => Running = false;

    /// <inheritdoc />
    public void Dispose() => Kill();
}

/// <summary>Scripted /health probe: healthy after <paramref name="failFirst"/> failures.</summary>
public sealed class SmokeHealthProbe(int failFirst = 0) : IL2HealthProbePort
{
    private int _calls;

    /// <summary>Probe calls so far (smoke assertion surface).</summary>
    public int Calls => _calls;

    /// <inheritdoc />
    public Task<bool> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        _calls++;
        return Task.FromResult(_calls > failFirst);
    }
}

/// <summary>Scripted fill port: serves queued results synchronously or hangs on demand.</summary>
public sealed class SmokeFillPort : IL2IntentFillPort
{
    private readonly Queue<L2IntentFillResult> _queued = [];

    /// <summary>Queue one result (served FIFO; an empty queue hangs forever — timeout lanes).</summary>
    public void Enqueue(L2IntentFillResult result) => _queued.Enqueue(result);

    /// <inheritdoc />
    public Task<L2IntentFillResult> FillAsync(string orderText, CancellationToken cancellationToken) =>
        _queued.Count > 0
            ? Task.FromResult(_queued.Dequeue())
            : new TaskCompletionSource<L2IntentFillResult>().Task; // hangs → deadline lane
}

/// <summary>Wire port that must never be reached (any call fails the smoke loudly).</summary>
public sealed class UnreachableDownloadWirePort : IL2DownloadWirePort
{
    /// <summary>Call count (smoke asserts zero).</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<L2RangeReply> FetchRangeAsync(
        L2Artifact artifact, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("smoke wire must never be called (no real downloads in G4)");
    }

    /// <inheritdoc />
    public Task<long> ProbeLengthAsync(L2Artifact artifact, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("smoke wire must never be called (no real downloads in G4)");
    }
}
