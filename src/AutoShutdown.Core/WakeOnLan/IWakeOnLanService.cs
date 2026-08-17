namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// Wake-on-LAN 发送服务（S21）。只向用户显式配置的局域网目标发送 Magic Packet；
/// 不扫描、不自动发现、不访问公网。目标缺失/非法或 Socket 失败一律结构化失败。
/// </summary>
public interface IWakeOnLanService
{
    Task<WolSendResult> SendAsync(Guid targetMachineId, CancellationToken cancellationToken);
}
