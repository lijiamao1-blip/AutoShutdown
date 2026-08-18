using System.Diagnostics;
using System.IO;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>
/// 打开目录 / 在资源管理器中定位文件（S-UI1 诊断中心）。UI 所有「打开日志目录」「定位导出包」
/// 动作统一经此服务，避免散落的进程启动点。仅 ShellExecute 打开系统资源管理器，绝不启动任何
/// 业务/电源进程。任何打开失败被吞掉并返回，不让 UI 崩溃（调用方已保证失败可呈现）。
/// </summary>
public interface IShellOpenService
{
    /// <summary>打开目录；目录不存在则先创建（日志目录可能尚未写入）。</summary>
    void OpenDirectory(string directoryPath);

    /// <summary>在资源管理器中定位并选中文件（父目录不存在则先创建）。</summary>
    void OpenFolderAndSelectFile(string filePath);
}

public sealed class ShellOpenService : IShellOpenService
{
    public void OpenDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打开资源管理器失败不向上冒泡；UI 不因此崩溃。
        }
    }

    public void OpenFolderAndSelectFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 同上：打开失败不影响 UI 主流程。
        }
    }
}
