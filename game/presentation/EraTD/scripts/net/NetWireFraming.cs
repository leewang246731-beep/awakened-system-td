using System.Text;

namespace EraTD.Net;

// T-139 (F-161) 线帧格式（表现层 ENet 适配器的封包层）：1 字节消息类 +
// UTF-8 JSON 体（EraNetJsonContext 源生成序列化——AOT 红线 D-05 同律）。
// 纯函数、零 Godot 依赖面（仅 System.Text）——headless 冒烟可直接做编解码
// 往返断言，不真开端口（G4 纪律：内存回环承载运行面，帧格式承载字节面）。

/// <summary>Wire message kinds of the T-139 ENet lane (v1 protocol).</summary>
internal enum NetWireMessageKind : byte
{
    /// <summary>Client → host: handshake info (protocol/core/content fingerprint).</summary>
    Handshake = 1,

    /// <summary>Host → client: join payload (session snapshot; also the mismatch refusal is a disconnect).</summary>
    SessionSnapshot = 2,

    /// <summary>Client → host: one command submission.</summary>
    CommandFromClient = 3,

    /// <summary>Host → clients: one stamped broadcast command.</summary>
    CommandBroadcast = 4,

    /// <summary>Host → clients: one periodic digest.</summary>
    DigestPush = 5,

    /// <summary>Client → host: one advisor order/confirm/refuse request.</summary>
    AdvisorRequest = 6,

    /// <summary>Host → client: one advisor approval reply.</summary>
    AdvisorReply = 7,
}

/// <summary>Framing helpers: [kind byte][UTF-8 JSON body].</summary>
internal static class NetWireFraming
{
    /// <summary>Frames one message (kind + JSON body) into packet bytes.</summary>
    internal static byte[] Frame(NetWireMessageKind kind, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var packet = new byte[body.Length + 1];
        packet[0] = (byte)kind;
        Array.Copy(body, 0, packet, 1, body.Length);
        return packet;
    }

    /// <summary>Parses one packet; false when malformed (truncated/empty).</summary>
    internal static bool TryParse(byte[] packet, out NetWireMessageKind kind, out string json)
    {
        kind = default;
        json = string.Empty;
        if (packet is null || packet.Length < 1)
        {
            return false;
        }

        if (!Enum.IsDefined(typeof(NetWireMessageKind), packet[0]))
        {
            return false;
        }

        kind = (NetWireMessageKind)packet[0];
        json = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
        return true;
    }
}
