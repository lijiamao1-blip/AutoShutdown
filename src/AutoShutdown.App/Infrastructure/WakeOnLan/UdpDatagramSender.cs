using System.Net;
using System.Net.Sockets;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.WakeOnLan;

namespace AutoShutdown.App.Infrastructure.WakeOnLan;

/// <summary>
/// UDP 数据报发送的托管实现（S21）。使用 <see cref="UdpClient"/> 向明确给定的
/// 局域网目标发送；不扫描、不自动发现、不访问公网。Socket/IO 异常转为结构化
/// <see cref="UdpDatagramSendResult"/>（绝不伪造成功）；取消直接传播。
/// </summary>
public sealed class UdpDatagramSender : IUdpDatagramSender
{
    public async Task<UdpDatagramSendResult> SendAsync(
        byte[] datagram,
        IPAddress destination,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            using var client = new UdpClient();
            await client
                .SendAsync(datagram.AsMemory(), new IPEndPoint(destination, port), cancellationToken)
                .ConfigureAwait(false);
            return new UdpDatagramSendResult { Status = UdpDatagramSendStatus.Success };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UdpDatagramSendResult
            {
                Status = UdpDatagramSendStatus.SendFailed,
                Error = exception.Message
            };
        }
    }
}
