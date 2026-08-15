namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 自启动操作结果。任何失败都以结果返回，不以未处理异常打断应用。
/// ResultCode 为稳定结果码；Message 为中文可显示消息；Status 为操作后的真实状态。
/// </summary>
public sealed record AutoStartOperationResult(
    bool Succeeded,
    string ResultCode,
    string Message,
    AutoStartStatus Status)
{
    public static AutoStartOperationResult Success(
        string resultCode,
        string message,
        AutoStartStatus status)
        => new(true, resultCode, message, status);

    public static AutoStartOperationResult Failure(
        string resultCode,
        string message,
        AutoStartStatus status)
        => new(false, resultCode, message, status);
}
