namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// Wake-on-LAN Magic Packet 构建器（S21）。数据包必须是 6 字节 0xFF
/// 后接目标 MAC 重复 16 次（共 6 + 16×6 = 102 字节）。纯函数，无副作用。
/// </summary>
public static class MagicPacket
{
    /// <summary>Magic Packet 总长度：6 字节前导 0xFF + 16 次 MAC 重复。</summary>
    public const int PacketLength = 6 + 16 * 6;

    /// <summary>前导同步字节（16 进制 FF，重复 6 次）。</summary>
    public const byte SyncByte = 0xFF;

    /// <summary>MAC 在数据包内重复的次数。</summary>
    public const int MacRepeats = 16;

    /// <summary>构建 Magic Packet。MAC 必须恰好 6 字节，否则抛 <see cref="ArgumentException"/>。</summary>
    public static byte[] Build(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("A magic packet requires a 6-byte MAC address.", nameof(mac));
        }

        var packet = new byte[PacketLength];

        for (var index = 0; index < 6; index++)
        {
            packet[index] = SyncByte;
        }

        for (var repeat = 0; repeat < MacRepeats; repeat++)
        {
            mac.CopyTo(packet.AsSpan(6 + repeat * 6));
        }

        return packet;
    }

    /// <summary>
    /// 从规范 MAC 字符串构建 Magic Packet。解析失败返回 null（调用方须按 InvalidTarget 失败，
    /// 绝不伪造成功）。
    /// </summary>
    public static byte[]? TryBuild(string? macText)
    {
        if (!MacAddress.TryParse(macText, out var mac))
        {
            return null;
        }

        return Build(mac);
    }
}
