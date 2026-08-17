namespace AutoShutdown.Core.Remote;

/// <summary>
/// secret/PIN/私钥的透明保护抽象。S23 契约：sharedSecret、私钥等敏感材料不得明文落盘，
/// 必须经本接口加密封装后再持久化（Windows 实现为 DPAPI；测试用确定性替身）。
/// 实现不得把原文写入日志或异常消息。
/// </summary>
public interface ISecretProtector
{
    /// <summary>把敏感字节封装为可安全落盘的字节串。输入/输出均不进入日志。</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>还原被封装的字节串。失败抛异常（调用方 fail-closed），不得返回默认值。</summary>
    byte[] Unprotect(byte[] protectedBytes);
}
