using Microsoft.Win32;

namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 基于 HKCU 的真实注册表存储。唯一允许的外部状态是
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的固定项
/// AutoShutdown.Desktop；不使用 HKLM、不要求管理员权限。
/// </summary>
public sealed class RegistryRunKeyStore : IRegistryRunKeyStore
{
    /// <summary>固定且唯一的注册项名称。</summary>
    public const string ValueName = "AutoShutdown.Desktop";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void SetValue(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void DeleteValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
