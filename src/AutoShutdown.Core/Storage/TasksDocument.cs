using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Storage;

/// <summary>
/// tasks.json 文档 schema（独立于 config.json）。包含版本字段与任务清单。
/// V2 新增文件；V1 无 tasks.json，故不存在 V1→V2 迁移链。
/// </summary>
public sealed record TasksDocument
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>tasks.json 的 schema 版本，必须为 <see cref="CurrentSchemaVersion"/>。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>任务清单（可为空集合，但不得为 null）。</summary>
    public IReadOnlyList<TaskDefinition> Tasks { get; init; } = [];
}
