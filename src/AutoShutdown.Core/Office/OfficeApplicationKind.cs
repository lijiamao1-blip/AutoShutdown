namespace AutoShutdown.Core.Office;

/// <summary>
/// 可被自动保存的 Office 桌面应用（S17）。仅识别 Word/Excel/PowerPoint，
/// 不扩展其他 Office 组件；未知项归入可诊断失败而非静默忽略。
/// </summary>
public enum OfficeApplicationKind
{
    Word = 0,
    Excel = 1,
    PowerPoint = 2
}
