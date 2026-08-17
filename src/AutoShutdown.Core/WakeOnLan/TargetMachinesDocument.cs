namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// target-machines.json 文档 schema（S21）。独立于 config.json / tasks.json。
/// 仅含 SchemaVersion 与目标机器清单；损坏数据不得静默回退为可能触发发送的默认清单。
/// </summary>
public sealed record TargetMachinesDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>目标机器清单（可为空集合，但不得为 null）。</summary>
    public IReadOnlyList<WakeOnLanTarget> Machines { get; init; } = [];
}
