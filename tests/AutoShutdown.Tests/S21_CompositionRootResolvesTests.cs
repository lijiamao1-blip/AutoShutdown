using AutoShutdown.App.AppHost;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.WakeOnLan;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21 组合根解析冒烟：完整 DI 图可解析出 S21 新增的设置页分区视图模型与
/// MainWindowViewModel（启动接线）。不触碰真实 timer、不发送任何 UDP。
/// </summary>
public sealed class S21_CompositionRootResolvesTests
{
    [Fact]
    public void CompositionRoot_ResolvesWolAndRtcSections_AndMainWindowViewModel()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        var wolSection = provider.GetRequiredService<WolTargetsSectionViewModel>();
        var rtcSection = provider.GetRequiredService<RtcStatusSectionViewModel>();
        var mainViewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.NotNull(wolSection);
        Assert.NotNull(rtcSection);
        Assert.Same(wolSection, mainViewModel.WolTargetsSection);
        Assert.Same(rtcSection, mainViewModel.RtcStatusSection);
    }

    [Fact]
    public void CompositionRoot_ResolvesWolTaskChain_AndRtcService_ThroughHandlerAndPipeline()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IWakeOnLanService>());
        Assert.NotNull(provider.GetRequiredService<IWakeOnLanTaskExecutor>());
        Assert.NotNull(provider.GetRequiredService<IRtcWakeService>());
        Assert.NotNull(provider.GetRequiredService<IScheduledTaskHandler>());
        Assert.NotNull(provider.GetRequiredService<IPrePipelineRunner>());
    }
}
