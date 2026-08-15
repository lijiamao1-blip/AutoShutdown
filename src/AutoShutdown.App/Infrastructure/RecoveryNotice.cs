namespace AutoShutdown.App.Infrastructure;

/// <summary>
/// 启动崩溃恢复通知：被标记为 interrupted 的任务 id 列表（供 T08 UI 呈现恢复横幅）。
/// </summary>
public sealed record RecoveryNotice(IReadOnlyList<Guid> InterruptedTaskIds);

/// <summary>
/// 供 UI 读取的恢复通知单例。ApplicationLifetimeCoordinator 在启动链写一次，
/// MainWindowViewModel 在 InitializeAsync 读取一次；只读通知，不持久化。
/// </summary>
public sealed class RecoveryNoticeService
{
    public RecoveryNotice? Notice { get; set; }
}
