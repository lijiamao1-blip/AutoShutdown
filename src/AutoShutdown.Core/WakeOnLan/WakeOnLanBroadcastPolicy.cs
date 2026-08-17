using System.Net;
using System.Net.Sockets;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// WoL 发送目标网络边界（S21-D1）。默认有限广播 255.255.255.255 与本机活动接口
/// 对应的定向广播地址允许发送；其余（公网单播、组播、回环、链路本地、0.0.0.0、
/// 任意非本机接口网段或无法验证为本机局域网广播的地址）一律拒绝，绝不向本机局域网
/// 之外发送 UDP。本策略为纯函数 + 可注入本机接口来源，可完整测试；
/// 不扫描、不自动发现、不访问公网。
/// </summary>
public sealed class WakeOnLanBroadcastPolicy
{
    private readonly IWakeOnLanLocalNetworks _localNetworks;

    public WakeOnLanBroadcastPolicy(IWakeOnLanLocalNetworks localNetworks)
    {
        ArgumentNullException.ThrowIfNull(localNetworks);
        _localNetworks = localNetworks;
    }

    /// <summary>
    /// 目标 IPv4 是否允许发送（默认有限广播或本机接口定向广播）。不允许时输出
    /// 可诊断原因。IPv6/非法输入一律拒绝（fail-closed）。
    /// </summary>
    public bool IsPermittedDestination(IPAddress destination, out string reason)
    {
        ArgumentNullException.ThrowIfNull(destination);
        reason = string.Empty;

        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            reason = "仅支持 IPv4 广播地址";
            return false;
        }

        var bytes = destination.GetAddressBytes();

        // 有限广播 255.255.255.255：仅本机网段广播，始终允许。
        if (bytes.All(byteValue => byteValue == 0xFF))
        {
            return true;
        }

        // 定向广播：必须等于某个本机活动接口的广播地址。
        foreach (var iface in _localNetworks.GetActiveIpv4Interfaces())
        {
            var directedBroadcast = DirectedBroadcast(iface.Address, iface.Mask);
            if (directedBroadcast is not null && directedBroadcast.SequenceEqual(bytes))
            {
                return true;
            }
        }

        reason = ClassifyRejection(bytes);
        return false;
    }

    private static byte[]? DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork
            || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcast = new byte[4];
        for (var index = 0; index < 4; index++)
        {
            broadcast[index] = (byte)(addressBytes[index] | (byte)~maskBytes[index]);
        }

        return broadcast;
    }

    private static string ClassifyRejection(byte[] bytes)
    {
        if (bytes[0] == 0)
        {
            return "零地址不允许发送";
        }

        if (bytes[0] == 127)
        {
            return "回环地址不允许发送";
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return "链路本地地址不允许发送";
        }

        if (bytes[0] is >= 224 and <= 239)
        {
            return "组播地址不允许发送";
        }

        if (IsPublicUnicast(bytes))
        {
            return "公网单播地址不允许发送（只能向本机局域网定向广播发送）";
        }

        return "不是本机活动接口的定向广播地址（非本机网段）";
    }

    /// <summary>RFC1918 私网地址之外的 IPv4 视为公网单播。</summary>
    private static bool IsPublicUnicast(byte[] bytes)
    {
        if (bytes[0] == 10)
        {
            return false;
        }

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            return false;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return false;
        }

        return true;
    }
}
