using AutoShutdown.Core.Office;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// Office COM 自动化边界（S17）。动作/服务只依赖此抽象，不直接触碰 COM；
/// 自动化测试用替身实现，绝不启动或操纵真实 Word/Excel/PowerPoint。
/// 真实实现必须在明确 STA 边界内执行、逐对象释放 COM、不得遗留 Office 进程。
/// </summary>
public interface IOfficeAutomation
{
    /// <summary>只读探测哪些 Office 应用可被 COM 自动化。不得启动进程。</summary>
    IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications();

    /// <summary>
    /// 保存指定应用内「已有路径」的打开文档；无路径新文档按失败计数（不猜路径、
    /// 不弹另存为）。所有 COM 引用在返回前释放；单文档/单应用异常不得使进程失控。
    /// 实现需在 STA 边界内执行并协作响应取消。
    /// </summary>
    OfficeApplicationSaveResult SaveOpenDocuments(
        OfficeApplicationKind application,
        CancellationToken cancellationToken);
}
