using System.Text;
using System.Text.RegularExpressions;
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
using AutoShutdown.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.2 log system tests. File I/O tests use isolated temp directories and
/// never touch the real user log directory, the real registry or real power.
/// These tests are executed by the user in a normal PowerShell environment.
/// </summary>
public sealed class S12LoggingTests
{
    private const string LinePattern =
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2} \[(Debug|Information|Warning|Error)\] \S+ .+$";

    // ---- 1/2. 正常写入 UTF-8 中文日志，行含时间/级别/事件名/消息 ----

    [Fact]
    public void Write_ChineseUtf8_LineHasTimestampLevelEventAndMessage()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path);

        logger.Info("ApplicationStarting", "中文日志测试，应用启动。");

        var content = File.ReadAllText(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log"));
        Assert.DoesNotContain('\uFFFD', content);
        Assert.Contains("中文日志测试，应用启动。", content);
        Assert.Contains("ApplicationStarting", content);
        Assert.Contains("[Information]", content);

        var line = content.TrimEnd('\r', '\n').Split('\n')[0];
        Assert.Matches(LinePattern, line.TrimEnd('\r'));
    }

    // ---- 3. 多线程并发写入不产生半行或交叉行 ----

    [Fact]
    public void Write_ConcurrentWrites_NoInterleavedLines()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path);

        const int Threads = 8;
        const int LinesPerThread = 25;
        var messages = new string[Threads];

        for (var t = 0; t < Threads; t++)
        {
            messages[t] = "线程" + t + "专用消息-" + new string((char)('A' + t), 12);
        }

        Parallel.For(0, Threads, t =>
        {
            for (var i = 0; i < LinesPerThread; i++)
            {
                logger.Info("Concurrent", messages[t]);
            }
        });

        var lines = File.ReadAllLines(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log"));
        Assert.Equal(Threads * LinesPerThread, lines.Length);

        for (var t = 0; t < Threads; t++)
        {
            Assert.Equal(LinesPerThread, lines.Count(line => line.EndsWith(messages[t], StringComparison.Ordinal)));
        }
    }

    // ---- 4. 达到大小上限后轮换 ----

    [Fact]
    public void Write_WhenMaxSizeReached_RotatesToNumberedFile()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path, maxFileBytes: 512);

        // Each line is about 355 bytes, so the second write must rotate.
        logger.Info("EventA", new string('a', 300));
        logger.Info("EventB", new string('b', 300));

        var primary = Path.Combine(directory.Path, "autoshutdown-" + Today + ".log");
        var first = Path.Combine(directory.Path, "autoshutdown-" + Today + ".1.log");

        Assert.True(File.Exists(primary), "A fresh primary file must exist.");
        Assert.True(File.Exists(first), "A rotated .1 file must exist.");

        Assert.Contains("EventA", File.ReadAllText(first));
        Assert.Contains("EventB", File.ReadAllText(primary));
    }

    // ---- 5. 轮换文件编号不覆盖现有文件 ----

    [Fact]
    public void Rotate_DoesNotOverwriteExistingNumberedFile()
    {
        using var directory = new TempDirectory();
        var primary = Path.Combine(directory.Path, "autoshutdown-" + Today + ".log");
        var first = Path.Combine(directory.Path, "autoshutdown-" + Today + ".1.log");
        var second = Path.Combine(directory.Path, "autoshutdown-" + Today + ".2.log");

        File.WriteAllText(primary, new string('p', 300), new UTF8Encoding(false));
        File.WriteAllText(first, "OLD-1", new UTF8Encoding(false));

        using var logger = new FileApplicationLogger(directory.Path, maxFileBytes: 128);
        logger.Info("EventX", new string('x', 100));

        // The pre-existing .1 file must be shifted to .2, never overwritten.
        Assert.Contains("OLD-1", File.ReadAllText(second));
        // The previous primary content is preserved as .1.
        Assert.Contains(new string('p', 300), File.ReadAllText(first));
        // The fresh line lands in the new primary file.
        Assert.Contains("EventX", File.ReadAllText(primary));
    }

    // ---- 6. 删除超过 14 天且名称合法的日志 ----

    [Fact]
    public void Retention_DeletesOldLegitimateFiles()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var retention = new LogRetentionService(directory.Path, new FakeClock(now));

        var oldFile = Path.Combine(directory.Path, "autoshutdown-2026-07-25.log");
        var olderRoll = Path.Combine(directory.Path, "autoshutdown-2026-07-10.2.log");
        File.WriteAllText(oldFile, "old", new UTF8Encoding(false));
        File.WriteAllText(olderRoll, "older", new UTF8Encoding(false));

        retention.RunOnce();

        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(olderRoll));
    }

    // ---- 7. 保留 14 天内日志 ----

    [Fact]
    public void Retention_KeepsRecentFiles()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var retention = new LogRetentionService(directory.Path, new FakeClock(now));

        var today = Path.Combine(directory.Path, "autoshutdown-2026-08-11.log");
        var boundary = Path.Combine(directory.Path, "autoshutdown-2026-07-28.log");
        File.WriteAllText(today, "today", new UTF8Encoding(false));
        File.WriteAllText(boundary, "boundary", new UTF8Encoding(false));

        retention.RunOnce();

        Assert.True(File.Exists(today));
        Assert.True(File.Exists(boundary));
    }

    // ---- 8. 不删除无关文件 ----

    [Fact]
    public void Retention_IgnoresUnrelatedFiles()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var retention = new LogRetentionService(directory.Path, new FakeClock(now));

        var unrelated = Path.Combine(directory.Path, "readme.txt");
        var dump = Path.Combine(directory.Path, "autoshutdown-2026-07-01.dmp");
        File.WriteAllText(unrelated, "keep", new UTF8Encoding(false));
        File.WriteAllText(dump, "keep", new UTF8Encoding(false));

        retention.RunOnce();

        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(dump));
    }

    // ---- 9. 日期无法识别的日志保留 ----

    [Fact]
    public void Retention_KeepsUnrecognizableNames()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var retention = new LogRetentionService(directory.Path, new FakeClock(now));

        var weird = Path.Combine(directory.Path, "autoshutdown-not-a-date.log");
        File.WriteAllText(weird, "keep", new UTF8Encoding(false));

        retention.RunOnce();

        Assert.True(File.Exists(weird));
    }

    // ---- 10. 日志目录不可写时业务调用不抛异常 ----

    [Fact]
    public void Write_WhenDirectoryUnusable_DoesNotThrow()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Combine(directory.Path, "blocker");
        File.WriteAllText(blocker, "this is a file, not a directory", new UTF8Encoding(false));
        var unusable = Path.Combine(blocker, "logs");

        using var logger = new FileApplicationLogger(unusable);

        var exception = Record.Exception(() =>
            logger.Info("SchedulerRunning", "这条日志无法落盘，但不得抛出。"));
        Assert.Null(exception);
    }

    // ---- 11. 写入失败不改变 SchedulerCommandResult ----

    [Fact]
    public async Task Write_Failure_DoesNotChangeSchedulerCommandResult()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Combine(directory.Path, "blocker");
        File.WriteAllText(blocker, "not a directory", new UTF8Encoding(false));

        using var logger = new FileApplicationLogger(Path.Combine(blocker, "logs"));
        var engine = new RecordingSchedulerEngine();
        var viewModel = new MainWindowViewModel(
            engine,
            new StubConfigurationService(ValidConfig()),
            new FakeClock(new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero)),
            logger,
            new NoOpAutoStartService());

        await viewModel.InitializeAsync();
        await viewModel.CreateCommand.ExecuteAsync();

        Assert.Single(engine.Commands);
        Assert.Contains("已提交", viewModel.StatusMessage);
    }

    // ---- 12. 写入失败不改变 ShutdownWorkflowResult ----

    [Fact]
    public async Task Write_Failure_DoesNotChangeShutdownWorkflowResult()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Combine(directory.Path, "blocker");
        File.WriteAllText(blocker, "not a directory", new UTF8Encoding(false));

        using var logger = new FileApplicationLogger(Path.Combine(blocker, "logs"));
        var innerResult = new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.Simulated,
            DecisionCode = ShutdownDecisionCode.Allowed,
            Message = "simulated"
        };
        var inner = new StubWorkflow(innerResult);
        var decorator = new LoggingShutdownWorkflowDecorator(inner, logger);

        var result = await decorator.ExecuteAsync(SampleInstance(), CancellationToken.None);

        Assert.Same(innerResult, result);
        Assert.Equal(ShutdownWorkflowStatus.Simulated, result.Status);
    }

    // ---- 13. 装饰器参数与返回值原样传递 ----

    [Fact]
    public async Task Decorator_PassesThroughParametersAndReturnValue()
    {
        var memory = new MemoryLogger();
        var instance = SampleInstance();
        var expected = new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.Rejected,
            DecisionCode = ShutdownDecisionCode.ActionNotAllowed,
            Message = "blocked"
        };
        var inner = new StubWorkflow(expected);
        var decorator = new LoggingShutdownWorkflowDecorator(inner, memory);

        var result = await decorator.ExecuteAsync(instance, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Same(instance, inner.ReceivedInstance);
        Assert.Equal(CancellationToken.None, inner.ReceivedCancellationToken);

        Assert.Contains(memory.Entries, e => e.EventName == "ShutdownWorkflowStarted");
        Assert.Contains(memory.Entries, e =>
            e.EventName == "ShutdownWorkflowRejected"
            && e.Message.Contains("DecisionCode=" + ShutdownDecisionCode.ActionNotAllowed));
    }

    // ---- 14. 异常由原业务按原语义传播，日志不得吞掉 ----

    [Fact]
    public async Task Decorator_PropagatesOriginalException()
    {
        var memory = new MemoryLogger();
        var inner = new ThrowingWorkflow(new InvalidOperationException("boom"));
        var decorator = new LoggingShutdownWorkflowDecorator(inner, memory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => decorator.ExecuteAsync(SampleInstance(), CancellationToken.None));

        Assert.Equal("boom", exception.Message);
        Assert.Contains(memory.Entries, e => e.EventName == "PowerServiceFailed" && e.Level == ApplicationLogLevel.Error);
    }

    // ---- 15. Release 默认不记录 Debug ----

    [Fact]
    public void Debug_IsNotWritten_WithDefaultMinLevel()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path);

        Assert.False(logger.IsEnabled(ApplicationLogLevel.Debug));
        logger.Debug("DebugDetail", "开发诊断内容");

        Assert.False(File.Exists(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log")));
    }

    // ---- 16. 日志不包含完整配置 JSON ----

    [Fact]
    public void ViewModel_ConfigurationLog_DoesNotSerializeConfigJson()
    {
        var source = File.ReadAllText(Path.Combine(FindAppSourceRoot(), "Presentation", "MainWindowViewModel.cs"));

        Assert.Contains("ConfigurationLoaded", source);
        Assert.DoesNotContain("JsonSerializer", source);
        Assert.DoesNotContain("WriteIndented", source);
    }

    // ---- 17. 不记录每秒倒计时刷新 ----

    [Fact]
    public void RefreshService_HasNoLoggingCalls()
    {
        var source = File.ReadAllText(Path.Combine(FindAppSourceRoot(), "Presentation", "DashboardRefreshService.cs"));

        Assert.DoesNotContain("IApplicationLogger", source);
        Assert.DoesNotContain("Log(", source);
    }

    // ---- 18. DI 中日志服务为 Singleton ----

    [Fact]
    public void Di_LoggerIsRegisteredAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IApplicationLogger>();
        var second = provider.GetRequiredService<IApplicationLogger>();

        Assert.Same(first, second);
    }

    // ---- 19. IPowerService 通过 GuardedPowerService 解析（默认测试模式走 Fake） ----

    [Fact]
    public void Di_IPowerServiceResolvesToGuardedPowerService()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
    }

    [Fact]
    public void ServiceRegistration_RegistersGuardedPowerServiceWithFakeAndWin32()
    {
        var source = File.ReadAllText(Path.Combine(FindAppSourceRoot(), "AppHost", "ServiceRegistration.cs"));

        Assert.Contains("AddSingleton<FakePowerService>", source);
        Assert.Contains("new GuardedPowerService(", source);
        Assert.DoesNotContain("AddSingleton<IPowerService, FakePowerService>", source);
    }

    // ---- 20. 全源码电源 DllImport 仅存在于 Win32PowerNativeApi ----

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

    // ---- 21. 修复回归：clock 抛异常时 Log 不抛 ----

    [Fact]
    public void Log_WhenClockThrows_DoesNotThrow()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(
            directory.Path,
            clock: () => throw new InvalidOperationException("clock failure"));

        var exception = Record.Exception(() =>
            logger.Info("SchedulerStarting", "时间源故障时不得抛出。"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log")));
    }

    // ---- 22. 修复回归：EventName/Message/Exception 换行只产生一行 ----

    [Fact]
    public void Format_NewlinesInEventAndMessage_ProduceSingleLine()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path);

        var exception = new InvalidOperationException("异常消息\r\n第二行");
        logger.Log(
            ApplicationLogLevel.Error,
            "多行\r事件",
            "消息第一行\n第二行",
            exception);

        var lines = File.ReadAllLines(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log"));

        Assert.Single(lines);
        var line = lines[0];
        Assert.Matches(LinePattern, line);
        Assert.Contains("\\r", line);
        Assert.Contains("\\n", line);
        Assert.Contains("多行\\r事件", line);
        Assert.Contains("消息第一行\\n第二行", line);
        Assert.Contains("异常消息\\r\\n第二行", line);
    }

    // ---- 23. 修复回归：堆栈不产生无前缀孤立行 ----

    [Fact]
    public void ExceptionStackTrace_DoesNotProduceOrphanLines()
    {
        using var directory = new TempDirectory();
        using var logger = new FileApplicationLogger(directory.Path);

        Exception captured = null!;
        try
        {
            ThrowWithStackTrace();
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        Assert.NotNull(captured.StackTrace);

        logger.Error("UnhandledException", "带堆栈的异常", captured);

        var lines = File.ReadAllLines(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log"));

        // Exactly one physical line; every line (i.e. the single line) has a prefix.
        Assert.Single(lines);
        Assert.Matches(LinePattern, lines[0]);
        Assert.Contains("System.InvalidOperationException", lines[0]);
    }

    // ---- 24. 修复回归：路径无效或目录不可枚举时 RunOnce 不抛 ----

    [Fact]
    public void RunOnce_WhenDirectoryPathInvalid_DoesNotThrow()
    {
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);

        // '<' is illegal in Windows paths; Directory.Exists throws.
        var invalid = new LogRetentionService(@"C:\bad<dir>\logs", new FakeClock(now));
        Assert.Null(Record.Exception(() => invalid.RunOnce()));

        using var directory = new TempDirectory();

        // A path whose parent component is a file cannot be enumerated.
        var blocker = Path.Combine(directory.Path, "blocker");
        File.WriteAllText(blocker, "file, not directory", new UTF8Encoding(false));
        var fileAsDirectory = new LogRetentionService(blocker, new FakeClock(now));
        Assert.Null(Record.Exception(() => fileAsDirectory.RunOnce()));
    }

    // ---- 25. 修复回归：清理失败不影响后续业务调用 ----

    [Fact]
    public void RunOnce_WhenClockThrows_DoesNotThrow_AndLaterCallsWork()
    {
        using var directory = new TempDirectory();

        // IClock.UtcNow throws -> the whole RunOnce must end safely.
        var throwing = new LogRetentionService(directory.Path, new ThrowingClock());
        Assert.Null(Record.Exception(() => throwing.RunOnce()));

        // Later business calls (logging) must keep working normally.
        using var logger = new FileApplicationLogger(directory.Path);
        Assert.Null(Record.Exception(() => logger.Info("SchedulerRunning", "清理失败后的后续调用正常。")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "autoshutdown-" + Today + ".log")));
    }

    private static void ThrowWithStackTrace() => throw new InvalidOperationException("stacked");

    // ---- Helpers ----

    private static readonly string Today = DateTimeOffset.Now.ToString("yyyy-MM-dd");

    private static TaskInstance SampleInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions =
        [
            PowerAction.Shutdown,
            PowerAction.Restart,
            PowerAction.Sleep,
            PowerAction.Hibernate
        ]
    };

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

    private static IEnumerable<string> EnumerateWholeSourceFiles()
    {
        var root = FindAppSourceRoot();
        var coreRoot = Path.GetFullPath(Path.Combine(root, "..", "AutoShutdown.Core"));
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AutoShutdownTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort cleanup for a test temp directory.
            }
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NoOpAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow => throw new InvalidOperationException("clock failure");

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MemoryLogger : IApplicationLogger
    {
        public List<(ApplicationLogLevel Level, string EventName, string Message)> Entries { get; } = [];

        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
            => Entries.Add((level, eventName, message));

        public void Dispose()
        {
        }
    }

    private sealed class StubWorkflow : IShutdownWorkflow
    {
        private readonly ShutdownWorkflowResult _result;

        public StubWorkflow(ShutdownWorkflowResult result) => _result = result;

        public TaskInstance? ReceivedInstance { get; private set; }

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<ShutdownWorkflowResult> ExecuteAsync(
            TaskInstance instance,
            CancellationToken cancellationToken)
        {
            ReceivedInstance = instance;
            ReceivedCancellationToken = cancellationToken;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingWorkflow : IShutdownWorkflow
    {
        private readonly Exception _exception;

        public ThrowingWorkflow(Exception exception) => _exception = exception;

        public Task<ShutdownWorkflowResult> ExecuteAsync(
            TaskInstance instance,
            CancellationToken cancellationToken)
            => Task.FromException<ShutdownWorkflowResult>(_exception);
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public StubConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSchedulerEngine : ISchedulerEngine
    {
        public List<SchedulerCommand> Commands { get; } = [];

        public SchedulerSnapshot Snapshot { get; set; } = new()
        {
            EngineStatus = SchedulerEngineStatus.Running,
            LastUpdatedAt = new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero)
        };

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }
}
