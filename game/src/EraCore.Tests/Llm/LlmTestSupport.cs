using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EraCore.Llm;
using EraCore.Secrets;

namespace EraCore.Tests.Llm;

// [C-16] T-072 测试支撑：Fake/录制端口、内存密钥库、cassette 模型与仓库相对
// 寻址。纯支撑类，不含测试方法（T3 不适用）。

/// <summary>
/// In-memory <see cref="ISecretStore"/> (CS-3.4 白名单端口之一) for gateway
/// tests — synthetic key values only, never real key material (RL5).
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public string Backend => "memory-test";

    public void Store(string providerId, string? secretValue)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            throw new ArgumentException("provider id 不能为空", nameof(providerId));
        }

        _secrets[providerId] = secretValue ?? string.Empty;
    }

    public string Retrieve(string providerId) =>
        string.IsNullOrEmpty(providerId)
            ? throw new ArgumentException("provider id 不能为空", nameof(providerId))
            : _secrets.TryGetValue(providerId, out var value) ? value : string.Empty;

    public void Clear(string providerId) => _secrets.Remove(providerId);

    public void ClearAll() => _secrets.Clear();
}

/// <summary>
/// Scripted <see cref="IHttpPort"/> fake: counts every wire call (the
/// zero-network oracle) and records requests verbatim, then serves queued
/// replies. Mocking the PORT is sanctioned (CS-3.4); the system under test
/// — the gateway and its parsers — is never mocked.
/// </summary>
public sealed class FakeWirePort : IHttpPort
{
    public sealed record ServedCall(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);

    private readonly Queue<Func<HttpPortRequest, HttpPortResponse>> _replies = [];

    public List<ServedCall> Calls { get; } = [];

    public int CallCount => Calls.Count;

    /// <summary>Exception to throw on the next call instead of replying.</summary>
    public Exception? NextFailure { get; set; }

    /// <summary>Queues one reply produced from the incoming request.</summary>
    public void EnqueueReply(Func<HttpPortRequest, HttpPortResponse> reply) => _replies.Enqueue(reply);

    /// <summary>Queues a fixed non-2xx reply with a body.</summary>
    public void EnqueueError(int status, string body) =>
        EnqueueReply(_ => new HttpPortResponse(status, body, null));

    /// <summary>Queues a 200 SSE reply from raw SSE text (chunked by the caller's stream).</summary>
    public void EnqueueSse(string rawSse, int chunkSize = 7) =>
        EnqueueReply(_ => new HttpPortResponse(200, null, new ChunkedStream(rawSse, chunkSize)));

    /// <summary>Queues a 200 plain-JSON reply.</summary>
    public void EnqueueJson(string body) =>
        EnqueueReply(_ => new HttpPortResponse(200, body, null));

    public Task<HttpPortResponse> SendAsync(HttpPortRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(new ServedCall(
            request.Method, request.Url,
            new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            request.BodyJson));
        if (NextFailure is { } failure)
        {
            throw failure;
        }

        if (_replies.Count == 0)
        {
            throw new InvalidOperationException("FakeWirePort: 未编排响应却收到调用（测试脚本缺口）");
        }

        return Task.FromResult(_replies.Dequeue()(request));
    }
}

/// <summary>
/// Read-only stream over a UTF-8 text payload that drips out in small
/// chunks (default 7 bytes) — guarantees the gateway's SSE line buffer
/// constantly reassembles half lines (半行拼接 exercised on every read).
/// </summary>
public sealed class ChunkedStream : Stream
{
    private readonly byte[] _data;
    private readonly int _chunkSize;
    private int _position;

    public ChunkedStream(string text, int chunkSize = 7)
    {
        _data = Encoding.UTF8.GetBytes(text);
        _chunkSize = chunkSize;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = Math.Min(_chunkSize, Math.Min(count, _data.Length - _position));
        if (take <= 0)
        {
            return 0;
        }

        _data.AsSpan(_position, take).CopyTo(buffer.AsSpan(offset, take));
        _position += take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var take = Math.Min(_chunkSize, Math.Min(buffer.Length, _data.Length - _position));
        if (take <= 0)
        {
            return ValueTask.FromResult(0);
        }

        _data.AsSpan(_position, take).CopyTo(buffer.Span);
        _position += take;
        return ValueTask.FromResult(take);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Stream that yields a prefix immediately and then never produces another
/// byte until cancelled — the chunk-idle timeout driver (§11.7 双超时).
/// </summary>
public sealed class StalledStream : Stream
{
    private readonly byte[] _prefix;
    private bool _prefixServed;

    public StalledStream(string prefix)
    {
        _prefix = Encoding.UTF8.GetBytes(prefix);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_prefixServed)
        {
            _prefixServed = true;
            var take = Math.Min(buffer.Length, _prefix.Length);
            _prefix.AsSpan(0, take).CopyTo(buffer.Span);
            return take;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Stream that never serves a single byte — headers arrived (2xx reply)
/// but the body never starts; drives the stream-phase first-byte timeout
/// (§11.7 首字节预算覆盖流首读, T-091).
/// </summary>
public sealed class FirstByteStallStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Stream serving a prefix, then sleeping a wall-clock <see cref="Gap"/>
/// before serving the suffix — the 时间可控注入 for chunk-idle boundary
/// tests (gap below budget ⇒ pass, above ⇒ TimeoutIdle; T-091).
/// </summary>
public sealed class GapStream : Stream
{
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private bool _prefixServed;
    private bool _suffixServed;

    public GapStream(string prefix, TimeSpan gap, string suffix)
    {
        _prefix = Encoding.UTF8.GetBytes(prefix);
        Gap = gap;
        _suffix = Encoding.UTF8.GetBytes(suffix);
    }

    /// <summary>Wall-clock delay between prefix and suffix.</summary>
    public TimeSpan Gap { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_prefixServed)
        {
            _prefixServed = true;
            var take = Math.Min(buffer.Length, _prefix.Length);
            _prefix.AsSpan(0, take).CopyTo(buffer.Span);
            return take;
        }

        if (!_suffixServed)
        {
            _suffixServed = true;
            await Task.Delay(Gap, cancellationToken).ConfigureAwait(false);
            var take = Math.Min(buffer.Length, _suffix.Length);
            _suffix.AsSpan(0, take).CopyTo(buffer.Span);
            return take;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Stream that serves a prefix then dies with <see cref="IOException"/> —
/// the mid-body disconnect oracle (首字节窗口内/流中途断连, T-091). An
/// empty prefix makes the very FIRST read throw (disconnect before the
/// first body byte).
/// </summary>
public sealed class DisconnectStream : Stream
{
    private readonly byte[] _prefix;
    private bool _prefixServed;

    public DisconnectStream(string prefix)
    {
        _prefix = Encoding.UTF8.GetBytes(prefix);
        _prefixServed = _prefix.Length == 0;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixServed)
        {
            throw new IOException("connection reset by peer (synthetic)");
        }

        _prefixServed = true;
        var take = Math.Min(buffer.Length, _prefix.Length);
        _prefix.AsSpan(0, take).CopyTo(buffer.Span);
        return ValueTask.FromResult(take);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixServed)
        {
            throw new IOException("connection reset by peer (synthetic)");
        }

        _prefixServed = true;
        var take = Math.Min(count, _prefix.Length);
        _prefix.AsSpan(0, take).CopyTo(buffer.AsSpan(offset, take));
        return take;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Wrapper counting Dispose/DisposeAsync calls — the resource-cleanup
/// oracle for timeout/断连 verdicts (T-091: the gateway must close the
/// body stream on every exit path).
/// </summary>
public sealed class DisposeTrackingStream : Stream
{
    private readonly Stream _inner;

    public DisposeTrackingStream(Stream inner) => _inner = inner;

    /// <summary>True after either dispose flavor fired.</summary>
    public bool Disposed { get; private set; }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
        _inner.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        Disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        // Skip the sync base path (double-dispose of the same inner stream).
        GC.SuppressFinalize(this);
    }
}

/// <summary>Deterministic wall clock for F-091 call-log timestamp tests (CS-1.4).</summary>
public sealed class FixedWallClock : EraCore.Contracts.Foundation.IClock
{
    private long _nowMs;

    public FixedWallClock(long nowMs) => _nowMs = nowMs;

    public long Tick => 0;

    public long NowMs => _nowMs;

    public void Advance(long deltaMs) => _nowMs += deltaMs;
}

/// <summary>
/// The one REAL <see cref="IHttpPort"/> implementation (CS-1.3: real
/// networking lives only in the test assembly) — System.Net.Http based,
/// used by the MiMo online tests and the cassette recorder. Production
/// ships the Godot HTTPClient adapter instead (方案 §11.7). Never follows
/// redirects (domain-lock guard) and never logs header values (RL5).
/// </summary>
public sealed class HttpClientLlmPort : IHttpPort, IDisposable
{
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _client;
    private bool _disposed;

    /// <summary>Default ctor goes to the real network.</summary>
    public HttpClientLlmPort()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
    {
    }

    /// <summary>Test ctor injects a fake handler (offline contract tests).</summary>
    public HttpClientLlmPort(HttpMessageHandler handler)
    {
        _handler = handler;
        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan, // budgets belong to the gateway
        };
    }

    /// <summary>Raw request count seen by the handler (redirect guard oracle).</summary>
    public int SendCount { get; private set; }

    public async Task<HttpPortResponse> SendAsync(
        HttpPortRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url)
        {
            Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue; // carried by StringContent
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        SendCount++;
        var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return new HttpPortResponse((int)response.StatusCode, null, stream);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HttpPortResponse((int)response.StatusCode, body, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        _handler.Dispose();
    }
}

/// <summary>
/// Port that never answers — cancels only when the caller's token fires;
/// drives the port-phase first-byte timeout test.
/// </summary>
public sealed class NeverPort : IHttpPort
{
    public int CallCount { get; private set; }

    public async Task<HttpPortResponse> SendAsync(
        HttpPortRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return new HttpPortResponse(0, null, null);
    }
}

/// <summary>
/// Wire port that tees everything it serves: request captured verbatim
/// (auth headers kept IN MEMORY only, stripped on cassette build), success
/// streams wrapped in <see cref="TeeStream"/> so the recorded bytes are
/// exactly what the gateway consumed. Recorder-only (RL5: nothing logged).
/// </summary>
public sealed class RecordingPort : IHttpPort
{
    private readonly IHttpPort _inner;
    private HttpPortRequest? _lastRequest;
    private (int Status, bool IsStream, MemoryStream Bytes)? _lastResponse;

    public RecordingPort(IHttpPort inner) => _inner = inner;

    public async Task<HttpPortResponse> SendAsync(
        HttpPortRequest request, CancellationToken cancellationToken = default)
    {
        _lastRequest = request;
        var reply = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.BodyStream is { } stream)
        {
            var copy = new MemoryStream();
            _lastResponse = (reply.StatusCode, true, copy);
            return new HttpPortResponse(reply.StatusCode, null, new TeeStream(stream, copy));
        }

        var bytes = new MemoryStream(
            Encoding.UTF8.GetBytes(reply.Body ?? string.Empty));
        _lastResponse = (reply.StatusCode, false, bytes);
        return reply;
    }

    /// <summary>
    /// Builds the cassette for the captured exchange. Response lines are
    /// the decoded wire lines exactly as the gateway saw them (SSE) or a
    /// single body line (non-stream).
    /// </summary>
    public LlmCassette BuildCassette(
        string name, string recordedAtUtc, string providerId,
        string protocol, string model, string baseUrl,
        string? source = null)
    {
        if (_lastRequest is null || _lastResponse is not { } reply)
        {
            throw new InvalidOperationException("录制端口尚未观察到任何调用");
        }

        var text = Encoding.UTF8.GetString(reply.Bytes.ToArray());
        var lines = reply.IsStream
            ? text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray()
            : new[] { text };
        return new LlmCassette(
            name, recordedAtUtc, providerId, protocol, model, baseUrl,
            _lastRequest.Url.AbsoluteUri,
            new Dictionary<string, string>(_lastRequest.Headers, StringComparer.OrdinalIgnoreCase),
            _lastRequest.BodyJson,
            reply.Status, reply.IsStream, lines, source);
    }
}

/// <summary>Stream copy that mirrors every read into a side MemoryStream.</summary>
public sealed class TeeStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _copy;

    public TeeStream(Stream inner, MemoryStream copy)
    {
        _inner = inner;
        _copy = copy;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _copy.Write(buffer.Span[..read]);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// LLM cassette (录制器产物, D-20/CS-3.4: cassettes are always recorder
/// output, never handwritten). Auth headers are dropped at record time and
/// the serialized text is asserted key-free before it is ever written.
/// <paramref name="Source"/> marks provenance: null = live recording
/// (mimo 金样); "synthetic-fakewireport" = recorder run over a scripted
/// FakeWirePort (T-091 错误/多工具矩阵，零成本合成，标注来源).
/// </summary>
public sealed record LlmCassette(
    string Cassette,
    string RecordedAtUtc,
    string ProviderId,
    string Protocol,
    string Model,
    string BaseUrl,
    string RequestUrl,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string RequestBody,
    int ResponseStatus,
    bool ResponseIsSse,
    IReadOnlyList<string> ResponseLines,
    string? Source = null)
{
    /// <summary>Provenance tag for synthetic cassettes (FakeWirePort scripting).</summary>
    public const string SyntheticSource = "synthetic-fakewireport";
}

/// <summary>Cassette path + mask helpers shared by recorder and replay tests.</summary>
public static class LlmCassettes
{
    /// <summary>Header names that must never be serialized into a cassette.</summary>
    public static readonly IReadOnlySet<string> SecretHeaderNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase) { "Authorization", "x-api-key" };

    /// <summary>
    /// game/content/llm-cassettes directory (repo-relative walk, RL9).
    /// Located through the existing game/content root so the directory may
    /// legitimately not exist yet (the recorder creates it).
    /// </summary>
    public static string CassetteDir()
    {
        var contentDir = FindRepoDir(Path.Combine("game", "content"), "game/content");
        return Path.Combine(contentDir, "llm-cassettes");
    }

    /// <summary>
    /// config/secrets.local.json path (repo-relative walk, RL9). The secrets
    /// file is gitignored, so it is located by anchoring on the always-present
    /// game/content root — probing by the file's own existence would throw on
    /// keyless checkouts (CI runners) instead of reaching OnlineSkipped's
    /// documented "no key file → skip" semantics (CI fix, T-096 era).
    /// </summary>
    public static string SecretsFilePath()
    {
        // contentRoot = <repo>/game/content → two levels up is the repo root
        // (game/content lives under the game/ code root, D-02).
        var contentRoot = FindRepoDir(Path.Combine("game", "content"), "game/content");
        var repoRoot = Directory.GetParent(Directory.GetParent(contentRoot)!.FullName)!.FullName;
        return Path.Combine(repoRoot, "config", "secrets.local.json");
    }

    /// <summary>True when online tests are skipped (SKIP_ONLINE=1 or no key file).</summary>
    public static bool OnlineSkipped(out string reason)
    {
        if (Environment.GetEnvironmentVariable("SKIP_ONLINE") == "1")
        {
            reason = "SKIP_ONLINE=1（显式跳过在线用例）";
            return true;
        }

        if (!File.Exists(SecretsFilePath()))
        {
            reason = "config/secrets.local.json 不存在（CI 无 key 环境语义）";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads the MiMo live-test descriptor (key + endpoints) from the
    /// gitignored secrets file. Values never reach test output (RL5).
    /// </summary>
    public static bool TryLoadMiMo(out string key, out string baseUrl, out string model)
    {
        key = string.Empty;
        baseUrl = string.Empty;
        model = string.Empty;
        if (!File.Exists(SecretsFilePath()))
        {
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(SecretsFilePath()));
        if (!document.RootElement.TryGetProperty("xiaomi_mimo", out var mimo)
            || !mimo.TryGetProperty("key", out var keyNode))
        {
            return false;
        }

        key = keyNode.GetString() ?? string.Empty;
        if (mimo.TryGetProperty("base_url_openai", out var baseNode))
        {
            baseUrl = baseNode.GetString() ?? string.Empty;
        }

        if (mimo.TryGetProperty("best_model", out var modelNode))
        {
            model = modelNode.GetString() ?? string.Empty;
        }

        return key.Length > 0;
    }

    /// <summary>
    /// Serializes one cassette, stripping secret headers and asserting the
    /// output contains no key material (RL5 double gate — recorder refuses
    /// to emit a dirty cassette).
    /// </summary>
    public static string SerializeMasked(LlmCassette cassette, string? knownKey)
    {
        cassette = cassette with
        {
            RequestHeaders = cassette.RequestHeaders
                .Where(kv => !SecretHeaderNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
        };
        var json = JsonSerializer.Serialize(cassette, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrEmpty(knownKey) && json.Contains(knownKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("cassette 序列化结果包含密钥明文，拒绝写出（RL5）");
        }

        return json;
    }

    private static string FindRepoDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, relative);
            if (File.Exists(probe) || Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 {label}（测试须在仓库内运行）");
    }
}
