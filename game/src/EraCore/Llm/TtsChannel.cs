using EraCore.Secrets;

namespace EraCore.Llm;

/// <summary>
/// Outcome states of one reserved TTS channel call (T-138, F-140).
/// <see cref="Ok"/> exists for the future implementation only — this build
/// has no wire path, so every call ends in one of the four refusal states.
/// </summary>
public enum TtsChannelStatus
{
    /// <summary>Audio synthesized. UNREACHABLE in this build (no implementation ships).</summary>
    Ok,

    /// <summary><c>tts.enable=false</c> — refused with zero network.</summary>
    Disabled,

    /// <summary>The <c>tts</c> section is incomplete (provider/model/voice) or references no provider row.</summary>
    NotConfigured,

    /// <summary>No key for the referenced provider in ISecretStore — refused with zero network (BYOK).</summary>
    MissingKey,

    /// <summary>Reserved boundary: the feature ships in a later release — refused, never fakes success.</summary>
    NotImplemented,
}

/// <summary>
/// Result of one reserved TTS channel call. Every non-<see cref="TtsChannelStatus.Ok"/>
/// state carries bilingual player-facing copy and NO audio — a refusal can
/// never be mistaken for a synthesized reply (绝不谎报成功).
/// </summary>
/// <param name="Status">Terminal gate the call stopped at.</param>
/// <param name="Audio">Synthesized audio; null on every refusal state.</param>
/// <param name="MessageZh">简体中文文案（玩家可见；永不包含 Key 材料）。</param>
/// <param name="MessageEn">English copy (player-visible; key-free).</param>
public sealed record TtsChannelResult(
    TtsChannelStatus Status,
    TtsSpeechResponse? Audio,
    string MessageZh,
    string MessageEn)
{
    /// <summary>True only on a genuine synthesized reply.</summary>
    public bool IsOk => Status == TtsChannelStatus.Ok && Audio is not null;

    /// <summary>True for every refusal state (all four are zero-network in this build).</summary>
    public bool IsRefusal => Status != TtsChannelStatus.Ok;
}

/// <summary>
/// Wire port of the reserved TTS channel (T-138, F-140) — the future seam
/// a real speech implementation plugs into, mirroring <see cref="IHttpPort"/>
/// discipline: real networking lives outside EraCore (CS-1.3) and
/// implementations never log auth material (RL5). NO implementation ships
/// in this build — the channel stops at
/// <see cref="TtsChannelStatus.NotImplemented"/> and this port is never
/// invoked (预留边界; tests pin that with call-counting fakes).
/// </summary>
public interface ITtsProvider
{
    /// <summary>Endpoint descriptor this provider would call (from <see cref="TtsProviderPresets"/>).</summary>
    TtsProviderEndpoint Endpoint { get; }

    /// <summary>Performs the speech call (reserved; no implementation exists yet).</summary>
    /// <exception cref="IOException">Transport failure, per IHttpPort convention.</exception>
    Task<TtsSpeechResponse> SpeakAsync(
        TtsSpeechRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The reserved TTS voice channel facade (T-138, F-140; 08 行
/// 「mimo-v2.5-tts 接口预留，不进首发验收」). Interface reservation ONLY:
/// every call walks the BYOK gates in order and stops at an explicit
/// refusal status — this build contains no wire implementation, so
/// <see cref="SpeakAsync"/> never reaches <see cref="ITtsProvider"/> and
/// never reports success. Gate order (与 LlmGateway 同构):
/// <list type="bullet">
/// <item><c>tts.enable=false</c> → <see cref="TtsChannelStatus.Disabled"/>;</item>
/// <item>provider/model/voice 不全或 provider 行不存在 →
/// <see cref="TtsChannelStatus.NotConfigured"/>;</item>
/// <item>ISecretStore 无该 provider 的 Key →
/// <see cref="TtsChannelStatus.MissingKey"/>（无 Key 零请求）；</item>
/// <item>全部通过 → <see cref="TtsChannelStatus.NotImplemented"/>（预留未
/// 实现，绝不半实现、绝不谎报成功）。</item>
/// </list>
/// Key discipline: only the stored value's LENGTH is inspected — the value
/// never enters results or messages (RL5 / F-084).
/// </summary>
public sealed class TtsChannel
{
    private readonly LlmConfig _config;
    private readonly ISecretStore _secrets;
    private readonly ITtsProvider? _provider;

    /// <param name="config">Config snapshot (the tts section drives the gates).</param>
    /// <param name="secrets">Provider key storage (key per provider row id, BYOK).</param>
    /// <param name="provider">
    /// Reserved injection seam for the future wire implementation; null =
    /// nothing wired. This build never invokes it regardless.
    /// </param>
    public TtsChannel(LlmConfig config, ISecretStore secrets, ITtsProvider? provider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _provider = provider;
    }

    /// <summary>The reserved provider port, as injected (diagnostics/tests; never called in this build).</summary>
    public ITtsProvider? Provider => _provider;

    /// <summary>
    /// Requests one speech synthesis. Returns the terminal refusal status
    /// synchronously — no network, no key value flow, no audio in this
    /// build (预留边界; see class docs for the gate order).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public Task<TtsChannelResult> SpeakAsync(
        TtsSpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var tts = _config.Tts ?? new LlmTtsConfig();
        if (!tts.Enable)
        {
            return Task.FromResult(Refuse(TtsChannelStatus.Disabled));
        }

        if (IsIncomplete(tts))
        {
            return Task.FromResult(Refuse(TtsChannelStatus.NotConfigured));
        }

        if (_secrets.Retrieve(tts.Provider).Length == 0)
        {
            return Task.FromResult(Refuse(TtsChannelStatus.MissingKey));
        }

        // 预留边界的终点站：配置与 Key 都齐也不发请求——mimo TTS 端点未开通，
        // 本版本到 NotImplemented 为止（绝不半实现、绝不谎报成功）。
        return Task.FromResult(Refuse(TtsChannelStatus.NotImplemented));
    }

    private bool IsIncomplete(LlmTtsConfig tts) =>
        string.IsNullOrWhiteSpace(tts.Provider)
        || string.IsNullOrWhiteSpace(tts.Model)
        || string.IsNullOrWhiteSpace(tts.Voice)
        || _config.FindProvider(tts.Provider) is null;

    private static TtsChannelResult Refuse(TtsChannelStatus status) => new(
        status, null, ZhFor(status), EnFor(status));

    private static string ZhFor(TtsChannelStatus status) => status switch
    {
        TtsChannelStatus.Disabled =>
            "语音通道未开启：本次调用未发出任何网络请求。",
        TtsChannelStatus.NotConfigured =>
            "语音通道配置不完整（tts 的 provider/model/voice 或对应服务行缺失），未发出任何网络请求。",
        TtsChannelStatus.MissingKey =>
            "未配置 API Key：密钥只存本机凭据存储，无 Key 不发任何请求。",
        TtsChannelStatus.NotImplemented =>
            "语音通道已预留，功能随后续版本开放：本次调用未发出任何网络请求。",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "unknown tts channel status."),
    };

    private static string EnFor(TtsChannelStatus status) => status switch
    {
        TtsChannelStatus.Disabled =>
            "Voice channel off: no network request was sent.",
        TtsChannelStatus.NotConfigured =>
            "Voice channel not fully configured (tts provider/model/voice or its service row missing); no network request was sent.",
        TtsChannelStatus.MissingKey =>
            "No API key configured: keys live only in this machine's credential store; no request is sent without one.",
        TtsChannelStatus.NotImplemented =>
            "Voice channel reserved: the feature ships in a later release; no network request was sent.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "unknown tts channel status."),
    };
}
