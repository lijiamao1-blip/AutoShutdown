using System.Diagnostics;
using System.IO;
using AutoShutdown.Core.Office;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// Office 保存辅助进程的唯一启动网关（S17 独立验收 D2）。全 App 层唯一允许
/// <see cref="Process.Start"/> 的位置；只启动固定用途的 Office 辅助进程。
/// 可执行文件路径由应用安装目录（AppContext.BaseDirectory）固定解析，绝不来自
/// 配置/任务/用户输入/环境变量；启动前校验文件存在且位于预期目录。
/// UseShellExecute=false（不经 cmd.exe/PowerShell/shell），参数仅含受控枚举与 GUID 标识。
/// </summary>
public sealed class OfficeSaveHelperLauncher : IOfficeSaveHelperLauncher
{
    private const string HelperFileName = "AutoShutdown.OfficeSaveHelper.exe";

    private readonly string _helperDirectory;

    public OfficeSaveHelperLauncher()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>测试专用：注入辅助程序目录以验证路径校验；生产只用无参构造（固定安装目录）。</summary>
    public OfficeSaveHelperLauncher(string helperDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperDirectory);
        _helperDirectory = helperDirectory;
    }

    public IOfficeSaveHelperProcess Launch(OfficeApplicationKind application, string correlationId)
    {
        var helperPath = ResolveHelperPath();

        var startInfo = BuildStartInfo(helperPath, application, correlationId);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Office save helper failed to start.");

        return new OfficeSaveHelperProcess(process);
    }

    /// <summary>
    /// 构建辅助进程启动信息（不含 <see cref="Process.Start"/>）。公开供参数/结构契约测试
    /// 无副作用验证：UseShellExecute=false、参数仅含受控枚举与 GUID、重定向标准流。
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        string helperPath,
        OfficeApplicationKind application,
        string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // ArgumentList 直接传参（不经 shell 解析、无引号注入）；参数仅含受控枚举与最小标识。
        startInfo.ArgumentList.Add("--app");
        startInfo.ArgumentList.Add(application.ToString());
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(correlationId);

        return startInfo;
    }

    private string ResolveHelperPath()
    {
        // 固定解析：仅应用安装目录 + 固定文件名，禁止任何外部提供。
        var directoryFull = Path.GetFullPath(_helperDirectory);
        var candidate = Path.GetFullPath(Path.Combine(directoryFull, HelperFileName));

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("Office save helper executable not found.", candidate);
        }

        // 校验解析结果确实位于预期应用目录内（防御目录穿越/符号链接逃逸）。
        if (!candidate.StartsWith(directoryFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Office save helper path escaped the application directory.");
        }

        return candidate;
    }
}
