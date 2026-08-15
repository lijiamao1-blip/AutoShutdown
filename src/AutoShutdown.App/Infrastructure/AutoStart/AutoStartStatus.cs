namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 开机自启动状态。所有值均为只读判定结果，不携带任何写操作。
/// </summary>
public enum AutoStartStatus
{
    /// <summary>注册项不存在或值为空：未启用。</summary>
    Disabled = 0,

    /// <summary>注册项值与当前可执行文件路径一致：已启用。</summary>
    Enabled = 1,

    /// <summary>注册项存在但指向其他路径：路径异常，等待用户主动修复。</summary>
    PathMismatch = 2,

    /// <summary>注册项存在但值无法解析为合法路径：值异常。</summary>
    InvalidValue = 3,

    /// <summary>无法读取注册表：不可用。</summary>
    Unavailable = 4
}
