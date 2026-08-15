namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 开机自启动服务。仅操作当前用户 Run 注册项的固定名称；
/// GetStatus 为只读检查，默认不开启，任何调用不得产生写操作。
/// </summary>
public interface IAutoStartService
{
    /// <summary>只读检查当前状态，绝不产生写操作。</summary>
    AutoStartStatus GetStatus();

    /// <summary>显式启用（幂等）。</summary>
    AutoStartOperationResult Enable();

    /// <summary>显式关闭（幂等）。</summary>
    AutoStartOperationResult Disable();

    /// <summary>将本软件已有但错误的项修正为当前路径。</summary>
    AutoStartOperationResult Repair();
}
