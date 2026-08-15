using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Infrastructure.Power;

/// <summary>
/// 双闸门电源路由。实现 <see cref="IPowerService"/>，是所有电源请求的统一入口：
///
/// 1. 配置闸门：加载配置。TestMode=true 时无条件走 <see cref="FakePowerService"/>；
///    否则要求 RealPowerEnabled=true，否则拒绝。
/// 2. 确认闸门：真实电源请求必须携带 RealPowerConfirmed=true（创建任务时用户确认后
///    持久化在任务实例上），否则拒绝。
///
/// 默认配置（TestMode=true、RealPowerEnabled=false）下行为与直接使用 FakePowerService 完全一致，
/// 真实电源零调用。
/// </summary>
public sealed class GuardedPowerService : IPowerService
{
    private readonly IConfigurationService _configurationService;
    private readonly FakePowerService _fake;
    private readonly IPowerService _real;

    public GuardedPowerService(
        IConfigurationService configurationService,
        FakePowerService fake,
        IPowerService real)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(fake);
        ArgumentNullException.ThrowIfNull(real);
        _configurationService = configurationService;
        _fake = fake;
        _real = real;
    }

    public async Task<PowerResult> ExecuteAsync(
        PowerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ConfigurationLoadResult load;
        try
        {
            load = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 配置不可用：安全拒绝，绝不调用真实电源。
            return Rejected("ConfigurationUnavailable", "The configuration is not available.");
        }

        if (load.Status != ConfigurationLoadStatus.Success
            || load.Config is null)
        {
            return Rejected("ConfigurationUnavailable", "The configuration is not available.");
        }

        var config = load.Config;

        // 闸门一：TestMode=true 无条件走 Fake（即使 RealPowerEnabled 被误设）。
        if (config.TestMode)
        {
            return await _fake.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // TestMode=false：要求发布配置显式开启 RealPowerEnabled。
        if (!config.RealPowerEnabled)
        {
            return Rejected("RealPowerNotEnabled", "Real power is not enabled in the configuration.");
        }

        // 闸门二：真实电源必须由用户明确确认（创建任务时持久化的实例确认标记）。
        if (!request.RealPowerConfirmed)
        {
            return Rejected(
                "RealPowerConfirmationMissing",
                "Real power execution requires an explicit user confirmation.");
        }

        return await _real.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static PowerResult Rejected(string reason, string message) => new()
    {
        Outcome = PowerOutcome.Rejected,
        Message = message,
        NativeErrorCode = null
    };
}
