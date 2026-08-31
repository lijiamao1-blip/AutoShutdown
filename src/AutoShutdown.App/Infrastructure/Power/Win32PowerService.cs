using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Infrastructure.Power;

/// <summary>
/// 真实电源服务。唯一把 <see cref="PowerRequest"/> 映射到
/// <see cref="IPowerNativeApi"/> 真实调用的实现。
/// 本服务不校验配置与用户确认——双闸门由 <see cref="GuardedPowerService"/> 负责。
/// </summary>
public sealed class Win32PowerService : IPowerService
{
    private readonly IPowerNativeApi _native;

    public Win32PowerService(IPowerNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public Task<PowerResult> ExecuteAsync(
        PowerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var (succeeded, nativeErrorCode) = request.Action switch
        {
            PowerAction.Shutdown => _native.Shutdown(request.ForceIfHung),
            PowerAction.Restart => _native.Restart(request.ForceIfHung),
            PowerAction.Sleep => _native.Sleep(),
            PowerAction.Hibernate => _native.Hibernate(),
            _ => (false, null)
        };

        if (request.Action is not (PowerAction.Shutdown or PowerAction.Restart
            or PowerAction.Sleep or PowerAction.Hibernate))
        {
            return Task.FromResult(new PowerResult
            {
                Outcome = PowerOutcome.Rejected,
                Message = "The requested action is not supported by the real power service."
            });
        }

        if (succeeded)
        {
            return Task.FromResult(new PowerResult
            {
                Outcome = PowerOutcome.Accepted,
                NativeErrorCode = nativeErrorCode,
                Message = "The real power action was executed successfully."
            });
        }

        return Task.FromResult(new PowerResult
        {
            Outcome = PowerOutcome.Failed,
            NativeErrorCode = nativeErrorCode,
            Message = "The real power action failed."
        });
    }
}
