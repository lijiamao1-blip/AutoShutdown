namespace AutoShutdown.Core.Office;

/// <summary>
/// 单个 Office 应用的保存结果（S17）。只含计数与脱敏状态，
/// 绝不包含文档正文、文件名隐私或完整路径。
/// </summary>
public sealed record OfficeApplicationSaveResult
{
    public OfficeApplicationKind Application { get; init; }

    public OfficeAppStatus Status { get; init; } = OfficeAppStatus.Success;

    public int SavedCount { get; init; }

    public int NoPathCount { get; init; }

    public int FailedCount { get; init; }
}
