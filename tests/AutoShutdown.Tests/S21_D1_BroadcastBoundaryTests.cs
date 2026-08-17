using System.Net;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-D1：WoL 网络边界最小返修。默认有限广播与本机活动接口定向广播允许发送；
/// 公网单播/组播/回环/链路本地/零地址/非本机网段一律结构化拒绝且 sender 调用数为 0。
/// 配置载入保留旧记录用于展示/人工修正；发送、测试发送、WoL 任务执行复用同一拒绝边界。
/// 全部纯内存 + 替身，无真实 UDP、无真实网络栈。
/// </summary>
public sealed class S21_D1_BroadcastBoundaryTests
{
    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // 本机活动接口 192.168.1.100/24 → 定向广播 192.168.1.255。
    private static readonly IWakeOnLanLocalNetworks LocalSubnet =
        new FixedLocalNetworks(("192.168.1.100", "255.255.255.0"));

    // ---- 策略层：允许 ----

    [Fact]
    public void Policy_AllowsLimitedBroadcast()
    {
        var policy = new WakeOnLanBroadcastPolicy(LocalSubnet);

        Assert.True(policy.IsPermittedDestination(IPAddress.Broadcast, out _));
    }

    [Fact]
    public void Policy_AllowsLocalInterfaceDirectedBroadcast()
    {
        var policy = new WakeOnLanBroadcastPolicy(LocalSubnet);

        Assert.True(policy.IsPermittedDestination(IPAddress.Parse("192.168.1.255"), out _));
    }

    [Theory]
    [InlineData("8.8.8.8", "公网单播")]
    [InlineData("239.255.255.250", "组播")]
    [InlineData("127.0.0.1", "回环")]
    [InlineData("169.254.1.1", "链路本地")]
    [InlineData("0.0.0.0", "零地址")]
    [InlineData("10.0.0.255", "不是本机活动接口")]
    [InlineData("192.168.5.255", "不是本机活动接口")]
    [InlineData("192.168.1.100", "不是本机活动接口")] // 本机接口自身单播不是广播
    public void Policy_RejectsOffLanDestinations(string address, string reasonFragment)
    {
        var policy = new WakeOnLanBroadcastPolicy(LocalSubnet);

        Assert.False(policy.IsPermittedDestination(IPAddress.Parse(address), out var reason));
        Assert.Contains(reasonFragment, reason);
    }

    [Fact]
    public void Policy_RejectsIpv6()
    {
        var policy = new WakeOnLanBroadcastPolicy(LocalSubnet);

        Assert.False(policy.IsPermittedDestination(IPAddress.Parse("::1"), out var reason));
        Assert.Contains("IPv4", reason);
    }

    [Fact]
    public void Policy_WhenNoLocalInterfaces_OnlyLimitedBroadcastAllowed()
    {
        var policy = new WakeOnLanBroadcastPolicy(new FixedLocalNetworks());

        Assert.True(policy.IsPermittedDestination(IPAddress.Broadcast, out _));
        Assert.False(policy.IsPermittedDestination(IPAddress.Parse("192.168.1.255"), out _));
    }

    // ---- 发送层：允许/拒绝且 sender 调用数为 0 ----

    [Fact]
    public async Task Send_WithLocalDirectedBroadcast_IsAllowed()
    {
        var (service, sender) = CreateService("192.168.1.255");

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.True(result.Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), sent.Destination);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("239.255.255.250")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.255")]
    [InlineData("192.168.5.255")]
    public async Task Send_WithUnsafeBroadcast_RejectsWithoutSending(string unsafeBroadcast)
    {
        var (service, sender) = CreateService(unsafeBroadcast);

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.Equal(WolSendStatus.InvalidTarget, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("拒绝向非本机局域网广播发送", result.Message);
        Assert.Empty(sender.Sent); // 绝不发出 UDP
    }

    [Fact]
    public async Task Send_DefaultLimitedBroadcast_AlwaysAllowed()
    {
        var (service, sender) = CreateService(broadcast: null);

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(IPAddress.Broadcast, Assert.Single(sender.Sent).Destination);
    }

    // ---- 配置载入：旧的不安全地址保留用于展示/人工修正，发送 fail-closed ----

    [Fact]
    public async Task ConfigLoad_WithOldUnsafeBroadcast_IsReadable_ButSendFailClosed()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            TargetMachineStore.FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{MachineId}}", "Name": "OLD", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "8.8.8.8", "Port": 9 }
              ]
            }
            """);
        var manager = new TargetMachineManager(new TargetMachineStore(storage));
        var sender = new RecordingSender();
        var service = new WakeOnLanService(manager, sender, new WakeOnLanBroadcastPolicy(LocalSubnet));

        // 读取可诊断：载入 Success，旧记录完整保留（供展示/人工修正）。
        var load = await manager.LoadAsync(CancellationToken.None);
        Assert.Equal(TargetMachinesLoadStatus.Success, load.Status);
        var machine = Assert.Single(load.Document!.Machines);
        Assert.Equal("8.8.8.8", machine.Ipv4BroadcastAddress);

        // 发送 fail-closed：拒绝，绝不发 UDP。
        var result = await service.SendAsync(MachineId, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("公网单播", result.Message);
        Assert.Empty(sender.Sent);
    }

    // ---- 共享边界：测试发送（UI）与调度 WoL 任务 ----

    [Fact]
    public async Task TestSendViaSectionViewModel_WithUnsafeBroadcast_RejectsWithoutSending()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            TargetMachineStore.FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{MachineId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "8.8.8.8", "Port": 9 }
              ]
            }
            """);
        var manager = new TargetMachineManager(new TargetMachineStore(storage));
        var sender = new RecordingSender();
        var service = new WakeOnLanService(manager, sender, new WakeOnLanBroadcastPolicy(LocalSubnet));
        var section = new AutoShutdown.App.Presentation.WolTargetsSectionViewModel(manager, service);
        await section.RefreshAsync(CancellationToken.None);
        var row = Assert.Single(section.Targets);

        section.TestSendTargetCommand.Execute(row);

        Assert.Contains("发送失败", section.ErrorText);
        Assert.Contains("拒绝向非本机局域网广播发送", section.ErrorText);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task TaskExecutor_WithUnsafeBroadcast_ThrowsHandlingExceptionWithoutSending()
    {
        var (service, sender) = CreateService("8.8.8.8");
        var executor = new WakeOnLanTaskExecutor(service);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => executor.ExecuteAsync(WolInstance(), CancellationToken.None));

        Assert.Contains("拒绝向非本机局域网广播发送", exception.Message);
        Assert.Empty(sender.Sent);
    }

    // ---- 夹具 ----

    private static (WakeOnLanService Service, RecordingSender Sender) CreateService(
        string? broadcast)
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            TargetMachineStore.FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{MachineId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF",
                  "Ipv4BroadcastAddress": {{ToJson(broadcast)}}, "Port": 9 }
              ]
            }
            """);
        var manager = new TargetMachineManager(new TargetMachineStore(storage));
        var sender = new RecordingSender();
        var service = new WakeOnLanService(
            manager,
            sender,
            new WakeOnLanBroadcastPolicy(LocalSubnet));
        return (service, sender);
    }

    private static string ToJson(string? value)
        => value is null ? "null" : $"\"{value}\"";

    private static TaskInstance WolInstance() => new()
    {
        InstanceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.WakeOnLan,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        HasExecuted = true,
        TargetMachineId = MachineId,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

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

/// <summary>测试替身：固定本机活动 IPv4 接口清单（纯内存，绝不触碰真实网络栈）。</summary>
internal sealed class FixedLocalNetworks : IWakeOnLanLocalNetworks
{
    private readonly IReadOnlyList<LocalIpv4Interface> _interfaces;

    public FixedLocalNetworks(params (string Address, string Mask)[] interfaces)
    {
        _interfaces = interfaces
            .Select(entry => new LocalIpv4Interface(
                IPAddress.Parse(entry.Address),
                IPAddress.Parse(entry.Mask)))
            .ToArray();
    }

    public IReadOnlyList<LocalIpv4Interface> GetActiveIpv4Interfaces() => _interfaces;
}
