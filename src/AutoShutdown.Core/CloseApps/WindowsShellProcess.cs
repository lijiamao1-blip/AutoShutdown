namespace AutoShutdown.Core.CloseApps;

/// <summary>Explorer 承载 Windows 桌面，应由系统关机流程处理，不能作为普通应用关闭。</summary>
public static class WindowsShellProcess
{
    public static bool IsExplorer(string? processName, string? executablePath = null)
    {
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 名称读取失败时，仍可用已读取的完整路径保护桌面进程。
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return false;
        }

        var path = ExecutablePathKey.Normalize(executablePath);
        return path is not null
            && string.Equals(Path.GetFileName(path), "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }
}
