namespace EraCore.Llm.L2;

/// <summary>
/// Frozen download fact of one L2 artifact (T-137): the model GGUF or the
/// llama.cpp runtime zip. Facts are measured and cross-checked against the
/// upstream (47-t129 spike §1.2; sha256 re-verified against the HuggingFace
/// LFS oid on 2026-10-13) — they are transport facts, not balance values.
/// </summary>
/// <param name="Id">Stable artifact id (<c>model</c> / <c>runtime</c>).</param>
/// <param name="DownloadUrl">Public direct-download URL (HuggingFace / GitHub Releases).</param>
/// <param name="FileName">Final file name under <c>data/models/</c> (or <c>data/runtime/</c> for the zip).</param>
/// <param name="RelativeDir">Directory relative to the injected data root where the artifact lands.</param>
/// <param name="SizeBytes">Exact expected byte length (size gate runs before sha256).</param>
/// <param name="Sha256Hex">Lowercase hex sha256 (verify gate; null only for informational rows).</param>
public sealed record L2Artifact(
    string Id,
    string DownloadUrl,
    string FileName,
    string RelativeDir,
    long SizeBytes,
    string? Sha256Hex);

/// <summary>
/// The T-137 L2 artifact manifest — the ONLY place the download facts live
/// (47-t129 spike 裁决：模型 1.93 GB 不进主包=可选下载；llama.cpp CPU 预编译
/// ~66 MB 解压后可随主包发布，主包未带时走同一下载器补拉). Player-facing
/// sizes in copy are derived from these records — never re-worded elsewhere
/// (禁双份措辞).
/// </summary>
public static class L2Facts
{
    /// <summary>
    /// The default local model (spike-measured): Qwen2.5-3B-Instruct Q4_K_M
    /// GGUF, bartowski quantization mirror, Apache-2.0 (Qwen2.5 license —
    /// 许可友好, spike §1.2). 1.93 GB ⇒ optional download, never in the
    /// main package.
    /// </summary>
    public static L2Artifact Model { get; } = new(
        Id: "model",
        DownloadUrl: "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
        FileName: "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
        RelativeDir: "models",
        SizeBytes: 1929903264L, // ci-allow: R7 T-137 47-t129 §1.2 实测模型字节数（下载事实常量，非平衡数值）
        Sha256Hex: "9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94"); // ci-allow: R7 T-137 与 HuggingFace LFS oid 对账过的校验和（十六进制非数值锚点）

    /// <summary>
    /// The llama.cpp CPU runtime (spike-measured b11714 win-cpu-x64): zip
    /// ~18.7 MB / ~66 MB unpacked. 体积裁决（任务卡）：可随主包分发；主包未带
    /// 时由下载器按本行补拉（同一 sha256/断点续传通道）.
    /// </summary>
    public static L2Artifact Runtime { get; } = new(
        Id: "runtime",
        DownloadUrl: "https://github.com/ggml-org/llama.cpp/releases/download/b11714/llama-b11714-bin-win-cpu-x64.zip",
        FileName: "llama-b11714-bin-win-cpu-x64.zip",
        RelativeDir: "runtime",
        SizeBytes: 19662606L, // ci-allow: R7 T-137 GitHub Release Content-Length 实测（下载事实常量）
        Sha256Hex: null); // GitHub Releases assets carry no stable server-side digest — size gate only, recorded in REV

    /// <summary>
    /// Default local server port (spike §1.1 used 8017; kept as the default —
    /// player-overridable via settings/l2-tier.json).
    /// </summary>
    public const int DefaultPort = 8017; // ci-allow: R7 T-137 47-t129 §1.2 spike 实测端口（工程域常量）

    /// <summary>
    /// llama-server context window (spike §3.2③ 强制配套：单槽小上下文压内存 —
    /// <c>-c 2048 -np 1</c>；实测常驻 ~3.4 GB 为 -c 4096×4 槽口径).
    /// </summary>
    public const int ServerContextTokens = 2048; // ci-allow: R7 T-137 47-t129 §3.2③ 单槽小上下文裁决（工程域常量）

    /// <summary>Single parallel slot (spike §3.2③).</summary>
    public const int ServerParallelSlots = 1; // ci-allow: R7 T-137 47-t129 §3.2③（工程域常量）

    /// <summary>
    /// Wave-gap fill budget per order (spike §2.4: 热整答 3.9~22.7 s 上界取整
    /// 25 s；超时=放弃本轮不卡波——任务卡验收词). The first-answer warmup
    /// (82.5 s cold, §2.4) is absorbed by the startup warm-up request, not by
    /// this budget.
    /// </summary>
    public static readonly TimeSpan WaveGapFillBudget = TimeSpan.FromSeconds(25); // ci-allow: R7 T-137 47-t129 §2.4 热答上界+1s 余量（工程域常量）

    /// <summary>Startup health-probe budget before the runtime is declared dead (spike §2.4 模型加载 3.2~4.7 s → 余量 60 s).</summary>
    public static readonly TimeSpan RuntimeStartBudget = TimeSpan.FromSeconds(60); // ci-allow: R7 T-137 47-t129 §2.4 加载实测×10 余量（工程域常量）

    /// <summary>
    /// Constant marker credential for the local lane: llama-server ignores
    /// Authorization; the BYOK gateway requires a non-empty key so the L2 row
    /// stores this marker. It is NOT key material (never secret, never
    /// player-configured, constant by construction) — recorded here so the
    /// single fact cannot drift between call sites.
    /// </summary>
    public const string LocalLaneKeyMarker = "local-lane-no-key";

    /// <summary>Provider row id of the synthesized L2 lane (gateway FindProvider key).</summary>
    public const string ProviderRowId = "l2-local";

    /// <summary>Model id the synthesized row advertises (llama-server accepts any id).</summary>
    public const string ProviderModelId = "qwen2.5-3b-instruct-q4km-local";

    /// <summary>Download chunk size the engine fetches per wire call (4 MiB).</summary>
    public const int DownloadChunkBytes = 4 * 1024 * 1024; // ci-allow: R7 T-137 下载分片工程常量（2 的幂，非内容锚点）

    /// <summary>Player-visible whole-GB size line (derived from <see cref="Model"/>; the only wording source).</summary>
    public static string ModelSizeZh => $"{Model.SizeBytes / (1024d * 1024d * 1024d):0.0} GB";

    /// <summary>All downloadable artifacts (model first — the UI lists it first).</summary>
    public static IReadOnlyList<L2Artifact> All => [Model, Runtime];
}
