namespace AutoShutdown.Core.Office;

/// <summary>
/// 一次 Office 自动保存的总体报告（S17）。成功当且仅当所有已安装应用均 Success；
/// 未安装任何 Office 视为无目标成功（无事可做）。摘要只含应用名与计数，不含敏感数据。
/// </summary>
public sealed record OfficeSaveReport
{
    public IReadOnlyList<OfficeApplicationSaveResult> Applications { get; init; } = [];

    public bool Succeeded => Applications.All(application => application.Status == OfficeAppStatus.Success);

    /// <summary>脱敏人类可读摘要（仅应用名 + 计数）。不含文件名/路径/内容。</summary>
    public string Summary { get; init; } = string.Empty;
}
