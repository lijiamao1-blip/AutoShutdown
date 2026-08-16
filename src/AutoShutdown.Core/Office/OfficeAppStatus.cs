namespace AutoShutdown.Core.Office;

/// <summary>
/// 单个 Office 应用自动保存的结局（S17）。
/// Success：已保存所有有路径文档且不存在无路径新文档。
/// NotDetected：应用未安装或 COM 不可用（可诊断失败）。
/// TimedOut：该应用的保存超过时限。
/// PartialFailure：存在无路径新文档或部分保存失败（明确告知，不静默忽略）。
/// </summary>
public enum OfficeAppStatus
{
    Success = 0,
    NotDetected = 1,
    TimedOut = 2,
    PartialFailure = 3
}
