using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 组合根解析冒烟：完整 DI 图可解析出任务计划程序单向同步的整条链
/// （设置存储 → 适配器 → 映射 → 同步服务 → 协调器 → 触发服务 → 分区 VM → 主窗口 VM）。
/// 不触碰真实任务计划程序（仅解析，不查询/不注册/不删除），不执行任何电源。
/// </summary>
public sealed class S22_CompositionRootResolvesTests
{
    [Fact]
    public async Task CompositionRoot_ResolvesTaskSyncSection_AndMainWindowViewModel()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        // ActivationPipeServer 只实现 IAsyncDisposable：容器须走异步释放（同 App.xaml.cs）。
        await using var provider = services.BuildServiceProvider();

        var section = provider.GetRequiredService<TaskSyncSectionViewModel>();
        var mainViewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.NotNull(section);
        Assert.Same(section, mainViewModel.TaskSyncSection);
    }

    [Fact]
    public async Task CompositionRoot_ResolvesTaskSyncChain_AsSingletons()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<TaskSyncSettingsStore>());
        Assert.NotNull(provider.GetRequiredService<ITaskSchedulerAdapter>());
        Assert.NotNull(provider.GetRequiredService<TaskSchedulerMapper>());
        Assert.NotNull(provider.GetRequiredService<TaskSyncService>());
        Assert.NotNull(provider.GetRequiredService<ExternalTriggerScheduleGate>());
        Assert.NotNull(provider.GetRequiredService<TasksDocumentStore>());
        Assert.NotNull(provider.GetRequiredService<ExternalTaskTriggerService>());

        // 协调器与文档存储为单例：订阅/读取状态全局唯一，防止多订阅或多文档实例。
        Assert.Same(
            provider.GetRequiredService<TaskSyncCoordinator>(),
            provider.GetRequiredService<TaskSyncCoordinator>());
        Assert.Same(
            provider.GetRequiredService<TasksDocumentStore>(),
            provider.GetRequiredService<TasksDocumentStore>());
    }

    [Fact]
    public async Task CompositionRoot_ResolvesPipeServer_WithExternalTriggerService()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        await using var provider = services.BuildServiceProvider();

        // ActivationPipeServer 依赖可选 ExternalTaskTriggerService；解析即验证外部触发链
        // （TasksDocumentStore → gate → handler → id 生成器）已在组合根完整接线。
        var pipeServer = provider.GetRequiredService<ActivationPipeServer>();
        Assert.NotNull(pipeServer);
        Assert.NotNull(provider.GetRequiredService<ExternalTaskTriggerService>());
    }
}
