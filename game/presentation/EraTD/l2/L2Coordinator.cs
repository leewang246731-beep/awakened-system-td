using System.IO;
using EraCore.IO;
using EraCore.Llm.L2;
using EraCore.Presentation;
using Godot;

namespace EraTD.L2;

// T-137 (C-27/F-078) L2 总装协调器（presentation 层纯 C#，UI 无关——设置页/
// 军师面板/波间服务都绑它）：档位选择持久化（settings/l2-tier.json）、模型+
// 运行时下载（EraCore 引擎，端口注入）、llama-server 生命周期（L2RuntimeHost）、
// 路由裁决（EraCore.L2RouteDecider——降级矩阵单源）。事件驱动状态广播；
// headless 冒烟注入假端口跑同一状态机（零真进程零真下载——G4 纪律）。

/// <summary>One state broadcast of the L2 lane (UI mirrors this verbatim).</summary>
/// <param name="Snapshot">Live runtime snapshot.</param>
/// <param name="Progress">Download progress (null when idle).</param>
/// <param name="Notice">Player-visible status line (zh; from the single copy sources).</param>
public sealed record L2LaneState(L2RuntimeSnapshot Snapshot, L2DownloadProgress? Progress, string Notice);

/// <summary>
/// The L2 coordinator (T-137): owns the L2 lane end to end on the
/// presentation side. Path resolution order (model): explicit override →
/// <c>{dataRoot}/models/{manifest name}</c>. Runtime executable: override
/// directory → exe-adjacent bundled <c>runtime/llama-server.exe</c> →
/// <c>{dataRoot}/runtime/unpacked/llama-server.exe</c> (downloaded+unzipped
/// on demand — 体积裁决: llama.cpp ~66 MB 可随主包，模型 1.93 GB 必须可选).
/// </summary>
public sealed class L2Coordinator : IDisposable
{
    private readonly IFileStore _store;
    private readonly IL2DownloadFilePort _files;
    private readonly Func<IL2DownloadWirePort> _wireFactory;
    private readonly Func<L2RuntimeHost> _runtimeFactory;
    private readonly AgentTierAvailability _platform;

    private L2RuntimeHost? _runtime;
    private CancellationTokenSource? _downloadCancel;
    private bool _downloadRunning;
    private bool _disposed;

    /// <summary>Raised on the commanding thread whenever the lane state changes (UI marshals itself).</summary>
    public event Action<L2LaneState>? StateChanged;

    /// <summary>Current persisted settings (tier selection / overrides / port).</summary>
    public L2TierSettings Settings { get; private set; }

    /// <summary>Live runtime snapshot (the route decider input).</summary>
    public L2RuntimeSnapshot Snapshot { get; private set; }

    /// <summary>Download progress of the in-flight run (null when idle).</summary>
    public L2DownloadProgress? Progress { get; private set; }

    /// <summary>In-flight runtime start task (the tier-selection auto-start); null when never started.</summary>
    public Task<L2RuntimeStartResult>? RuntimeStartTask { get; private set; }

    /// <param name="store">D-03 store (settings persistence + data-root resolution).</param>
    /// <param name="files">Streaming download file port over the same root.</param>
    /// <param name="wireFactory">Per-run wire port factory (Godot node-based; one run at a time).</param>
    /// <param name="runtimeFactory">Runtime host factory (process/probe/warmup seams pre-bound).</param>
    /// <param name="platform">Platform tier row (the only Web-fact source).</param>
    public L2Coordinator(
        IFileStore store,
        IL2DownloadFilePort files,
        Func<IL2DownloadWirePort> wireFactory,
        Func<L2RuntimeHost> runtimeFactory,
        AgentTierAvailability platform)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _wireFactory = wireFactory ?? throw new ArgumentNullException(nameof(wireFactory));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        Settings = L2TierStore.Load(store);
        Snapshot = ComputeSnapshot();
    }

    // ------------------------------------------------------------- routing

    /// <summary>Routes one order decision through the frozen matrix (降级判据单源).</summary>
    public L2RouteDecision DecideRoute() =>
        L2RouteDecider.Decide(L2TierStore.RoutingKind(Settings), Snapshot, _platform);

    // ------------------------------------------------------------- commands

    /// <summary>
    /// Persists a new tier selection (immediate — non-BYOK field, mirrors the
    /// render-tier row). Selecting L2 with both artifacts present auto-starts
    /// the local server (async, non-blocking; failures surface through the
    /// snapshot → neutral banner). Selecting L1/L3 stops a running lane.
    /// </summary>
    public void SelectTier(L2TierSelection selection)
    {
        Settings = Settings with { Selection = selection };
        L2TierStore.Save(_store, Settings);

        if (selection == L2TierSelection.L2
            && !_disposed
            && RuntimeStartTask is not { IsCompleted: false }
            && ResolveModelPath() is not null
            && ResolveServerExecutable() is not null)
        {
            RuntimeStartTask = StartRuntimeAsync(); // fire-and-forget; StartRuntimeAsync never throws
        }
        else if (selection != L2TierSelection.L2)
        {
            StopRuntime();
        }

        Publish();
    }

    /// <summary>Updates the path overrides + port (settings page expert row).</summary>
    public void ApplyOverrides(string modelPathOverride, string runtimePathOverride, int port)
    {
        Settings = Settings with
        {
            ModelPathOverride = modelPathOverride.Trim(),
            RuntimePathOverride = runtimePathOverride.Trim(),
            Port = port <= 0 ? L2Facts.DefaultPort : port,
        };
        L2TierStore.Save(_store, Settings);
        Publish();
    }

    /// <summary>
    /// Starts the opt-in download (model first, then the runtime zip when no
    /// executable is resolvable). Progress rides <see cref="StateChanged"/>.
    /// </summary>
    public async Task<L2DownloadResult> StartDownloadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_downloadRunning)
        {
            return new L2DownloadResult(L2DownloadOutcome.TransportFailed, "model", "a download is already running");
        }

        _downloadRunning = true;
        _downloadCancel = new CancellationTokenSource();
        Snapshot = Snapshot with { Phase = L2RuntimePhase.Downloading };
        Publish();

        try
        {
            L2DownloadResult modelResult = await RunEngineAsync(L2Facts.Model, _downloadCancel.Token);
            if (!modelResult.IsUsable)
            {
                return modelResult;
            }

            // Runtime only when nothing else resolves an executable (bundled
            // builds skip this fetch — 体积裁决的「随主包」分支).
            if (ResolveServerExecutable() is null)
            {
                L2DownloadResult runtimeResult = await RunEngineAsync(L2Facts.Runtime, _downloadCancel.Token);
                if (runtimeResult.IsUsable)
                {
                    await UnpackRuntimeZipAsync();
                }
            }

            return modelResult;
        }
        finally
        {
            _downloadRunning = false;
            Progress = null;
            _downloadCancel?.Dispose();
            _downloadCancel = null;
            Snapshot = ComputeSnapshot();
            Publish();
        }
    }

    /// <summary>Cancels the in-flight download (the .part file survives — resume next run).</summary>
    public void CancelDownload() => _downloadCancel?.Cancel();

    private async Task<L2DownloadResult> RunEngineAsync(L2Artifact artifact, CancellationToken cancellationToken)
    {
        var engine = new L2DownloadEngine(_wireFactory(), _files);
        return await engine.RunAsync(
            artifact,
            progress =>
            {
                Progress = progress;
                Publish();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unzips the downloaded runtime zip under the data root (Godot ZIPReader).</summary>
    private async Task UnpackRuntimeZipAsync()
    {
        string zipPath = DataRootFile(L2DownloadEngine.FinalPath(L2Facts.Runtime));
        if (!File.Exists(zipPath))
        {
            return;
        }

        var reader = new ZipReader();
        try
        {
            Error open = reader.Open(zipPath);
            if (open != Error.Ok)
            {
                return; // unpack failure leaves the zip on disk — the next attempt retries honestly
            }

            string destDir = Path.Combine(_store.Root, "runtime", "unpacked");
            Directory.CreateDirectory(destDir);
            foreach (string entry in reader.GetFiles())
            {
                if (entry.EndsWith('/'))
                {
                    continue; // directory entry
                }

                byte[] bytes = reader.ReadFile(entry);
                string dest = Path.Combine(destDir, entry.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
            }
        }
        finally
        {
            reader.Close();
        }
    }

    /// <summary>Starts the local server lane (spawn → /health → warm-up) and refreshes the snapshot.</summary>
    public async Task<L2RuntimeStartResult> StartRuntimeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? model = ResolveModelPath();
        string? executable = ResolveServerExecutable();
        if (model is null || executable is null)
        {
            Snapshot = ComputeSnapshot();
            Publish();
            return new L2RuntimeStartResult(false, "model or runtime missing");
        }

        Snapshot = Snapshot with { Phase = L2RuntimePhase.Starting, Detail = "llama-server 启动中" };
        Publish();

        _runtime?.Dispose();
        _runtime = _runtimeFactory();
        L2RuntimeStartResult result = await _runtime.StartAsync(executable, model, Settings.Port)
            .ConfigureAwait(false);

        Snapshot = ComputeSnapshot(result.Ok
            ? L2RuntimePhase.Ready
            : L2RuntimePhase.ProcessDead, result.Detail);
        Publish();
        return result;
    }

    /// <summary>Stops the local server lane (idempotent).</summary>
    public void StopRuntime()
    {
        _runtime?.Stop();
        Snapshot = ComputeSnapshot();
        Publish();
    }

    /// <summary>Frame-lane health refresh: a dead process flips the snapshot to ProcessDead (the 降级矩阵 input).</summary>
    public void RefreshRuntimeHealth()
    {
        if (Snapshot.Phase == L2RuntimePhase.Ready
            && _runtime is { IsSpawned: false })
        {
            Snapshot = ComputeSnapshot(L2RuntimePhase.ProcessDead, "server process exited");
            Publish();
        }
    }

    // ------------------------------------------------------------- resolution

    /// <summary>Absolute model path when one exists (override → data/models/&lt;name&gt;); null when missing.</summary>
    public string? ResolveModelPath()
    {
        if (!string.IsNullOrWhiteSpace(Settings.ModelPathOverride) && File.Exists(Settings.ModelPathOverride))
        {
            return Settings.ModelPathOverride;
        }

        string dataPath = DataRootFile(L2DownloadEngine.FinalPath(L2Facts.Model));
        return File.Exists(dataPath) ? dataPath : null;
    }

    /// <summary>Absolute llama-server path when one exists (override dir → bundled exe-adjacent → downloaded+unpacked); null when missing.</summary>
    public string? ResolveServerExecutable()
    {
        if (!string.IsNullOrWhiteSpace(Settings.RuntimePathOverride))
        {
            string candidate = Path.Combine(Settings.RuntimePathOverride, "llama-server.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Bundled-with-package lane (release layout ships runtime/ next to the exe).
        string bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // Downloaded lane (unpacked under the data root — any drive, R9).
        string downloaded = Path.Combine(_store.Root, "runtime", "unpacked", "llama-server.exe");
        return File.Exists(downloaded) ? downloaded : null;
    }

    private string DataRootFile(string relativePath) =>
        Path.GetFullPath(Path.Combine(_store.Root, relativePath));

    private L2RuntimeSnapshot ComputeSnapshot(L2RuntimePhase? phaseOverride = null, string? detail = null)
    {
        bool modelPresent = ResolveModelPath() is not null;
        bool runtimePresent = ResolveServerExecutable() is not null;
        L2RuntimePhase phase = phaseOverride ?? (modelPresent
            ? runtimePresent
                ? _runtime is { IsSpawned: true } ? L2RuntimePhase.Ready : L2RuntimePhase.Idle
                : L2RuntimePhase.RuntimeMissing
            : L2RuntimePhase.ModelMissing);
        return new L2RuntimeSnapshot(phase, modelPresent, runtimePresent, Settings.Port, detail ?? Snapshot?.Detail);
    }

    /// <summary>Current state snapshot (the UI mirror source).</summary>
    public L2LaneState CurrentState => new(Snapshot, Progress, NoticeOf());

    private void Publish() => StateChanged?.Invoke(CurrentState);

    /// <summary>Player-visible one-line status (single copy source discipline).</summary>
    private string NoticeOf() => Snapshot.Phase switch
    {
        L2RuntimePhase.Ready => "L2 本地军师已就绪（127.0.0.1 本地推理，不上传）。",
        L2RuntimePhase.Downloading => Progress is { } p
            ? $"正在下载模型 {p.PercentText}（{p.DownloadedBytes / (1024 * 1024)} / {p.TotalBytes / (1024 * 1024)} MB）——可取消，支持断点续传。"
            : "正在准备下载……",
        L2RuntimePhase.Starting => "L2 本地军师启动中（首次加载模型需约一分钟）。",
        L2RuntimePhase.Idle => "L2 已就绪待启动——选择 L2 档位后自动拉起本地军师。",
        L2RuntimePhase.ModelMissing => $"L2 模型未下载（{L2Facts.ModelSizeZh}，可选）——当前军令走 L1 语法档。",
        L2RuntimePhase.RuntimeMissing => "L2 运行时缺失——点击下载将一并补拉（约 19 MB）。",
        L2RuntimePhase.ProcessDead => "L2 进程已退出——军令自动回退 L1 语法档。",
        _ => string.Empty,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CancelDownload();
        _runtime?.Dispose();
        _downloadCancel?.Dispose();
        _disposed = true;
    }
}
