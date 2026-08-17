using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>单次同步中单个任务的最终结果。</summary>
public enum TaskSyncEntryStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Unchanged = 4,
    Failed = 5,

    /// <summary>观察到的外部任务名字无法解析为自家稳定 id（他应用任务）：跳过，绝不删除。</summary>
    SkippedForeign = 6
}

public sealed record TaskSyncEntry
{
    /// <summary>对应本地任务 id；SkippedForeign 时为 Guid.Empty。</summary>
    public Guid TaskId { get; init; }

    public string ExternalTaskName { get; init; } = string.Empty;

    public TaskSyncEntryStatus Status { get; init; } = TaskSyncEntryStatus.Unknown;

    public string? Message { get; init; }
}

/// <summary>一次 outbound 同步的完整报告（逐任务部分失败可见）。</summary>
public sealed record TaskSyncReport
{
    /// <summary>整体成功：查询成功且无任何任务失败。SkippedForeign 不算失败。</summary>
    public bool Succeeded { get; init; }

    public ExternalTaskQueryStatus QueryStatus { get; init; } = ExternalTaskQueryStatus.Unknown;

    /// <summary>查询失败时的原因（UI 修复建议依据）。</summary>
    public string? QueryFailureMessage { get; init; }

    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }
    public int SkippedForeign { get; init; }

    public IReadOnlyList<TaskSyncEntry> Entries { get; init; } = [];
}

/// <summary>
/// 幂等 outbound 同步引擎（S22 CP2）：以本地任务集合为唯一事实源，单向收敛到外部任务计划程序。
/// 读本地 → 映射期望 → 查询外部 → 创建缺失 / 更新失配 / 删除陈旧（仅自家任务）→ 逐任务报告。
/// 绝不写本地、绝不 inbound、绝不修改传入的任务快照；外部改动只被观察，永不反向覆盖本地。
/// 对同一输入重复同步是幂等的：第二次除 Unchanged 外无任何变更。
/// </summary>
public sealed class TaskSyncService
{
    private readonly TaskSchedulerMapper _mapper;
    private readonly ITaskSchedulerAdapter _adapter;

    public TaskSyncService(TaskSchedulerMapper mapper, ITaskSchedulerAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(adapter);
        _mapper = mapper;
        _adapter = adapter;
    }

    public async Task<TaskSyncReport> SyncAsync(
        IReadOnlyCollection<TaskDefinition> localTasks,
        string appExePath,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localTasks);
        ArgumentNullException.ThrowIfNull(appExePath);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (appExePath.Length == 0)
        {
            throw new ArgumentException("appExePath must not be empty.", nameof(appExePath));
        }

        // 期望集合：本地事实源只读快照 → 期望外部任务。映射为 null（无未来触发/结构性无效）
        // 视为「当前不需要外部触发」，其既有外部任务应在收敛时被清理。
        var desiredById = new Dictionary<Guid, ExternalTaskSpec>();
        foreach (var localTask in localTasks)
        {
            if (localTask is null)
            {
                continue;
            }

            var spec = _mapper.Map(localTask, appExePath, now, timeZone);
            if (spec is not null)
            {
                desiredById[localTask.Id] = spec;
            }
        }

        var query = await _adapter.QueryOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (query.Status != ExternalTaskQueryStatus.Success)
        {
            return new TaskSyncReport
            {
                Succeeded = false,
                QueryStatus = query.Status,
                QueryFailureMessage = query.Message ?? "The external task query failed."
            };
        }

        var observed = (query.Tasks ?? [])
            .Where(task => task is not null && !string.IsNullOrEmpty(task.Name))
            .OrderBy(task => task.Name, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<TaskSyncEntry>();
        var matchedIds = new HashSet<Guid>();

        // 第一遍：既有外部任务 → 等价(Unchanged)/更新(Update)/陈旧清理(Delete)。
        // 只触碰名字可解析为自家稳定 id 的任务；他应用任务一律跳过，绝不删除。
        foreach (var observedTask in observed)
        {
            if (!TaskSyncNaming.TryParseOwnedTaskName(observedTask.Name, out var taskId))
            {
                entries.Add(SkippedForeign(observedTask.Name));
                continue;
            }

            if (matchedIds.Add(taskId) && desiredById.TryGetValue(taskId, out var desired))
            {
                if (TaskSyncEquivalence.IsEquivalent(desired, observedTask))
                {
                    entries.Add(Unchanged(taskId, observedTask.Name));
                }
                else
                {
                    entries.Add(await MutateAsync(
                        taskId,
                        observedTask.Name,
                        () => _adapter.UpdateAsync(desired, cancellationToken),
                        TaskSyncEntryStatus.Updated).ConfigureAwait(false));
                }

                continue;
            }

            // 本地任务已不存在 / 不再需要外部触发 → 删除（名字已验证为自家）。
            entries.Add(await MutateAsync(
                taskId,
                observedTask.Name,
                () => _adapter.DeleteAsync(observedTask.Name, cancellationToken),
                TaskSyncEntryStatus.Deleted).ConfigureAwait(false));
        }

        // 第二遍：本地期望但外部缺失 → 创建（确定性顺序便于测试与审计）。
        foreach (var pair in desiredById.OrderBy(pair => pair.Value.Name, StringComparer.Ordinal))
        {
            if (matchedIds.Contains(pair.Key))
            {
                continue;
            }

            entries.Add(await MutateAsync(
                pair.Key,
                pair.Value.Name,
                () => _adapter.CreateAsync(pair.Value, cancellationToken),
                TaskSyncEntryStatus.Created).ConfigureAwait(false));
        }

        return BuildReport(entries, ExternalTaskQueryStatus.Success, null);
    }

    private static async Task<TaskSyncEntry> MutateAsync(
        Guid taskId,
        string externalName,
        Func<Task<ExternalTaskMutationResult>> mutation,
        TaskSyncEntryStatus successStatus)
    {
        ExternalTaskMutationResult result;
        try
        {
            result = await mutation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new TaskSyncEntry
            {
                TaskId = taskId,
                ExternalTaskName = externalName,
                Status = TaskSyncEntryStatus.Failed,
                Message = "Unexpected adapter failure: " + exception.Message
            };
        }

        if (result.Succeeded)
        {
            return new TaskSyncEntry
            {
                TaskId = taskId,
                ExternalTaskName = externalName,
                Status = successStatus,
                Message = result.Message
            };
        }

        return new TaskSyncEntry
        {
            TaskId = taskId,
            ExternalTaskName = externalName,
            Status = TaskSyncEntryStatus.Failed,
            Message = result.Message ?? result.Status.ToString()
        };
    }

    private static TaskSyncEntry SkippedForeign(string externalName) => new()
    {
        TaskId = Guid.Empty,
        ExternalTaskName = externalName,
        Status = TaskSyncEntryStatus.SkippedForeign
    };

    private static TaskSyncEntry Unchanged(Guid taskId, string externalName) => new()
    {
        TaskId = taskId,
        ExternalTaskName = externalName,
        Status = TaskSyncEntryStatus.Unchanged
    };

    private static TaskSyncReport BuildReport(
        IReadOnlyList<TaskSyncEntry> entries,
        ExternalTaskQueryStatus queryStatus,
        string? queryFailureMessage)
    {
        var counts = new TaskSyncReport
        {
            QueryStatus = queryStatus,
            QueryFailureMessage = queryFailureMessage,
            Created = entries.Count(entry => entry.Status == TaskSyncEntryStatus.Created),
            Updated = entries.Count(entry => entry.Status == TaskSyncEntryStatus.Updated),
            Deleted = entries.Count(entry => entry.Status == TaskSyncEntryStatus.Deleted),
            Unchanged = entries.Count(entry => entry.Status == TaskSyncEntryStatus.Unchanged),
            Failed = entries.Count(entry => entry.Status == TaskSyncEntryStatus.Failed),
            SkippedForeign = entries.Count(entry => entry.Status == TaskSyncEntryStatus.SkippedForeign),
            Entries = entries
        };

        return counts with
        {
            Succeeded = counts.Failed == 0
        };
    }
}
