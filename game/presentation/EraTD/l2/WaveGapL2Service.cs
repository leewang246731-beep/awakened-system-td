using System.Collections.Concurrent;
using EraCore.Agent.Grammar;
using EraCore.Agent.Pipeline;
using EraCore.Llm;
using EraCore.Llm.L2;
using EraCore.Secrets;
using Godot;

namespace EraTD.L2;

// T-137 (C-27/F-078) 波间异步执行器 + L2 意图填充端口（presentation 层）：
// 队列/状态机在 EraCore（WaveGapFillQueue）；本服务只做三件事——开窗放单
// （Task 派发，绝不阻塞模拟 tick）、主线程泵（完成/超时结果统一回主线再落
// 队列——队列永远只在主线程被碰）、降级回调（超时=放弃本轮+横幅，进程死/
// 失败=回 L1）。填充端口包 LlmActionRouter（F-096 五条防线随附——本地通道
// 同罩，spike §2.3 实证 ①b 别名规整恰好兜住 3B 模型回显）。

/// <summary>
/// Godot-side <see cref="IHttpPort"/> for the L2/L3 fill lane: full-body
/// collection over one <see cref="HttpRequest"/> node (the wave-gap lane
/// uses non-stream replies — the synthesized L2 row sets use_stream=false).
/// Same contract discipline as the settings-page port: zero redirects,
/// exceptions on transport failure, nothing logged.
/// </summary>
public sealed class FillLaneHttpPort : IHttpPort
{
    private readonly HttpRequest _request;

    /// <summary>Creates the port over one owner node (must be inside the scene tree).</summary>
    public FillLaneHttpPort(Node owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _request = new HttpRequest
        {
            MaxRedirects = 0,
            Timeout = 90d,
            BodySizeLimit = 8 * 1024 * 1024,
            UseThreads = true,
        };
        owner.AddChild(_request);
    }

    /// <inheritdoc />
    public async Task<HttpPortResponse> SendAsync(
        HttpPortRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var completion = new TaskCompletionSource<HttpPortResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(long result, long code, string[] headers, byte[] body)
        {
            if (result != (long)HttpRequest.Result.Success)
            {
                completion.TrySetException(result == (long)HttpRequest.Result.Timeout
                    ? new OperationCanceledException(nameof(HttpRequest.Result.Timeout))
                    : new IOException($"fill-lane transport failure result={result}"));
                return;
            }

            if (code is >= 200 and <= 299)
            {
                completion.TrySetResult(new HttpPortResponse((int)code, null, new MemoryStream(body ?? [])));
            }
            else
            {
                string text = body is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(body) : string.Empty;
                completion.TrySetResult(new HttpPortResponse((int)code, text, null));
            }
        }

        _request.RequestCompleted += OnCompleted;
        CancellationTokenRegistration cancel = cancellationToken.Register(
            () =>
            {
                _request.CancelRequest();
                completion.TrySetCanceled();
            });

        var headers = new string[request.Headers.Count];
        int i = 0;
        foreach (var (name, value) in request.Headers)
        {
            headers[i++] = $"{name}: {value}";
        }

        var method = request.Method == "GET"
            ? Godot.HttpClient.Method.Get
            : Godot.HttpClient.Method.Post;
        Error sent = _request.Request(request.Url.ToString(), headers, method, request.BodyJson);
        if (sent != Error.Ok)
        {
            _request.RequestCompleted -= OnCompleted;
            cancel.Dispose();
            throw new IOException($"fill-lane request rejected result={sent}");
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

/// <summary>
/// ISecretStore view of the L2 lane: the constant marker (llama-server
/// ignores Authorization — spike §2.5). NOT key material, never persisted,
/// never player-configurable; keeps the gateway's key-presence gate
/// single-sourced (no L2 bypass branch).
/// </summary>
public sealed class LocalLaneSecretStore : ISecretStore
{
    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string Backend => "l2-local-marker";

    /// <inheritdoc />
    public void Store(string providerId, string? secretValue) =>
        throw new NotSupportedException("the L2 lane stores no key material");

    /// <inheritdoc />
    public string Retrieve(string providerId) =>
        providerId == L2Facts.ProviderRowId ? L2Facts.LocalLaneKeyMarker : string.Empty;

    /// <inheritdoc />
    public void Clear(string providerId)
    {
    }

    /// <inheritdoc />
    public void ClearAll()
    {
    }
}

/// <summary>
/// <see cref="IL2IntentFillPort"/> over the real C-16 gateway: one order →
/// <see cref="LlmActionRouter.ProposeAsync"/> against the L2 provider row
/// (OpenAI protocol, 127.0.0.1) or the active L3 row (BYOK config). Every
/// gateway guarantee (whitelist/双超时/调用日志) rides unchanged — the spike
/// §2.5 zero-protocol-change verdict is structural here.
/// </summary>
public sealed class GatewayIntentFillPort : IL2IntentFillPort
{
    private readonly LlmGateway _gateway;
    private readonly AliasDictionary? _towerAliases;
    private readonly string _providerId;

    /// <param name="gateway">Gateway bound to the synthesized L2 row (or the L3 config).</param>
    /// <param name="providerId">Provider row id the fills route through.</param>
    /// <param name="towerAliases">Content alias dictionary (F-096 ①b canonicalization; null skips).</param>
    public GatewayIntentFillPort(LlmGateway gateway, string providerId, AliasDictionary? towerAliases = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _providerId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        _towerAliases = towerAliases;
    }

    /// <summary>Builds the L2-bound port over one owner node (OpenAI lane to 127.0.0.1).</summary>
    public static GatewayIntentFillPort ForLocalLane(Node owner, int port, AliasDictionary? towerAliases = null)
    {
        LlmProviderConfig row = L2ProviderRow.Build(port);
        var config = new LlmConfig
        {
            Version = 1,
            ActiveProvider = row.Id,
            Providers = [row],
        };
        var gateway = new LlmGateway(config, new FillLaneHttpPort(owner), new LocalLaneSecretStore());
        return new GatewayIntentFillPort(gateway, row.Id, towerAliases);
    }

    /// <inheritdoc />
    public async Task<L2IntentFillResult> FillAsync(string orderText, CancellationToken cancellationToken)
    {
        var router = new LlmActionRouter(_gateway, _towerAliases);
        LlmActionProposal proposal = await router.ProposeAsync(_providerId, orderText, cancellationToken)
            .ConfigureAwait(false);

        return proposal.Ok && proposal.ActionJson is not null
            ? L2IntentFillResult.Succeeded(proposal.ActionJson)
            : L2IntentFillResult.Failed(
                proposal.ErrorKey ?? "no_tool_call",
                proposal.ErrorKey == LlmErrorKeys.Network ? L2DegradationReason.ProcessDead : L2DegradationReason.FillFailed);
    }
}

/// <summary>Frame-loop pump source of the wave-gap service (thread-pool → main thread marshal).</summary>
public sealed record WaveGapLandedFill(L2QueuedFill Order, L2IntentFillResult Result);

/// <summary>
/// The wave-gap async executor (T-137 验收面「波间异步」): the host's frame
/// loop drives <see cref="OnWaveGapOpened"/> at wave transitions and
/// <see cref="PumpFrame"/> every frame. The queue itself is ONLY ever
/// touched on the main thread; fill tasks land their results through a
/// concurrent hand-off drained by <see cref="PumpFrame"/> — the simulation
/// tick never waits on any of it (超时=放弃本轮不卡波).
/// </summary>
public sealed class WaveGapL2Service
{
    private readonly Func<long> _clockMs;
    private readonly Func<IL2IntentFillPort> _fillPortFactory;
    private readonly ConcurrentQueue<WaveGapLandedFill> _landed = new();

    /// <summary>Main-thread callback when a fill landed inside its budget (host: re-enter the five-stage pipeline + render).</summary>
    public event Action<WaveGapLandedFill>? FillLanded;

    /// <summary>Main-thread callback when a fill was abandoned / landed stale (host: neutral L1-fallback banner).</summary>
    public event Action<L2QueuedFill, L2DegradationReason>? FillAbandoned;

    /// <summary>The pure queue (EraCore) — enqueue + window discipline.</summary>
    public WaveGapFillQueue Queue { get; }

    /// <summary>True while a fill task is in flight (diagnostics/smoke probe).</summary>
    public bool FillInFlight { get; private set; }

    /// <param name="clockMs">Wall-ms clock (main thread reads).</param>
    /// <param name="queue">The EraCore queue (main-thread-only discipline enforced here).</param>
    /// <param name="fillPortFactory">Fill port factory (fresh per fill; node-bound).</param>
    public WaveGapL2Service(Func<long> clockMs, WaveGapFillQueue queue, Func<IL2IntentFillPort> fillPortFactory)
    {
        _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _fillPortFactory = fillPortFactory ?? throw new ArgumentNullException(nameof(fillPortFactory));
    }

    /// <summary>Queues one L1-clarified order for the next wave gap (L2 tier selected).</summary>
    public L2QueuedFill? Enqueue(string orderText) => Queue.Enqueue(orderText);

    /// <summary>
    /// Wave-gap open (frame loop): promotes the head order and dispatches the
    /// fill task. Returns the promoted order (null = nothing to do / already
    /// one in flight). The dispatch itself never awaits — the task hands its
    /// result to <see cref="PumpFrame"/>.
    /// </summary>
    public L2QueuedFill? OnWaveGapOpened()
    {
        L2QueuedFill? started = Queue.OpenWindow();
        if (started is null)
        {
            return null;
        }

        FillInFlight = true;
        _ = RunFillAsync(started);
        return started;
    }

    private async Task RunFillAsync(L2QueuedFill fill)
    {
        L2IntentFillResult result;
        try
        {
            IL2IntentFillPort port = _fillPortFactory();
            result = await port.FillAsync(fill.OrderText, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = L2IntentFillResult.Failed("fill-crashed", L2DegradationReason.FillFailed);
        }

        _landed.Enqueue(new WaveGapLandedFill(fill, result));
    }

    /// <summary>
    /// Frame pump (main thread): (1) deadline check — an expired in-flight
    /// order is abandoned with the neutral timeout notice (放弃本轮);
    /// (2) drain landed results — a fresh landing completes the queue entry,
    /// a stale one (abandoned earlier) only fires the fallback notice.
    /// </summary>
    public void PumpFrame()
    {
        if (Queue.InFlightExpired)
        {
            L2QueuedFill? expired = Queue.AbandonInFlight();
            if (expired is not null)
            {
                FillInFlight = false;
                FillAbandoned?.Invoke(expired, L2DegradationReason.FillTimeout);
            }
        }

        while (_landed.TryDequeue(out WaveGapLandedFill? landed))
        {
            FillInFlight = false;
            L2FillCompletion? completion = Queue.Complete(landed.Order.Id, landed.Result);
            if (completion is not null)
            {
                FillLanded?.Invoke(landed);
            }
            else
            {
                // Stale: the budget expired before the answer arrived.
                FillAbandoned?.Invoke(landed.Order, L2DegradationReason.FillTimeout);
            }
        }
    }

    /// <summary>Drops the whole queue (tier switched / lane stopped). Returns the dropped count.</summary>
    public int Clear() => Queue.Clear();
}
