using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C4：WoL 目标机器管理分区（设置页）。加载严格区分 NotFound/Corrupt/Invalid/
/// UnsupportedVersion/IoFailure（损坏一律 fail-closed 清空并上报）；增删改立即原子持久化；
/// MAC 格式提示、重复名称、非法广播/端口中文报错；测试发送结果明确显示成功/失败原因。
/// 默认安全：初始清单为空，绝不自动发送。全部纯内存，无真实 UDP。
/// </summary>
public sealed class S21_WolTargetsSectionViewModelTests
{
    private const string FileName = "target-machines.json";

    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- 加载：严格区分状态，fail-closed ----

    [Fact]
    public async Task Refresh_WhenNotFound_EmptyAndNoError()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(viewModel.Targets);
        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.StatusText);
    }

    [Fact]
    public async Task Refresh_WhenSuccess_PopulatesTargets()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{TargetId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "192.168.1.255", "Port": 9 }
              ]
            }
            """);
        var viewModel = CreateViewModel(storage);

        await viewModel.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(viewModel.Targets);
        Assert.Equal("NAS", row.Name);
        Assert.Equal("AA:BB:CC:DD:EE:FF", row.Mac);
        Assert.Equal("192.168.1.255", row.BroadcastText);
        Assert.Equal(9, row.Port);
        Assert.Equal("192.168.1.255:9", row.SummaryText);
        Assert.Contains("已加载 1 台目标机器", viewModel.StatusText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Refresh_WhenCorrupt_FailClosed()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage
        {
            ReadStatus = StorageReadStatus.Corrupt
        };
        var viewModel = CreateViewModel(storage);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(viewModel.Targets);
        Assert.True(viewModel.HasError);
        Assert.Contains("目标机器配置不可用", viewModel.ErrorText);
    }

    [Fact]
    public async Task Refresh_WhenInvalid_FailClosed()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{TargetId}}", "Name": "X", "Mac": "not-a-mac" }
              ]
            }
            """);
        var viewModel = CreateViewModel(storage);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(viewModel.Targets);
        Assert.True(viewModel.HasError);
        Assert.Contains("目标机器配置不可用", viewModel.ErrorText);
    }

    [Fact]
    public async Task Refresh_WhenUnsupportedVersion_FailClosed()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 999, "Machines": [] }""");
        var viewModel = CreateViewModel(storage);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(viewModel.Targets);
        Assert.True(viewModel.HasError);
        Assert.Contains("目标机器配置不可用", viewModel.ErrorText);
    }

    [Fact]
    public async Task Refresh_WhenIoFailure_FailClosed()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage
        {
            ReadStatus = StorageReadStatus.IoFailure
        };
        var viewModel = CreateViewModel(storage);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(viewModel.Targets);
        Assert.True(viewModel.HasError);
        Assert.Contains("目标机器配置不可用", viewModel.ErrorText);
    }

    // ---- MAC 格式提示 ----

    [Fact]
    public void MacFormatHint_IsPresented()
    {
        Assert.Contains("AA:BB:CC:DD:EE:FF", WolTargetsSectionViewModel.MacFormatHint);
        Assert.Contains("大写十六进制", WolTargetsSectionViewModel.MacFormatHint);
        Assert.Contains("默认向 255.255.255.255:9 发送", WolTargetsSectionViewModel.MacFormatHint);
    }

    // ---- 添加目标 ----

    [Fact]
    public async Task AddTarget_WhenValid_PersistsAndClearsInputs()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        var manager = CreateManager(storage);
        var viewModel = CreateViewModel(storage, manager: manager, idGenerator: () => TargetId);

        viewModel.NameInput = "NAS";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";
        viewModel.BroadcastInput = "192.168.1.255";
        viewModel.PortInput = "9";

        await viewModel.AddTargetCommand.ExecuteAsync();

        var row = Assert.Single(viewModel.Targets);
        Assert.Equal(TargetId, row.Id);
        Assert.Equal("NAS", row.Name);
        Assert.Equal("AA:BB:CC:DD:EE:FF", row.Mac);
        Assert.Contains("已添加目标「NAS」", viewModel.StatusText);
        Assert.False(viewModel.HasError);
        // 输入已清空。
        Assert.Equal(string.Empty, viewModel.NameInput);
        Assert.Equal(string.Empty, viewModel.MacInput);
        Assert.Equal(string.Empty, viewModel.BroadcastInput);
        Assert.Equal(string.Empty, viewModel.PortInput);
        // 已通过 IStorage 原子写入持久化（InMemoryStorage 仅记录写入证据）。
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(FileName, Assert.Single(storage.WritePaths));
    }

    [Fact]
    public async Task AddTarget_WhenInvalidMac_ShowsChineseError()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());
        viewModel.NameInput = "NAS";
        viewModel.MacInput = "invalid";

        await viewModel.AddTargetCommand.ExecuteAsync();

        Assert.Empty(viewModel.Targets);
        Assert.True(viewModel.HasError);
        Assert.Contains("MAC 格式无效", viewModel.ErrorText);
    }

    [Fact]
    public async Task AddTarget_WhenEmptyName_ShowsChineseError()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());
        viewModel.NameInput = "   ";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";

        await viewModel.AddTargetCommand.ExecuteAsync();

        Assert.Empty(viewModel.Targets);
        Assert.Contains("名称不能为空", viewModel.ErrorText);
    }

    [Fact]
    public async Task AddTarget_WhenDuplicateName_Rejected()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());
        viewModel.NameInput = "NAS";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";
        await viewModel.AddTargetCommand.ExecuteAsync();

        viewModel.NameInput = "nas"; // 大小写不敏感重复
        viewModel.MacInput = "AA:BB:CC:DD:EE:F0";
        await viewModel.AddTargetCommand.ExecuteAsync();

        var row = Assert.Single(viewModel.Targets);
        Assert.Equal("NAS", row.Name);
        Assert.Contains("已存在同名目标机器", viewModel.ErrorText);
    }

    [Fact]
    public async Task AddTarget_WhenInvalidBroadcast_ShowsChineseError()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());
        viewModel.NameInput = "NAS";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";
        viewModel.BroadcastInput = "999.1.1.1";

        await viewModel.AddTargetCommand.ExecuteAsync();

        Assert.Empty(viewModel.Targets);
        Assert.Contains("广播地址必须是合法的 IPv4 地址", viewModel.ErrorText);
    }

    [Fact]
    public async Task AddTarget_WhenInvalidPort_ShowsChineseError()
    {
        var viewModel = CreateViewModel(new S21_TargetMachineStoreTests.InMemoryStorage());
        viewModel.NameInput = "NAS";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";
        viewModel.PortInput = "70000";

        await viewModel.AddTargetCommand.ExecuteAsync();

        Assert.Empty(viewModel.Targets);
        Assert.Contains("端口必须是 1~65535 之间的整数", viewModel.ErrorText);
    }

    [Fact]
    public async Task AddTarget_WhenStoreCorrupt_BlocksMutation()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage
        {
            ReadStatus = StorageReadStatus.Corrupt
        };
        var viewModel = CreateViewModel(storage);
        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.NameInput = "NAS";
        viewModel.MacInput = "AA:BB:CC:DD:EE:FF";
        await viewModel.AddTargetCommand.ExecuteAsync();

        Assert.Empty(viewModel.Targets);
        Assert.Equal(0, storage.WriteCount); // 绝不覆盖损坏证据
        Assert.True(viewModel.HasError);
    }

    // ---- 移除目标 ----

    [Fact]
    public async Task RemoveTarget_WhenSuccess_RemovesRowAndPersists()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{TargetId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF" }
              ]
            }
            """);
        var viewModel = CreateViewModel(storage);
        await viewModel.RefreshAsync(CancellationToken.None);
        var row = Assert.Single(viewModel.Targets);

        viewModel.RemoveTargetCommand.Execute(row);

        Assert.Empty(viewModel.Targets);
        Assert.Contains("已移除目标「NAS」", viewModel.StatusText);
        Assert.Equal(1, storage.WriteCount);
    }

    // ---- 测试发送：仅用户逐目标触发；结果明确显示 ----

    [Fact]
    public async Task TestSend_WhenSucceeds_ShowsStatusAndRecordsSentTarget()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{TargetId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF" }
              ]
            }
            """);
        var recorder = new RecordingWakeOnLanService();
        var viewModel = CreateViewModel(storage, wolService: recorder);
        await viewModel.RefreshAsync(CancellationToken.None);
        var row = Assert.Single(viewModel.Targets);

        viewModel.TestSendTargetCommand.Execute(row);

        Assert.Contains("已向「NAS」发送 Magic Packet", viewModel.StatusText);
        Assert.False(viewModel.HasError);
        Assert.Equal(TargetId, Assert.Single(recorder.SentIds));
    }

    [Fact]
    public async Task TestSend_WhenFails_ShowsFailureReason()
    {
        var storage = new S21_TargetMachineStoreTests.InMemoryStorage();
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{TargetId}}", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF" }
              ]
            }
            """);
        var recorder = new RecordingWakeOnLanService
        {
            Handler = _ => WolSendResult.Failure(WolSendStatus.SendFailed, "socket timeout")
        };
        var viewModel = CreateViewModel(storage, wolService: recorder);
        await viewModel.RefreshAsync(CancellationToken.None);
        var row = Assert.Single(viewModel.Targets);

        viewModel.TestSendTargetCommand.Execute(row);

        Assert.Contains("发送失败", viewModel.ErrorText);
        Assert.Contains("socket timeout", viewModel.ErrorText);
        Assert.Equal(string.Empty, viewModel.StatusText);
    }

    // ---- 夹具 ----

    private static TargetMachineManager CreateManager(S21_TargetMachineStoreTests.InMemoryStorage storage)
        => new(new TargetMachineStore(storage));

    private static WolTargetsSectionViewModel CreateViewModel(
        S21_TargetMachineStoreTests.InMemoryStorage storage,
        TargetMachineManager? manager = null,
        IWakeOnLanService? wolService = null,
        Func<Guid>? idGenerator = null)
        => new(
            manager ?? CreateManager(storage),
            wolService ?? new RecordingWakeOnLanService(),
            idGenerator: idGenerator);

    private sealed class RecordingWakeOnLanService : IWakeOnLanService
    {
        public List<Guid> SentIds { get; } = [];

        public Func<Guid, WolSendResult> Handler { get; set; } = _ => WolSendResult.Success("sent");

        public Task<WolSendResult> SendAsync(Guid targetMachineId, CancellationToken cancellationToken)
        {
            SentIds.Add(targetMachineId);
            return Task.FromResult(Handler(targetMachineId));
        }
    }
}
