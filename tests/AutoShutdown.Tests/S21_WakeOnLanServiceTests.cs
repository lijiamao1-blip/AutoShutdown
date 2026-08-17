using System.Net;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S21-C1：WakeOnLanService 只向用户配置的目标发送精确的 Magic Packet；
/// 目标缺失/非法/Socket 失败一律结构化失败；取消直接传播。</summary>
public sealed class S21_WakeOnLanServiceTests
{
    [Fact]
    public async Task Send_WithDefaultSettings_SendsExactPacketToLimitedBroadcastPort9()
    {
        var (service, sender) = CreateService(seedMachine: ValidMachine(
            "NAS", mac: "01:02:03:04:05:06"));

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.True(result.Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(IPAddress.Broadcast, sent.Destination);
        Assert.Equal(9, sent.Port);
        Assert.Equal(102, sent.Datagram.Length);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(0xFF, sent.Datagram[i]);
        }

        for (var repeat = 0; repeat < 16; repeat++)
        {
            for (var i = 0; i < 6; i++)
            {
                Assert.Equal((byte)(i + 1), sent.Datagram[6 + repeat * 6 + i]);
            }
        }
    }

    [Fact]
    public async Task Send_WithConfiguredBroadcastAndPort_UsesThem()
    {
        var (service, sender) = CreateService(seedMachine: ValidMachine(
            "NAS", mac: "AA:BB:CC:DD:EE:FF", broadcast: "192.168.1.255", port: 40000));

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.True(result.Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), sent.Destination);
        Assert.Equal(40000, sent.Port);
    }

    [Fact]
    public async Task Send_WhenTargetNotFound_ReturnsTargetNotFoundAndSendsNothing()
    {
        var (service, sender) = CreateService(seedMachine: null);

        var result = await service.SendAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(WolSendStatus.TargetNotFound, result.Status);
        Assert.False(result.Succeeded);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mac")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("GG:HH:II:JJ:KK:LL")]
    public void GetStructuralError_WhenInvalidMac_ReturnsError(string mac)
    {
        var error = WakeOnLanTarget.GetStructuralError(ValidMachine("Bad", mac: mac));

        Assert.NotNull(error);
        Assert.Contains("MAC", error);
    }

    [Theory]
    [InlineData("999.999.1.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.256")]
    public void GetStructuralError_WhenInvalidBroadcast_ReturnsError(string broadcast)
    {
        var error = WakeOnLanTarget.GetStructuralError(ValidMachine(
            "Bad", mac: "AA:BB:CC:DD:EE:FF", broadcast: broadcast));

        Assert.NotNull(error);
        Assert.Contains("IPv4", error);
    }

    [Fact]
    public void GetStructuralError_WhenValid_ReturnsNull()
    {
        var error = WakeOnLanTarget.GetStructuralError(ValidMachine(
            "NAS", mac: "AA:BB:CC:DD:EE:FF", broadcast: "192.168.1.255", port: 40000));

        Assert.Null(error);
    }

    [Fact]
    public void GetStructuralError_WhenEmptyName_ReturnsError()
    {
        var error = WakeOnLanTarget.GetStructuralError(ValidMachine("   "));

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Send_WhenSenderFails_ReturnsSendFailed()
    {
        var (service, sender) = CreateService(seedMachine: ValidMachine("NAS", mac: "AA:BB:CC:DD:EE:FF"));
        sender.FailWith = new InvalidOperationException("network unreachable");

        var result = await service.SendAsync(MachineId, CancellationToken.None);

        Assert.Equal(WolSendStatus.SendFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("network unreachable", result.Message);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Send_WhenCancelled_PropagatesOperationCanceledException()
    {
        var (service, sender) = CreateService(seedMachine: ValidMachine("NAS", mac: "AA:BB:CC:DD:EE:FF"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SendAsync(MachineId, cts.Token));
    }

    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (WakeOnLanService Service, RecordingSender Sender) CreateService(
        WakeOnLanTarget? seedMachine)
    {
        var storage = new SeededStorage();
        if (seedMachine is not null)
        {
            storage.Seed(
                TargetMachineStore.FileName,
                $$"""
                {
                  "SchemaVersion": 1,
                  "Machines": [
                    { "Id": "{{MachineId}}", "Name": "{{seedMachine.Name}}", "Mac": "{{seedMachine.Mac}}",
                      "Ipv4BroadcastAddress": {{ToJson(seedMachine.Ipv4BroadcastAddress)}},
                      "Port": {{seedMachine.Port?.ToString() ?? "null"}} }
                  ]
                }
                """);
        }

        var sender = new RecordingSender();
        var service = new WakeOnLanService(
            new TargetMachineManager(new TargetMachineStore(storage)),
            sender,
            // 本机活动接口 192.168.1.100/24 → 定向广播 192.168.1.255（S21-D1）。
            new WakeOnLanBroadcastPolicy(
                new FixedLocalNetworks(("192.168.1.100", "255.255.255.0"))));
        return (service, sender);
    }

    private static string ToJson(string? value)
        => value is null ? "null" : $"\"{value}\"";

    private static WakeOnLanTarget ValidMachine(
        string name,
        string mac = "AA:BB:CC:DD:EE:FF",
        string? broadcast = null,
        int? port = null) => new()
    {
        Id = MachineId,
        Name = name,
        Mac = mac,
        Ipv4BroadcastAddress = broadcast,
        Port = port
    };

    private sealed class RecordingSender : IUdpDatagramSender
    {
        public List<SentDatagram> Sent { get; } = [];

        public Exception? FailWith { get; set; }

        public async Task<UdpDatagramSendResult> SendAsync(
            byte[] datagram,
            IPAddress destination,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            Sent.Add(new SentDatagram((byte[])datagram.Clone(), destination, port));

            if (FailWith is not null)
            {
                return new UdpDatagramSendResult
                {
                    Status = UdpDatagramSendStatus.SendFailed,
                    Error = FailWith.Message
                };
            }

            return new UdpDatagramSendResult { Status = UdpDatagramSendStatus.Success };
        }
    }

    private sealed record SentDatagram(byte[] Datagram, IPAddress Destination, int Port);

    private sealed class SeededStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();

        public void Seed(string relativePath, string json)
        {
            using var document = JsonDocument.Parse(json);
            _documents[relativePath] = document.RootElement.Clone();
        }

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!_documents.TryGetValue(relativePath, out var element))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            return Task.FromResult(new StorageReadResult<T>
            {
                Status = StorageReadStatus.Success,
                Value = element.Deserialize<T>()
            });
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
            => Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
    }
}
