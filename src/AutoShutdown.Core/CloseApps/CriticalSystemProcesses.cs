namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 绝不允许作为关闭目标的 Windows 关键进程名（单一事实源，S-CRIT1）。
///
/// 此前选择端（<c>ProcessSelectionGuard</c>，App 层）与执行端（<see cref="CloseAppsService"/>，
/// Core 层）各自维护一份手工清单，两份已经漂移：选择端有 <c>conhost</c> / <c>lsaiso</c>，
/// 执行端没有。后果是「界面拦得住、执行端拦不住」——手改 config.json 或旧版本遗留下来的
/// 目标可以绕过只存在于选择端的保护。清单合并到此处后，两端引用同一份事实源，不会再漂移。
///
/// 名称按进程名（不含扩展名）比较，大小写不敏感。只增不减地保守维护：
/// 多拦一个进程最多是「某个程序需要用户自己关」，少拦一个可能让系统组件被强杀。
/// </summary>
public static class CriticalSystemProcesses
{
    /// <summary>关键系统进程名（不含扩展名，大小写不敏感）。</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 内核/会话基础设施：关闭等同于强制重启或蓝屏。
            "system",
            "idle",
            "registry",
            "memory compression",
            "csrss",
            "smss",
            "wininit",
            "winlogon",
            "services",
            "lsass",
            "lsaiso",

            // 图形/音频/字体宿主：关闭会直接破坏桌面会话。
            "dwm",
            "fontdrvhost",
            "audiodg",

            // 服务宿主与控制台宿主。
            "svchost",
            "conhost",

            // 桌面外壳的常驻宿主与输入相关组件。README 明确建议不要把 TextInputHost
            // 加入关闭目标；其余同类宿主一并保护，避免用户把它们当成普通应用选进来。
            "textinputhost",
            "sihost",
            "taskhostw",
            "ctfmon"
        };

    /// <summary>进程名是否属于关键系统进程。名称为空/空白一律返回 false（由调用方另行 fail-closed）。</summary>
    public static bool IsCritical(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && Names.Contains(processName);
}
