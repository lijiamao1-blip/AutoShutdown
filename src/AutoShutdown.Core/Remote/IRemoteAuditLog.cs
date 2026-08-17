namespace AutoShutdown.Core.Remote;

/// <summary>
/// 远程层高危审计抽象（S23 CP5）。实现负责落盘（App 层文件审计日志）；测试用内存替身。
/// 条目契约：绝不包含 PIN、sharedSecret、HMAC 或证书私钥。
/// </summary>
public interface IRemoteAuditLog
{
    void Write(RemoteAuditEntry entry);
}
