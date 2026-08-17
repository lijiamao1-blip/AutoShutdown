namespace AutoShutdown.Core.Remote;

/// <summary>
/// S23 远程命令白名单。与 S19 本地命令白名单（<c>LocalCommandWhitelist</c>）完全独立、
/// 绝不混用。默认仅授权只读方法 queryStatus/listTasks；触发命令 triggerShutdown/
/// cancelShutdown 需本地显式启用。远程请求无任何写入本白名单的接口；本白名单只由本地
/// 配置（remote-settings.json）读取。
/// </summary>
public sealed record RemoteCommandWhiteList
{
    /// <summary>queryStatus（只读状态查询）。默认允许。</summary>
    public bool QueryStatus { get; init; } = true;

    /// <summary>listTasks（只读任务清单）。默认允许。</summary>
    public bool ListTasks { get; init; } = true;

    /// <summary>triggerShutdown（受控触发）。默认拒绝，需本地显式启用。</summary>
    public bool TriggerShutdown { get; init; }

    /// <summary>cancelShutdown（取消待决倒计时/关机）。默认拒绝，需本地显式启用。</summary>
    public bool CancelShutdown { get; init; }
}

/// <summary>远程命令白名单结构校验（空 = 合法）。</summary>
public static class RemoteCommandWhiteListValidator
{
    public static IReadOnlyList<string> Validate(RemoteCommandWhiteList? whitelist)
    {
        if (whitelist is null)
        {
            return ["Remote.WhiteList must not be null."];
        }

        return [];
    }
}
