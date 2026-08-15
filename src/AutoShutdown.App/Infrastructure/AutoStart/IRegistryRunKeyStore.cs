namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 当前用户 Run 键固定注册项的存储抽象。生产实现访问 HKCU 注册表；
/// 测试注入内存替身，禁止自动化测试触碰真实注册表。
/// 实现只允许操作固定项名，不得枚举或清理其他项。
/// </summary>
public interface IRegistryRunKeyStore
{
    /// <summary>读取固定项值；不存在或为空返回 null。</summary>
    string? GetValue();

    /// <summary>写入固定项值。</summary>
    void SetValue(string value);

    /// <summary>删除固定项值；值不存在时视为成功（幂等）。</summary>
    void DeleteValue();
}
