namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 空闲输入源抽象：提供「距上次用户输入的空闲时长」这一事实，隔离 Win32 调用。
/// 返回 null 表示无法检测（API 失败、权限不足或未初始化），调用方必须默认不触发。
/// </summary>
public interface IIdleInputSource
{
    /// <summary>当前空闲时长；null = 检测失败。</summary>
    TimeSpan? GetIdleDuration();
}
