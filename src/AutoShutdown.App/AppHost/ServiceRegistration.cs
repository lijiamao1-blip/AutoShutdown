using System.IO;
using System.Windows.Threading;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.CloseApps;
using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.App.Infrastructure.Idle;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.App.Infrastructure.Rtc;
using AutoShutdown.App.Infrastructure.TaskScheduler;
using AutoShutdown.App.Infrastructure.WakeOnLan;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Office;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Recovery;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.WakeOnLan;
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
        // 解析时不触碰 COM）；其逐应用硬超时经独立辅助进程实现，辅助进程由
        // IOfficeSaveHelperLauncher（唯一进程启动网关）启动。
        services.AddSingleton<IOfficeSaveHelperLauncher, OfficeSaveHelperLauncher>();
        services.AddSingleton<IOfficeAutomation, ComOfficeAutomation>();

        // S18：CloseApps 关闭应用。真实网关走托管 System.Diagnostics.Process
        // （WM_CLOSE 优雅关闭 + PID/启动时间复核强杀），无 P/Invoke、无进程启动、
        // 无 shell；强杀仅逐目标 opt-in。CloseAppsAction 默认 Block（fail-closed）。
        services.AddSingleton<IProcessManager, DiagnosticProcessManager>();
        services.AddSingleton<IAppWindowManager, DiagnosticAppWindowManager>();
        services.AddSingleton<CloseAppsService>(provider =>
            new CloseAppsService(
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IProcessManager>(),
                provider.GetRequiredService<IAppWindowManager>()));

        // S19：RunCommands 关机前命令。真实进程启动边界为 CommandRunner（托管
        // ProcessStartInfo.ArgumentList + Process.Kill(entireProcessTree)），无 P/Invoke、
        // 无 cmd/powershell/shell 字符串拼接；本地白名单默认空（默认拒绝）。
        // RunCommandsAction 默认 Block（fail-closed），逐命令 block/continue。
        // S19-D1：进程树受控快照网关（WMI 父子关系 + PID/启动时间身份）先于 Runner 注册，
        // CommandRunner 经 IProcessTreeGateway 完成整树枚举/存活确认（可测试、可审计）。
        services.AddSingleton<IProcessTreeGateway, DiagnosticProcessTreeGateway>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<RunCommandsService>(provider =>
            new RunCommandsService(
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<ICommandRunner>()));

        // S21：一次性 RTC 唤醒。真实实现基于 waitable timer（P/Invoke 收敛于
        // Win32PowerNativeApi.cs），能力诚实声明（仅可唤醒睡眠/休眠，无法唤醒完全关机）。
        services.AddSingleton<IRtcWakeNativeApi, Win32RtcWakeNativeApi>();
        services.AddSingleton<IRtcWakeService, Win32RtcWakeService>();

        // S21：WoL 目标机器 + 发送 + 任务执行。只向用户显式配置的局域网目标发送
        // Magic Packet；不扫描、不自动发现、不访问公网。执行器经调度 handler 派发，
        // 失败抛 ScheduledTaskHandlingException → 实例 Faulted（绝不伪造成功）。
        // S21-D1：网络边界 —— 仅允许默认有限广播与本机活动接口定向广播发送；
        // 真实接口枚举（LocalNetworkInterfaces）只读本机网络配置，枚举失败 fail-closed。
        services.AddSingleton<TargetMachineStore>();
        services.AddSingleton<TargetMachineManager>();
        services.AddSingleton<IUdpDatagramSender, UdpDatagramSender>();
        services.AddSingleton<IWakeOnLanLocalNetworks, LocalNetworkInterfaces>();
        services.AddSingleton<WakeOnLanBroadcastPolicy>();
        services.AddSingleton<IWakeOnLanService, WakeOnLanService>();
        services.AddSingleton<IWakeOnLanTaskExecutor, WakeOnLanTaskExecutor>();

        // S22：Windows 任务计划程序单向同步（outbound）。本地 TaskCollection 是唯一事实源，
        // 只外发（创建/更新/删除/查询），绝不 inbound；外部任务只回调本地应用（--trigger-task
        // <id>），不携带任何电源命令。同步设置（task-sync.json）默认关闭；启用前必须先持久化
        // 配置（fail-closed）。协调器在 ctor 订阅本地事件，因此必须在调度引擎加载任务前解析
        // （经 ApplicationLifetimeCoordinator 构造），保证启动即纳入事实源变更。
        services.AddSingleton<TaskSyncSettingsStore>(provider =>
            new TaskSyncSettingsStore(provider.GetRequiredService<IStorage>()));
        services.AddSingleton<ITaskSchedulerAdapter, WinTaskSchedulerAdapter>();
        services.AddSingleton<TaskSchedulerMapper>();
        services.AddSingleton<TaskSyncService>();
        services.AddSingleton<TaskSyncCoordinator>(provider =>
        {
            var coordinator = new TaskSyncCoordinator(
                provider.GetRequiredService<ITaskService>(),
                provider.GetRequiredService<TaskSyncService>(),
                provider.GetRequiredService<IClock>(),
                Environment.ProcessPath
                    ?? throw new InvalidOperationException("No process path is available."));

            // 启动即按存储恢复开关（fail-closed：缺失/损坏/非法一律关闭）。协调器在调度引擎
            // 加载任务前解析，保证初始加载触发的防抖同步在「同步关闭」时绝不创建外部任务。
            // FileStorage 内部全程 ConfigureAwait(false)，此处同步等待不会死锁。
            var load = provider.GetRequiredService<TaskSyncSettingsStore>()
                .LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            coordinator.Enabled = load.Status == TaskSyncSettingsLoadStatus.Success
                && load.Document?.Enabled == true;
            return coordinator;
        });
        services.AddSingleton<ExternalTriggerScheduleGate>();
        // tasks.json 的独立文档存储：SchedulerEngine 内部自建实例（不可注入），此处单独注册，
        // 供外部触发服务严格读取本地事实源（区分 NotFound/Corrupt/Invalid/UnsupportedVersion）。
        services.AddSingleton<TasksDocumentStore>(provider =>
            new TasksDocumentStore(provider.GetRequiredService<IStorage>()));
        services.AddSingleton<ExternalTaskTriggerService>();

        services.AddSingleton<IPrePipelineRunner>(provider =>
            new PrePipelineRunner(
            [
                new OfficeSaveAction(provider.GetRequiredService<IOfficeAutomation>()),
                new RunCommandsAction(provider.GetRequiredService<RunCommandsService>()),
                new CloseAppsAction(provider.GetRequiredService<CloseAppsService>()),
                new RtcWakeAction(
                    provider.GetRequiredService<IRtcWakeService>(),
                    provider.GetRequiredService<IConfigurationService>())
            ]));

        // S20：无人值守。版本化授权记录（unattended.json）+ 策略服务（fail-closed）+ 倒计时边界
        // 等效确认评估器。远程层（S23，未实现）不得写入；本地启用/撤销经 UI 二次确认。
        services.AddSingleton<UnattendedAuthorizationStore>(provider =>
            new UnattendedAuthorizationStore(provider.GetRequiredService<IStorage>()));
        services.AddSingleton<IUnattendedPolicyService>(provider =>
            new UnattendedPolicyService(
                provider.GetRequiredService<UnattendedAuthorizationStore>(),
                provider.GetRequiredService<IClock>()));
        services.AddSingleton<UnattendedConfirmationEvaluator>();

        services.AddSingleton<ShutdownWorkflow>(provider =>
            new ShutdownWorkflow(
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IPowerService>(),
                provider.GetRequiredService<IPrePipelineRunner>(),
                provider.GetRequiredService<IUnattendedPolicyService>(),
                provider.GetRequiredService<UnattendedConfirmationEvaluator>(),
                provider.GetRequiredService<IRtcWakeService>()));
        services.AddSingleton<IShutdownWorkflow>(provider =>
            new LoggingShutdownWorkflowDecorator(
                provider.GetRequiredService<ShutdownWorkflow>(),
                provider.GetRequiredService<IApplicationLogger>()));
        services.AddSingleton<IScheduledTaskHandler>(provider =>
            new ShutdownScheduledTaskHandler(
                provider.GetRequiredService<IShutdownWorkflow>(),
                provider.GetRequiredService<IWakeOnLanTaskExecutor>()));
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
            IdleShutdownRule.GlobalDefaultThreshold,
            provider.GetRequiredService<IUnattendedPolicyService>(),
            provider.GetRequiredService<UnattendedConfirmationEvaluator>()));

        // S21：设置页分区视图模型（WoL 目标机器管理 + RTC 唤醒能力状态）。测试发送只由
        // 用户逐目标点击触发；RTC 分区仅能力探测，不设置任何真实唤醒定时器。
        services.AddSingleton<WolTargetsSectionViewModel>(provider =>
            new WolTargetsSectionViewModel(
                provider.GetRequiredService<TargetMachineManager>(),
                provider.GetRequiredService<IWakeOnLanService>()));
        services.AddSingleton<RtcStatusSectionViewModel>(provider =>
            new RtcStatusSectionViewModel(
                provider.GetRequiredService<IRtcWakeService>()));

        // S22：任务计划程序同步分区。Enable/Disable 副作用（先清理再持久化）都在 VM 内完成；
        // 配置损坏/非法/版本过高一律保持关闭（fail-closed，绝不静默启用）。
        services.AddSingleton<TaskSyncSectionViewModel>(provider =>
            new TaskSyncSectionViewModel(
                provider.GetRequiredService<TaskSyncSettingsStore>(),
                provider.GetRequiredService<TaskSyncCoordinator>(),
                provider.GetRequiredService<ITaskSchedulerAdapter>(),
                provider.GetRequiredService<IClock>(),
                log: message => provider
                    .GetRequiredService<IApplicationLogger>()
                    .Info("TaskSync", message)));

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

        services.AddSingleton<MainWindowViewModel>(provider =>
            new MainWindowViewModel(
                provider.GetRequiredService<ISchedulerEngine>(),
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IApplicationLogger>(),
                provider.GetRequiredService<IAutoStartService>(),
                unattendedPolicy: provider.GetRequiredService<IUnattendedPolicyService>(),
                wolTargetsSection: provider.GetRequiredService<WolTargetsSectionViewModel>(),
                rtcStatusSection: provider.GetRequiredService<RtcStatusSectionViewModel>(),
                taskSyncSection: provider.GetRequiredService<TaskSyncSectionViewModel>()));
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
