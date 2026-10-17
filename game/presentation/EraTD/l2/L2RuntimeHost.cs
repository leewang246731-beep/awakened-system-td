using System.Diagnostics;
using EraCore.Llm.L2;
using Godot;

namespace EraTD.L2;

// T-137 (C-27/F-078) llama.cpp server 进程宿主（presentation 层）：启动/
// 健康探测（/health）/OpenAI 兼容端点预热（消 82.5s 冷首答——47-t129 §3.2①
// 强制配套）/停机清理。EraCore 侧零进程依赖（协议与状态机在
// EraCore.Llm.L2；反射守卫 LlmLayerGuardTests 保持绿）。进程口可注入——
// headless 冒烟用假进程，绝不真拉 llama-server（G4 纪律）。

/// <summary>Spawn/kill seam of the llama-server child process (injectable: real process vs smoke fake).</summary>
public interface ILlamaServerProcess : IDisposable
{
    /// <summary>Launches the executable. Returns false with an error line when the spawn fails.</summary>
    bool Start(string executablePath, string[] arguments, out string error);

    /// <summary>True when the process is no longer running.</summary>
    bool HasExited { get; }

    /// <summary>Exit code when <see cref="HasExited"/>.</summary>
    int ExitCode { get; }

    /// <summary>Kills the process tree (shutdown lane).</summary>
    void Kill();
}

/// <summary>Real <see cref="System.Diagnostics.Process"/> adapter (desktop only).</summary>
public sealed class RealLlamaServerProcess : ILlamaServerProcess
{
    private Process? _process;

    /// <inheritdoc />
    public bool Start(string executablePath, string[] arguments, out string error)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                error = "process start returned null";
                return false;
            }

            // Drain output on background threads so a full pipe can never deadlock the child.
            _ = _process.StandardOutput.ReadToEndAsync();
            _ = _process.StandardError.ReadToEndAsync();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public bool HasExited => _process is null || _process.HasExited;

    /// <inheritdoc />
    public int ExitCode => _process is { HasExited: true } ? _process.ExitCode : -1;

    /// <inheritdoc />
    public void Kill()
    {
        try
        {
            if (_process is { } process && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone — shutdown is idempotent
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Kill();
}

/// <summary>Health-probe seam (GET /health; injectable for smoke).</summary>
public interface IL2HealthProbePort
{
    /// <summary>Returns true when the local server answered /health with 2xx.</summary>
    Task<bool> ProbeAsync(int port, CancellationToken cancellationToken);
}

/// <summary>Real /health probe over one in-tree Godot HttpRequest node.</summary>
public sealed class GodotHealthProbePort : IL2HealthProbePort
{
    private readonly HttpRequest _request;

    /// <summary>Creates the probe over one owner node (the node must be inside the scene tree).</summary>
    public GodotHealthProbePort(Node owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _request = new HttpRequest
        {
            MaxRedirects = 0,
            Timeout = 10d,
            UseThreads = true,
        };
        owner.AddChild(_request);
    }

    /// <inheritdoc />
    public async Task<bool> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(long result, long code, string[] headers, byte[] body)
        {
            completion.TrySetResult(
                result == (long)HttpRequest.Result.Success && code is >= 200 and <= 299);
        }

        _request.RequestCompleted += OnCompleted;
        CancellationTokenRegistration cancel = cancellationToken.Register(
            () =>
            {
                _request.CancelRequest();
                completion.TrySetResult(false);
            });

        Error sent = _request.Request($"http://127.0.0.1:{port}/health", [], Godot.HttpClient.Method.Get);
        if (sent != Error.Ok)
        {
            _request.RequestCompleted -= OnCompleted;
            cancel.Dispose();
            return false;
        }

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _request.RequestCompleted -= OnCompleted;
            cancel.Dispose();
        }
    }
}

/// <summary>Terminal health of one start attempt.</summary>
/// <param name="Ok">True when Ready (probe green; warm-up outcome rides the detail).</param>
/// <param name="Detail">Diagnostic line (no key material exists on this lane).</param>
public sealed record L2RuntimeStartResult(bool Ok, string Detail);

/// <summary>
/// The llama-server process host (T-137): spawns the child under the
/// spike-mandated arguments, polls /health inside the start budget, then
/// fires ONE warm-up chat request against the OpenAI endpoint
/// (prefix-cache preheat — spike §2.4 82.5 s cold-start killer). Shutdown is
/// idempotent and always kills the tree. UI-thread safe: every long step is
/// async; the host never blocks the frame loop. All IO seams are injected —
/// the headless smoke drives the same state machine with fakes.
/// </summary>
public sealed class L2RuntimeHost : IDisposable
{
    private readonly ILlamaServerProcess _process;
    private readonly IL2HealthProbePort _healthProbe;
    private readonly Func<int, CancellationToken, Task<bool>> _warmupCall;

    private bool _started;
    private bool _disposed;

    /// <summary>True while a child process is spawned and not yet stopped.</summary>
    public bool IsSpawned => _started && !_process.HasExited;

    /// <param name="process">Spawn seam (real adapter or smoke fake).</param>
    /// <param name="healthProbe">/health seam.</param>
    /// <param name="warmupCall">Warm-up seam: (port, ct) → true when the OpenAI endpoint accepted one chat request (47-t129 §3.2①).</param>
    public L2RuntimeHost(
        ILlamaServerProcess process,
        IL2HealthProbePort healthProbe,
        Func<int, CancellationToken, Task<bool>> warmupCall)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _warmupCall = warmupCall ?? throw new ArgumentNullException(nameof(warmupCall));
    }

    /// <summary>
    /// Starts the lane: spawn → /health poll loop within
    /// <see cref="L2Facts.RuntimeStartBudget"/> → one warm-up chat request.
    /// Returns the terminal verdict (process death mid-probe surfaces as a
    /// failure with the exit code — the 降级矩阵 ProcessDead input fact).
    /// </summary>
    /// <param name="executablePath">Absolute llama-server path (host-resolved).</param>
    /// <param name="modelAbsolutePath">Absolute GGUF path (host-resolved).</param>
    /// <param name="port">Local port.</param>
    /// <param name="cancellationToken">Cancel the start (player closed the page).</param>
    public async Task<L2RuntimeStartResult> StartAsync(
        string executablePath,
        string modelAbsolutePath,
        int port,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop(); // idempotent restart semantics

        if (!_process.Start(
                executablePath,
                L2ProviderRow.ServerArguments(modelAbsolutePath, port),
                out string spawnError))
        {
            return new L2RuntimeStartResult(false, $"spawn failed: {spawnError}");
        }

        _started = true;

        // /health poll loop inside the budget (0.5 s cadence).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(L2Facts.RuntimeStartBudget);
        while (true)
        {
            if (_process.HasExited)
            {
                int code = _process.ExitCode;
                Stop();
                return new L2RuntimeStartResult(false, $"process exited during startup with code {code}");
            }

            bool healthy;
            try
            {
                healthy = await _healthProbe.ProbeAsync(port, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Stop();
                return new L2RuntimeStartResult(false, "start cancelled");
            }
            catch (Exception)
            {
                healthy = false; // probe port failures are retries, not verdicts
            }

            if (healthy)
            {
                break;
            }

            if (budget.Token.IsCancellationRequested)
            {
                Stop();
                return new L2RuntimeStartResult(
                    false, $"health probe not green within {L2Facts.RuntimeStartBudget.TotalSeconds:0}s");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop();
                return new L2RuntimeStartResult(false, "start budget exhausted");
            }
        }

        // Spike §3.2①: one warm-up request absorbs the 82.5 s cold prefix —
        // the first real wave-gap fill then rides the hot cache. Warm-up
        // failure is non-fatal: the lane is up; the first fill just pays
        // the cold cost honestly.
        bool warmed;
        try
        {
            warmed = await _warmupCall(port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            warmed = false;
        }

        return new L2RuntimeStartResult(true, warmed ? "ready (warmed)" : "ready (warm-up skipped)");
    }

    /// <summary>Stops the lane (idempotent, always kills the tree).</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _process.Kill();
        _started = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _process.Dispose();
        _disposed = true;
    }
}
