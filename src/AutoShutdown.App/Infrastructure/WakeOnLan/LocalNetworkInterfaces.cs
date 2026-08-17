using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AutoShutdown.Core.WakeOnLan;

namespace AutoShutdown.App.Infrastructure.WakeOnLan;

/// <summary>
/// 本机当前活动 IPv4 接口的真实枚举（S21-D1）。仅用托管
/// <c>System.Net.NetworkInformation</c> 读取本机网络配置以计算定向广播地址——不扫描、
/// 不自动发现、不访问公网，无 P/Invoke。枚举失败时返回空清单（fail-closed：未知接口
/// → 仅有限广播 255.255.255.255 可用，任何自定义定向广播一律拒绝）。
/// </summary>
public sealed class LocalNetworkInterfaces : IWakeOnLanLocalNetworks
{
    public IReadOnlyList<LocalIpv4Interface> GetActiveIpv4Interfaces()
    {
        var interfaces = new List<LocalIpv4Interface>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var maskBytes = mask.GetAddressBytes();
                    if (maskBytes.All(byteValue => byteValue == 0))
                    {
                        continue;
                    }

                    interfaces.Add(new LocalIpv4Interface(unicast.Address, mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 枚举失败 → 无可验证的本机定向广播 → fail-closed（仅有限广播可用）。
            return Array.Empty<LocalIpv4Interface>();
        }

        return interfaces;
    }
}
