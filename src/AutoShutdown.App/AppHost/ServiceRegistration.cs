using System.IO;
using System.Windows.Threading;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Idle;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Office;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Recovery;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace AutoShutdown.App.AppHost;

public static class ServiceRegistration
{
    public static IServiceCollection AddAutoShutdownServices(
        this IServiceCollection services,
        string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        dataRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoShutdown");

        services.AddSingleton<FakePowerService>();
        services.AddSingleton<IPowerNativeApi, Win32PowerNativeApi>();
        services.AddSingleton<Win32PowerService>();
        // 双闸门路由：TestMode=true 时行为等价于 FakePowerService（默认安全测试模式）；
        // 仅当 TestMode=false 且 RealPowerEnabled=true 且请求携带用户确认标记时才可能调用真实电源。
        services.AddSingleton<IPowerService>(provider =>
            new GuardedPowerService(
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<FakePowerService>(),
                provider.GetRequiredService<Win32PowerService>()));
        services.AddSingleton<IStorage>(_ => new FileStorage(dataRoot));
        services.AddSingleton<IConfigurationService>(provider =>
            new ConfigurationService(
                provider.GetRequiredService<IStorage>(),
                Array.Empty<IConfigurationMigration>()));
        services.AddSingleton<TaskInstanceStateMachine>();
        services.AddSingleton<ITaskInstanceStateMachine>(provider =>
            new LoggingTaskInstanceStateMachineDecorator(
                provider.GetRequiredService<TaskInstanceStateMachine>(),
                provider.GetRequiredService<IApplicationLogger>()));
        services.AddSingleton<TaskArbitrator>();
        services.AddSingleton<ITaskArbitrator>(provider =>
            new LoggingTaskArbitratorDecorator(
                provider.GetRequiredService<TaskArbitrator>(),
                provider.GetRequiredService<IApplicationLogger>()));
        services.AddSingleton<CrashRecoveryManager>();
        services.AddSingleton<INextExecutionCalculator, NextExecutionCalculator>();
        services.AddSingleton<IIdentifierGenerator, GuidIdentifierGenerator>();
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAsyncDeadline, SystemAsyncDeadline>();
        services.AddSingleton<IIdleInputSource, Win32IdleInputSource>();
        services.AddSingleton<IIdleMonitor, IdleMonitor>();

        // Application log infrastructure. The logger is a Singleton; App.xaml.cs
        // may override this registration with a pre-created instance so startup
        // events share the same file sink.
        services.AddSingleton<IApplicationLogger>(_ =>
            new FileApplicationLogger(Path.Combine(dataRoot, "logs")));
        services.AddSingleton(provider => new LogRetentionService(
            Path.Combine(dataRoot, "logs"),
            provider.GetRequiredService<IClock>()));

        // The workflow is decorated with logging only; business behavior,
        // parameters and return values are passed through untouched.
        // S17：Pre-Pipeline 挂载第一个真实 Action（OfficeSave）。固定顺序
        // OfficeSave→RunCommands→CloseApps（后续阶段追加）；Runner 串行、
        // 异常隔离、block/continue 语义由 Runner 层保证。Office 自动化走
        // 抽象 IOfficeAutomation（真机 COM 实现为 ComOfficeAutomation，惰性，
        // 解析时不触碰 COM），自动化测试注入替身。
        services.AddSingleton<IOfficeComGateway, RotOfficeComGateway>();
        services.AddSingleton<IOfficeAutomation, ComOfficeAutomation>();
        services.AddSingleton<IPrePipelineRunner>(provider =>
            new PrePipelineRunner(
            [
                new OfficeSaveAction(provider.GetRequiredService<IOfficeAutomation>())
            ]));
        services.AddSingleton<ShutdownWorkflow>(provider =>
            new ShutdownWorkflow(
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IPowerService>(),
                provider.GetRequiredService<IPrePipelineRunner>()));
        services.AddSingleton<IShutdownWorkflow>(provider =>
            new LoggingShutdownWorkflowDecorator(
                provider.GetRequiredService<ShutdownWorkflow>(),
                provider.GetRequiredService<IApplicationLogger>()));
        services.AddSingleton<IScheduledTaskHandler, ShutdownScheduledTaskHandler>();
        services.AddSingleton<ISchedulerEngine>(provider => new SchedulerEngine(
            provider.GetRequiredService<IStorage>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IAsyncDeadline>(),
            provider.GetRequiredService<ITaskService>(),
            provider.GetRequiredService<ITaskInstanceStateMachine>(),
            provider.GetRequiredService<IIdentifierGenerator>(),
            provider.GetRequiredService<IScheduledTaskHandler>(),
            provider.GetRequiredService<ITaskArbitrator>(),
            provider.GetRequiredService<IIdleMonitor>(),
            IdleShutdownRule.GlobalDefaultThreshold));

        services.AddSingleton<IWindowActivationService, WindowActivationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ActivationPipeServer>();
        services.AddSingleton<RecoveryNoticeService>();
        services.AddSingleton<ApplicationLifetimeCoordinator>();

        // Auto-start infrastructure. The registry store is lazy (no registry
        // access until GetStatus/Enable/Disable is called), so resolving the
        // service never writes anything. Default is off; only an explicit
        // user confirmation may enable it.
        services.AddSingleton<IRegistryRunKeyStore, RegistryRunKeyStore>();
        services.AddSingleton<IAutoStartService>(provider =>
            new AutoStartService(
                provider.GetRequiredService<IRegistryRunKeyStore>(),
                () => Environment.ProcessPath));

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IMainWindowFactory, MainWindowFactory>();
        services.AddSingleton<INotificationService, WpfNotificationService>();
        services.AddSingleton<NotificationCoordinator>();
        services.AddSingleton(provider => new DashboardRefreshService(
            provider.GetRequiredService<MainWindowViewModel>(),
            provider.GetRequiredService<ISchedulerEngine>(),
            provider.GetRequiredService<IClock>(),
            System.Windows.Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("No WPF dispatcher is available.")));
        return services;
    }
}
