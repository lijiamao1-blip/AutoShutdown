using System.Security.Cryptography;
using AutoShutdown.Core.Remote;

namespace AutoShutdown.App.Infrastructure.Remote;

/// <summary>
/// Windows DPAPI 保护器（S23）：sharedSecret / 证书私钥等敏感材料经「当前用户」DPAPI 加密封装后
/// 才允许落盘，绝不明文写盘或日志。DPAPI 输出每次不同（无确定性）；解封失败抛异常，调用方 fail-closed
/// （绝不回退为默认值）。本实现不把任何输入/输出写入日志或异常消息。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly DataProtectionScope _scope;

    public DpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
    }

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, _scope);
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, _scope);
    }
}
