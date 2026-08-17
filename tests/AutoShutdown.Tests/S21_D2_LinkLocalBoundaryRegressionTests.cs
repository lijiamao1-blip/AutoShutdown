using System.Net;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-D2 回归：链路本地（APIPA）定向广播即使在伪造本机活动接口下也必须拒绝。
/// 禁止范围（0.0.0.0/8、回环、链路本地、组播）先于本机定向广播匹配（fail-closed）；
/// 默认有限广播与合法 RFC1918 私网定向广播仍允许；拒绝时 sender 调用数为 0。
/// 全部纯内存 + 替身，无真实 UDP、无真实网络栈。
/// </summary>
public sealed class S21_D2_LinkLocalBoundaryRegressionTests
{
    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Policy_LinkLocalDirectedBroadcast_RejectedEvenIfLocalInterfaceMatches()
    {
        // S21-D2 缺口：伪造 APIPA 活动接口 169.254.1.10/24 → 其定向广播 169.254.1.255
        // 不得被当作本机定向广播放行，必须先按链路本地拒绝。
        var policy = new WakeOnLanBroadcastPolicy(
            new FixedLocalNetworks(("169.254.1.10", "255.255.255.0")));

        Assert.False(policy.IsPermittedDestination(IPAddress.Parse("169.254.1.255"), out var reason));
        Assert.Contains("链路本地", reason);
    }

    [Fact]
    public async Task Send_LinkLocalDirectedBroadcast_RejectsWithoutSendingEvenIfLocalInterfaceMatches()
    {
        // 伪造 APIPA 接口下发送 169.254.1.255 → InvalidTarget 且 sender 调用数为 0。
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            TargetMachineStore.FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{MachineId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "169.254.1.255", "Port": 9 }
              ]
            }
            """);
        var manager = new TargetMachineManager(new TargetMachineStore(storage));
        var sender = new RecordingSender();
        var service = new WakeOnLanService(
            manager,
            sender,
            new WakeOnLanBroadcastPolicy(
                new FixedLocalNetworks(("169.254.1.10", "255.255.255.0"))));

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.Equal(WolSendStatus.InvalidTarget, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("链路本地", result.Message);
        Assert.Empty(sender.Sent); // 绝不发出 UDP
    }

    [Fact]
    public void Policy_ForbiddenRanges_RejectedBeforeLocalMatch()
    {
        // 即便本机活动接口自身落在禁止范围内（回环 127/8），其"定向广播"也必须在匹配前拒绝。
        var policy = new WakeOnLanBroadcastPolicy(
            new FixedLocalNetworks(("127.0.0.1", "255.255.255.0")));

        Assert.False(policy.IsPermittedDestination(IPAddress.Parse("127.0.0.255"), out var reason));
        Assert.Contains("回环", reason);
    }

    [Fact]
    public void Policy_LimitedBroadcast_StillAllowed()
    {
        var policy = new WakeOnLanBroadcastPolicy(
            new FixedLocalNetworks(("169.254.1.10", "255.255.255.0")));

        Assert.True(policy.IsPermittedDestination(IPAddress.Broadcast, out _));
    }

    [Fact]
    public async Task Send_PrivateDirectedBroadcast_StillAllowed()
    {
        // 合法 RFC1918 本机活动接口定向广播仍允许（与 D1 行为保持一致）。
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            TargetMachineStore.FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{MachineId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "192.168.1.255", "Port": 9 }
              ]
            }
            """);
        var manager = new TargetMachineManager(new TargetMachineStore(storage));
        var sender = new RecordingSender();
        var service = new WakeOnLanService(
            manager,
            sender,
            new WakeOnLanBroadcastPolicy(
                new FixedLocalNetworks(("192.168.1.100", "255.255.255.0"))));

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), Assert.Single(sender.Sent).Destination);
    }

    private sealed class RecordingSender : IUdpDatagramSender
    {
        public List<(byte[] Datagram, IPAddress Destination, int Port)> Sent { get; } = [];

        public Task<UdpDatagramSendResult> SendAsync(
            byte[] datagram,
            IPAddress destination,
            int port,
            CancellationToken cancellationToken)
        {
            Sent.Add(((byte[])datagram.Clone(), destination, port));
            return Task.FromResult(new UdpDatagramSendResult
            {
                Status = UdpDatagramSendStatus.Success
            });
        }
    }
}
