using AutoShutdown.Core.Office;

namespace AutoShutdown.OfficeSaveHelper.Office;

/// <summary>
/// Office 原生 COM 互操作网关（S17 独立验收 D2，位于辅助进程内）。所有 COM 原生调用
/// 收敛于此网关；只允许「附加」Running Object Table 中已运行的实例，绝不创建/启动 Office。
/// 供 <see cref="OfficeComSaveWorker"/> 编排使用；自动化测试注入替身，不触碰真实 Office。
/// </summary>
public interface IOfficeComGateway
{
    /// <summary>
    /// 仅通过 ROT 附加已运行的 Office 实例；无活动对象时返回 null，绝不创建替代实例。
    /// 进程探测与附加之间的退出竞态在此安全失败（返回 null，不抛异常、不创建）。
    /// </summary>
    IOfficeComApplication? TryAttach(OfficeApplicationKind application);
}

/// <summary>已附加的 Office 应用会话。仅提供文档枚举与释放；无 Quit、无创建入口。</summary>
public interface IOfficeComApplication : IDisposable
{
    /// <summary>枚举该应用的打开文档。返回的每个文档包装须由调用方释放。</summary>
    IReadOnlyList<IOfficeComDocument> GetOpenDocuments();
}

/// <summary>单个 Office 文档包装。仅暴露是否有路径与保存；无 Quit、无另存为入口。</summary>
public interface IOfficeComDocument : IDisposable
{
    bool HasPath { get; }

    void Save();
}
