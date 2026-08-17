namespace AutoShutdown.App.Infrastructure.Remote;

/// <summary>
/// 远程控制服务器的本地控制面（S23 CP5）：设置页分区只依赖本接口启动/停止服务器，
/// 不依赖具体 TCP 实现，便于隔离测试。远程命令永远只经 <c>RemoteRequestHandler</c> 走
/// 本地调度引擎唯一路径，本接口绝无第二个电源出口。
/// </summary>
public interface IRemoteServerControl
{
    /// <summary>当前是否正在监听。</summary>
    bool IsRunning { get; }

    /// <summary>按当前 remote-settings.json 启动监听（未启用/配置损坏/强制 TLS 无证书一律不监听）。</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止监听并关闭所有在途连接。</summary>
    Task StopAsync();
}

/// <summary>远程活动提示分类（托盘气泡分级：连接为低危、触发/取消为高危）。</summary>
public enum RemoteServerNotificationKind
{
    Unknown = 0,

    /// <summary>低危：一个远程请求被处理（连接活动）。</summary>
    Connection = 1,

    /// <summary>高危：远程 triggerShutdown 被接受并派发到本地调度引擎。</summary>
    TriggerShutdown = 2,

    /// <summary>高危：远程 cancelShutdown 被接受并派发到本地调度引擎。</summary>
    CancelShutdown = 3
}

/// <summary>一次远程活动的本地通知负载（仅来源信息，绝不含 PIN/secret/HMAC/私钥）。</summary>
public sealed record RemoteServerNotification
{
    public RemoteServerNotificationKind Kind { get; init; } = RemoteServerNotificationKind.Unknown;

    /// <summary>发起连接的对端 IP（用于本地展示；来源不可信）。</summary>
    public string SourceIp { get; init; } = string.Empty;

    /// <summary>活动发生时刻（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; init; }
}
