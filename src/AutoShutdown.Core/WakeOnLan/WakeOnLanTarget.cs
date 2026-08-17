using System.Net;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// 目标机器配置（S21）。稳定 id、名称、MAC（严格校验）、可选 IPv4 广播地址与端口。
/// 端口默认 9（WoL 标准）；广播地址缺省时回退到有限广播 255.255.255.255。
/// 只向用户显式配置的目标发送，绝不扫描、自动发现或访问公网。
/// </summary>
public sealed record WakeOnLanTarget
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>规范 MAC：<c>AA:BB:CC:DD:EE:FF</c>（大写）。</summary>
    public string Mac { get; init; } = string.Empty;

    /// <summary>可选 IPv4 广播地址（如 192.168.1.255）。null 表示使用有限广播 255.255.255.255。</summary>
    public string? Ipv4BroadcastAddress { get; init; }

    /// <summary>可选 UDP 端口（1..65535）。null 表示默认 9。</summary>
    public int? Port { get; init; }

    /// <summary>WoL 标准默认端口。</summary>
    public const int DefaultPort = 9;

    /// <summary>默认有限广播地址（仅局域网，不出网）。</summary>
    public static readonly IPAddress DefaultBroadcastAddress = IPAddress.Broadcast;

    /// <summary>返回结构错误描述；结构合法返回 null。</summary>
    public static string? GetStructuralError(WakeOnLanTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Id == Guid.Empty)
        {
            return "target.Id must not be an empty GUID.";
        }

        if (string.IsNullOrWhiteSpace(target.Name))
        {
            return "target.Name must not be empty.";
        }

        if (!MacAddress.TryParse(target.Mac, out _))
        {
            return "target.Mac is not a valid MAC address.";
        }

        if (target.Ipv4BroadcastAddress is { } broadcast)
        {
            if (!IPAddress.TryParse(broadcast, out var parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return "target.Ipv4BroadcastAddress must be a valid IPv4 address.";
            }
        }

        if (target.Port is { } port && port is < 1 or > 65535)
        {
            return "target.Port must be between 1 and 65535.";
        }

        return null;
    }
}
