using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Checkpoint;
using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraNet;

/// <summary>
/// T-139 (F-161) 联机会话快照（迟到加入/断线重连/偏差重同步的统一载荷，
/// 48 §3.2）：<c>(seed + 广播命令流 + 当前 tick)</c>——与 K-7 检查点记录
/// <see cref="WaveSnapshotRecord"/> 同形状（seed+命令流，恢复=确定性重放），
/// 区别只在边界：检查点锚波界（槽位轮转），会话快照锚「当前 tick」。
/// 线协议复用 <see cref="SimCommandLog"/>（K-8 回放流既有行形状，零新线格式）。
/// </summary>
/// <param name="Seed">Level seed (replay key #1).</param>
/// <param name="Tick">Host current tick at snapshot time.</param>
/// <param name="SnapshotEveryTicks">Digest cadence the client must adopt.</param>
/// <param name="Commands">The full broadcast journal in broadcast (= authority enqueue) order.</param>
public sealed record NetSessionSnapshot(
    long Seed,
    long Tick,
    int SnapshotEveryTicks,
    IReadOnlyList<SimCommandLog> Commands);

/// <summary>
/// T-139 (F-161) 房间握手信息（48 §6.5 内容指纹门禁的最小接线）：客户端
/// 上行本端指纹，宿主逐项比对——不一致即拒绝入房（对端内容/代码版本不一致
/// 必然哈希漂移，48 §1.3 学费条款）。协议版本是线格式自身的破坏性变更闸。
/// </summary>
/// <param name="ProtocolVersion">Wire-protocol version (must match exactly).</param>
/// <param name="CoreVersion">EraCore assembly version (EraCore.VersionInfo).</param>
/// <param name="ContentFingerprint">Order-sensitive FNV-1a over the level's content files.</param>
public sealed record NetHandshakeInfo(
    int ProtocolVersion,
    string CoreVersion,
    string ContentFingerprint)
{
    /// <summary>
    /// Compares this info against the host's: true when the peer may join.
    /// Ordinal comparisons only — no culture, no timing, pure value equality
    /// against the three frozen fields.
    /// </summary>
    public bool Matches(NetHandshakeInfo host) =>
        ProtocolVersion == host.ProtocolVersion
        && string.Equals(CoreVersion, host.CoreVersion, StringComparison.Ordinal)
        && string.Equals(ContentFingerprint, host.ContentFingerprint, StringComparison.Ordinal);
}

/// <summary>T-139 wire-protocol version (bump = breaking change, review-gated).</summary>
public static class NetWireProtocol
{
    /// <summary>First protocol generation (T-139 v1: commands/digests/advisor/snapshot).</summary>
    public const int Version = 1;
}

/// <summary>
/// T-139 内容指纹（48 §6.5）：对「参与关卡装配的内容文件清单」做顺序敏感
/// FNV-1a（与 ComputeStateHash 同族的确定性聚合——同一份内容 ⇒ 同一指纹；
/// 任何文件字节变化 ⇒ 指纹变化）。纯函数、零 I/O——文件读取由调用方做
/// （表现层/测试装载内容后传入字符串序列）。
/// </summary>
public static class NetContentFingerprint
{
    /// <summary>
    /// Computes the order-sensitive FNV-1a 64 fingerprint over the content
    /// file bodies (caller-supplied order MUST be deterministic: same file
    /// list, same order, every call).
    /// </summary>
    public static string Compute(IEnumerable<string> fileContents)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis (既有哈希族常数)
        foreach (string content in fileContents)
        {
            foreach (char c in content)
            {
                hash ^= c;
                hash *= 1099511628211UL; // FNV-1a prime
            }

            // 文件间分隔：记录长度防止「a|b vs ab」拼接歧义。
            hash ^= (ulong)content.Length;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Source-generated JSON metadata for every EraNet wire type (AOT red line
/// D-05: 联机线序列化同样只走生成元数据；snake_case 对齐仓库线格式惯例)。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NetSessionSnapshot))]
[JsonSerializable(typeof(NetHandshakeInfo))]
[JsonSerializable(typeof(AdvisorOrderRequest))]
[JsonSerializable(typeof(AdvisorOrderReply))]
[JsonSerializable(typeof(SimCommand))]
[JsonSerializable(typeof(SimCommandLog))]
[JsonSerializable(typeof(SimHashSample))]
public sealed partial class EraNetJsonContext : JsonSerializerContext;
