using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Storage;

/// <summary>
/// tasks.json 文档 schema（独立于 config.json）。包含版本字段与任务清单。
/// V2 新增文件；V1 无 tasks.json，故不存在 V1→V2 迁移链。
/// </summary>
public sealed record TasksDocument
{
    /// <summary>
    /// tasks.json schema 版本。V2（S14）新增 Weekdays/NextWorkday/NthWorkdayOfMonth/OneTime
    /// 及 HolidayDates 字段；均为可空增量，V1 文档可无损迁移（身份迁移：仅版本号提升）。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>tasks.json 的 schema 版本，必须为 <see cref="CurrentSchemaVersion"/>。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>任务清单（可为空集合，但不得为 null）。</summary>
    public IReadOnlyList<TaskDefinition> Tasks { get; init; } = [];
}
