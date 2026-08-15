using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.3 auto-start tests. All registry interactions go through the
/// injectable IRegistryRunKeyStore abstraction backed by an in-memory fake;
/// these tests NEVER touch the real user registry, real power or startup
/// folders. Executed by the user in a normal PowerShell environment.
/// </summary>
public sealed class S12_3AutoStartTests
{
    private const string ExeFileName = "AutoShutdown.App.exe";

    // ---- 1. 默认状态只读且零写入 ----

    [Fact]
    public void DefaultState_IsReadOnly_Disabled_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore();

        var service = new AutoStartService(store, () => exe);
        var status = service.GetStatus();

        Assert.Equal(AutoStartStatus.Disabled, status);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    // ---- 2. 不存在时返回 Disabled ----

    [Fact]
    public void GetStatus_WhenValueMissing_ReturnsDisabled()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(initialValue: null);

        var status = new AutoStartService(store, () => exe).GetStatus();

        Assert.Equal(AutoStartStatus.Disabled, status);
    }

    // ---- 3. 正确值返回 Enabled ----

    [Fact]
    public void GetStatus_WhenValueMatchesCurrentPath_ReturnsEnabled()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(exe));

        var status = new AutoStartService(store, () => exe).GetStatus();

        Assert.Equal(AutoStartStatus.Enabled, status);
    }

    // ---- 4. 比较正确处理引号、大小写和规范化绝对路径 ----

    [Fact]
    public void GetStatus_WhenValueHasQuotesOrCaseDifference_ComparesNormalized()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);

        // 引号包裹 + 大小写不同的同路径，必须判定为已启用。
        var store = new FakeRunKeyStore(Quote(exe.ToUpperInvariant()));
        var status = new AutoStartService(store, () => exe).GetStatus();

        Assert.Equal(AutoStartStatus.Enabled, status);
    }

    // ---- 4. 旧路径返回 PathMismatch 且不自动修复 ----

    [Fact]
    public void GetStatus_WhenOldPath_ReturnsPathMismatch_WithoutAutoRepair()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var oldPath = Path.Combine(directory.Path, "OldVersion", "AutoShutdown.Old.exe");
        var store = new FakeRunKeyStore(Quote(oldPath));

        var service = new AutoStartService(store, () => exe);

        Assert.Equal(AutoStartStatus.PathMismatch, service.GetStatus());
        Assert.Equal(AutoStartStatus.PathMismatch, service.GetStatus());
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    // ---- 5. 无效值返回 InvalidValue（含空字符串、空白、纯引号、无法解析内容） ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("bad\0path")]
    public void GetStatus_WhenValueInvalid_ReturnsInvalidValue(string invalidValue)
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(invalidValue);

        var status = new AutoStartService(store, () => exe).GetStatus();

        Assert.Equal(AutoStartStatus.InvalidValue, status);
    }

    // ---- 6. Enable 写入一次且路径带完整引号 ----

    [Fact]
    public void Enable_WritesOnce_WithFullyQuotedPath()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore();

        var result = new AutoStartService(store, () => exe).Enable();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Enabled, result.Status);
        Assert.Equal(1, store.SetCount);
        Assert.Equal(Quote(Path.GetFullPath(exe)), store.LastWrittenValue);
    }

    // ---- 7. Enable 已启用时幂等、无重复写入 ----

    [Fact]
    public void Enable_WhenAlreadyEnabled_IsIdempotent_NoExtraWrite()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(exe));

        var result = new AutoStartService(store, () => exe).Enable();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Enabled, result.Status);
        Assert.Equal(0, store.SetCount);
    }

    // ---- Enable 遇路径异常时显式覆盖（用户主动启用=明确意图） ----

    [Fact]
    public void Enable_WhenPathMismatch_ExplicitEnableOverwritesOldPath()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(Path.Combine(directory.Path, "old.exe")));

        var result = new AutoStartService(store, () => exe).Enable();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Enabled, result.Status);
        Assert.Equal(1, store.SetCount);
        Assert.Equal(Quote(Path.GetFullPath(exe)), store.LastWrittenValue);
    }

    // ---- 8. Disable 删除固定项一次 ----

    [Fact]
    public void Disable_DeletesFixedValueOnce()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(exe));

        var result = new AutoStartService(store, () => exe).Disable();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Disabled, result.Status);
        Assert.Equal(1, store.DeleteCount);
        Assert.Equal(0, store.SetCount);
    }

    // ---- 9. Disable 不存在时幂等成功 ----

    [Fact]
    public void Disable_WhenValueMissing_IsIdempotentSuccess()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(initialValue: null);

        var result = new AutoStartService(store, () => exe).Disable();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Disabled, result.Status);
        Assert.Equal(0, store.DeleteCount);
    }

    // ---- 10. Repair 仅修改本软件固定项 ----

    [Fact]
    public void Repair_OnlyRewritesFixedValue_WithCurrentQuotedPath()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var oldPath = Path.Combine(directory.Path, "Old", "AutoShutdown.Old.exe");
        var store = new FakeRunKeyStore(Quote(oldPath));

        var result = new AutoStartService(store, () => exe).Repair();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Enabled, result.Status);
        Assert.Equal(1, store.SetCount);
        Assert.Equal(Quote(Path.GetFullPath(exe)), store.LastWrittenValue);
    }

    // ---- 10b. Repair 状态门：仅 PathMismatch/InvalidValue 允许修复，其余零写入 ----

    [Fact]
    public void Repair_WhenDisabled_Rejects_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(initialValue: null);

        var result = new AutoStartService(store, () => exe).Repair();

        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeRepairNotAllowed, result.ResultCode);
        Assert.Contains("未启用", result.Message);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void Repair_WhenAlreadyEnabled_Rejects_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(exe));

        var result = new AutoStartService(store, () => exe).Repair();

        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeRepairNotAllowed, result.ResultCode);
        Assert.Contains("已正确启用", result.Message);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void Repair_WhenStatusUnavailable_Rejects_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(throwOnGet: () => true);

        var service = new AutoStartService(store, () => exe);
        var result = service.Repair();

        Assert.Equal(AutoStartStatus.Unavailable, service.GetStatus());
        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeRepairNotAllowed, result.ResultCode);
        Assert.Contains("无法确认", result.Message);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void Repair_WhenInvalidValue_AllowedAndFixes()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore("   ");

        var result = new AutoStartService(store, () => exe).Repair();

        Assert.True(result.Succeeded);
        Assert.Equal(AutoStartStatus.Enabled, result.Status);
        Assert.Equal(1, store.SetCount);
        Assert.Equal(Quote(Path.GetFullPath(exe)), store.LastWrittenValue);
    }

    // ---- 11. 路径缺失/非 EXE/为空时拒绝启用且零写入 ----

    [Fact]
    public void Enable_WhenPathFileMissing_Rejects_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var missing = Path.Combine(directory.Path, "Missing.exe");
        var store = new FakeRunKeyStore();

        var result = new AutoStartService(store, () => missing).Enable();

        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodePathUnavailable, result.ResultCode);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void Enable_WhenPathNotExe_Rejects_ZeroWrites()
    {
        using var directory = new TempDirectory();
        var notExe = Path.Combine(directory.Path, "notanexe.txt");
        File.WriteAllText(notExe, string.Empty);
        var store = new FakeRunKeyStore();

        var result = new AutoStartService(store, () => notExe).Enable();

        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodePathUnavailable, result.ResultCode);
        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public void Enable_WhenPathNull_Rejects_ZeroWrites()
    {
        var store = new FakeRunKeyStore();

        var result = new AutoStartService(store, () => null).Enable();

        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodePathUnavailable, result.ResultCode);
        Assert.Equal(0, store.SetCount);
    }

    // ---- 12. 存储读取、写入、删除异常均安全返回失败结果 ----

    [Fact]
    public void GetStatus_WhenStoreReadThrows_ReturnsUnavailable_WithoutThrowing()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(throwOnGet: () => true);

        var service = new AutoStartService(store, () => exe);
        var exception = Record.Exception(() => service.GetStatus());

        Assert.Null(exception);
        Assert.Equal(AutoStartStatus.Unavailable, service.GetStatus());
    }

    [Fact]
    public void Enable_WhenStoreWriteThrows_ReturnsFailure_WithoutThrowing()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(throwOnSet: () => true);

        var service = new AutoStartService(store, () => exe);
        AutoStartOperationResult? result = null;
        var exception = Record.Exception(() => result = service.Enable());

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeEnableFailed, result.ResultCode);
        Assert.Equal(1, store.SetCount);
    }

    [Fact]
    public void Disable_WhenStoreDeleteThrows_ReturnsFailure_WithoutThrowing()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(initialValue: Quote(exe), throwOnDelete: () => true);

        var service = new AutoStartService(store, () => exe);
        AutoStartOperationResult? result = null;
        var exception = Record.Exception(() => result = service.Disable());

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeDisableFailed, result.ResultCode);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public void Repair_WhenStoreWriteThrows_ReturnsFailure_WithoutThrowing()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        // 状态须为 PathMismatch 才会进入写入分支并触发写异常。
        var store = new FakeRunKeyStore(
            initialValue: Quote(Path.Combine(directory.Path, "Old", "AutoShutdown.Old.exe")),
            throwOnSet: () => true);

        var service = new AutoStartService(store, () => exe);
        AutoStartOperationResult? result = null;
        var exception = Record.Exception(() => result = service.Repair());

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal(AutoStartService.ResultCodeRepairFailed, result.ResultCode);
        Assert.Equal(1, store.SetCount);
    }

    // ---- 13. UI 初始读取状态不写入 ----

    [Fact]
    public async Task Ui_InitialLoad_ReadsStatus_WithoutWriting()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore();
        var viewModel = CreateViewModel(directory.Path, store, exe);

        await viewModel.InitializeAsync();

        Assert.Equal(AutoStartStatus.Disabled, viewModel.AutoStartStatus);
        Assert.Equal("未启用", viewModel.AutoStartStatusText);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    // ---- 14. 用户取消确认时零写入 ----

    [Fact]
    public async Task Ui_Enable_WhenConfirmationDeclined_WritesNothing()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore();
        var viewModel = CreateViewModel(directory.Path, store, exe, confirmation: () => false);

        await viewModel.InitializeAsync();
        await viewModel.EnableAutoStartCommand.ExecuteAsync();

        Assert.Equal(AutoStartStatus.Disabled, viewModel.AutoStartStatus);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, store.DeleteCount);
    }

    // ---- 15. 操作完成后重新读取状态 ----

    [Fact]
    public async Task Ui_Enable_WhenConfirmed_ThenReReadsRealStatus()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore();
        var viewModel = CreateViewModel(directory.Path, store, exe, confirmation: () => true);

        await viewModel.InitializeAsync();
        Assert.Equal("未启用", viewModel.AutoStartStatusText);

        await viewModel.EnableAutoStartCommand.ExecuteAsync();

        Assert.Equal(1, store.SetCount);
        Assert.Equal(AutoStartStatus.Enabled, viewModel.AutoStartStatus);
        Assert.Equal("已启用", viewModel.AutoStartStatusText);
    }

    [Fact]
    public async Task Ui_Disable_WhenConfirmed_ThenReReadsRealStatus()
    {
        using var directory = new TempDirectory();
        var exe = CreateExe(directory);
        var store = new FakeRunKeyStore(Quote(exe));
        var viewModel = CreateViewModel(directory.Path, store, exe);

        await viewModel.InitializeAsync();
        Assert.Equal("已启用", viewModel.AutoStartStatusText);

        await viewModel.DisableAutoStartCommand.ExecuteAsync();

        Assert.Equal(1, store.DeleteCount);
        Assert.Equal(AutoStartStatus.Disabled, viewModel.AutoStartStatus);
        Assert.Equal("未启用", viewModel.AutoStartStatusText);
    }

    // ---- 16. DI 可解析唯一 IAutoStartService（不触碰真实注册表） ----

    [Fact]
    public void Di_ResolvesSingleIAutoStartService_AsAutoStartService()
    {
        using var directory = new TempDirectory();
        var services = new ServiceCollection();
        services.AddAutoShutdownServices(directory.Path);

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IAutoStartService>();

        Assert.IsType<AutoStartService>(service);
        Assert.Same(service, provider.GetRequiredService<IAutoStartService>());
    }

    // ---- 17. IPowerService 通过 GuardedPowerService 解析（默认测试模式走 Fake） ----

    [Fact]
    public void Di_IPowerService_ResolvesToGuardedPowerService()
    {
        using var directory = new TempDirectory();
        var services = new ServiceCollection();
        services.AddAutoShutdownServices(directory.Path);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
    }

    // ---- 18. 全源码电源 DllImport 仅存在于 Win32PowerNativeApi ----

    [Fact]
    public void WholeSource_PowerDllImportsOnlyInWin32PowerNativeApi()
    {
        foreach (var file in EnumerateWholeSourceFiles())
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);

            if (name == "Win32PowerNativeApi.cs")
            {
                Assert.Contains("DllImport", content);
            }
            else
            {
                Assert.DoesNotContain("DllImport", content);
                Assert.DoesNotContain("ExitWindowsEx", content);
                Assert.DoesNotContain("SetSuspendState", content);
            }

            Assert.DoesNotContain("shutdown.exe", content);
            Assert.DoesNotContain("Process.Start", content);
        }
    }

    // ---- Helpers ----

    private static MainWindowViewModel CreateViewModel(
        string dataRoot,
        IRegistryRunKeyStore store,
        string exePath,
        Func<bool>? confirmation = null)
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                CurrentInstance = null,
                LastUpdatedAt = new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero)
            }
        };
        var config = new StubConfigurationService(new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        });
        var clock = new FakeClock(new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero));
        var autoStart = new AutoStartService(store, () => exePath);
        return new MainWindowViewModel(engine, config, clock, new NullLogger(), autoStart, confirmation);
    }

    private static string CreateExe(TempDirectory directory)
    {
        var exe = Path.Combine(directory.Path, ExeFileName);
        File.WriteAllText(exe, string.Empty);
        return exe;
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private static IEnumerable<string> EnumerateWholeSourceFiles()
    {
        var appRoot = FindAppSourceRoot();
        var coreRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "AutoShutdown.Core"));
        return Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source directory was not found.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "autoshutdown-s123-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup must never fail the test.
            }
        }
    }

    private sealed class FakeRunKeyStore : IRegistryRunKeyStore
    {
        private readonly Func<bool> _throwOnGet;
        private readonly Func<bool> _throwOnSet;
        private readonly Func<bool> _throwOnDelete;

        public FakeRunKeyStore(
            string? initialValue = null,
            Func<bool>? throwOnGet = null,
            Func<bool>? throwOnSet = null,
            Func<bool>? throwOnDelete = null)
        {
            Value = initialValue;
            _throwOnGet = throwOnGet ?? (() => false);
            _throwOnSet = throwOnSet ?? (() => false);
            _throwOnDelete = throwOnDelete ?? (() => false);
        }

        public string? Value { get; private set; }

        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? LastWrittenValue { get; private set; }

        public string? GetValue()
        {
            GetCount++;
            if (_throwOnGet())
            {
                throw new IOException("read failed");
            }

            return Value;
        }

        public void SetValue(string value)
        {
            SetCount++;
            if (_throwOnSet())
            {
                throw new IOException("write failed");
            }

            Value = value;
            LastWrittenValue = value;
        }

        public void DeleteValue()
        {
            DeleteCount++;
            if (_throwOnDelete())
            {
                throw new IOException("delete failed");
            }

            Value = null;
        }
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public StubConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
