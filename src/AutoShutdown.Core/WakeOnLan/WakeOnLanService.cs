using System.Net;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// Wake-on-LAN 发送编排（S21）。加载目标配置 → 严格 MAC 解析 → 构建 Magic Packet
/// → 解析广播地址（缺省有限广播 255.255.255.255）与端口（缺省 9）→ 经
/// <see cref="IUdpDatagramSender"/> 发送。只向用户显式配置的目标发送，绝不伪造成功。
/// S21-D1 网络边界：任何非本机局域网定向广播的发送目的地一律结构化拒绝（fail-closed），
/// 绝不发出 UDP 包；默认有限广播恒允许。
/// </summary>
public sealed class WakeOnLanService : IWakeOnLanService
{
    private readonly TargetMachineManager _targets;
    private readonly IUdpDatagramSender _sender;
    private readonly WakeOnLanBroadcastPolicy _broadcastPolicy;

    public WakeOnLanService(
        TargetMachineManager targets,
        IUdpDatagramSender sender,
        WakeOnLanBroadcastPolicy broadcastPolicy)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(broadcastPolicy);
        _targets = targets;
        _sender = sender;
        _broadcastPolicy = broadcastPolicy;
    }

    public async Task<WolSendResult> SendAsync(
        Guid targetMachineId,
        CancellationToken cancellationToken)
    {
        var target = await _targets.GetAsync(targetMachineId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return WolSendResult.Failure(
                WolSendStatus.TargetNotFound,
                "The target machine does not exist.");
        }

        if (!MacAddress.TryParse(target.Mac, out var mac))
        {
            return WolSendResult.Failure(
                WolSendStatus.InvalidTarget,
                "The target MAC address is invalid.");
        }

        var datagram = MagicPacket.Build(mac);

        IPAddress broadcast;
        if (target.Ipv4BroadcastAddress is { } configured)
        {
            if (!IPAddress.TryParse(configured, out var parsed)
                || parsed is null
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return WolSendResult.Failure(
                    WolSendStatus.InvalidTarget,
                    "The target IPv4 broadcast address is invalid.");
            }

            broadcast = parsed;
        }
        else
        {
            broadcast = WakeOnLanTarget.DefaultBroadcastAddress;
        }

        // S21-D1 网络边界：默认有限广播恒允许；自定义地址仅当为本机局域网定向广播时
        // 才允许。不安全地址一律结构化拒绝，绝不发送 UDP。
        if (!_broadcastPolicy.IsPermittedDestination(broadcast, out var reason))
        {
            return WolSendResult.Failure(
                WolSendStatus.InvalidTarget,
                "拒绝向非本机局域网广播发送：" + reason);
        }

        var port = target.Port ?? WakeOnLanTarget.DefaultPort;

        var send = await _sender
            .SendAsync(datagram, broadcast, port, cancellationToken)
            .ConfigureAwait(false);

        if (!send.Succeeded)
        {
            return WolSendResult.Failure(
                WolSendStatus.SendFailed,
                "The magic packet could not be sent: " + (send.Error ?? "unknown socket error."));
        }

        return WolSendResult.Success(
            $"Magic packet sent to {target.Name} ({MacAddress.ToCanonical(mac)}) via {broadcast}:{port}.");
    }
}
