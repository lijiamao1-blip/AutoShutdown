using System.Net;
using AutoShutdown.Core.WakeOnLan;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 可测试的 UDP 数据报发送抽象（S21）。只发送给定的字节与目标地址/端口；
/// 不扫描、不自动发现、不访问公网。Socket 错误须以 <see cref="UdpDatagramSendResult"/>
/// 结构化失败返回，绝不伪造成功。取消（CancellationToken）直接传播（OperationCanceledException）。
/// </summary>
public interface IUdpDatagramSender
{
    Task<UdpDatagramSendResult> SendAsync(
        byte[] datagram,
        IPAddress destination,
        int port,
        CancellationToken cancellationToken);
}
