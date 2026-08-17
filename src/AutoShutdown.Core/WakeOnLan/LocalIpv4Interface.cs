using System.Net;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// 本机当前活动 IPv4 接口（地址 + 掩码）。用于计算本机网段对应的定向广播地址。
/// 纯数据，不含任何网络访问。
/// </summary>
public sealed record LocalIpv4Interface(IPAddress Address, IPAddress Mask);

/// <summary>
/// 本机当前活动 IPv4 接口枚举抽象（S21-D1）。真实实现（App 层）用托管
/// <c>System.Net.NetworkInformation</c> 枚举本机接口——仅读取本机网络配置，不扫描、
/// 不自动发现、不访问公网；自动化测试注入固定替身，绝不触碰真实网络栈。
/// 枚举失败时返回空清单（fail-closed：未知接口 → 仅有限广播可用）。
/// </summary>
public interface IWakeOnLanLocalNetworks
{
    IReadOnlyList<LocalIpv4Interface> GetActiveIpv4Interfaces();
}
