using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Office;

/// <summary>
/// 只读 Office 能力探测（S17）。仅包装 IOfficeAutomation 的可用性探测，
/// 不启动进程、不保存文档、不关闭应用。
/// </summary>
public sealed class OfficeDetector
{
    private readonly IOfficeAutomation _automation;

    public OfficeDetector(IOfficeAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
    }

    public IReadOnlyList<OfficeApplicationKind> DetectAvailable()
        => _automation.DetectAvailableApplications();
}
