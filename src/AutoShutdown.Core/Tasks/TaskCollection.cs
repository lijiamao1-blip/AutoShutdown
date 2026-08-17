namespace AutoShutdown.Core.Tasks;

/// <summary>
/// 任务定义的领域模型集合（内存，无持久化）。提供多任务增删改查与分别启停。
/// 结构校验（Id、Action、Priority、WarningSeconds、Kind/时长/目标时间）统一委托
/// <see cref="TaskDefinitionValidator"/>，与 TaskService.Create 共用同一套规则。
/// 本类不引入任何 tasks.json 读写（归 S13-T02B）。
/// </summary>
public sealed class TaskCollection
{
    private readonly Dictionary<Guid, TaskDefinition> _items = new();

    /// <summary>
    /// 集合变更事件（S22 CP3）：仅在实际提交成功后于变更线程同步触发，供 outbound 同步协调器
    /// 消费（创建/更新/删除/分别启停）。本集合不产生任何「外部状态」事件，杜绝 inbound 回写通道。
    /// </summary>
    public event EventHandler<TaskCollectionChangedEventArgs>? Changed;

    /// <summary>集合中任务数量。</summary>
    public int Count => _items.Count;

    /// <summary>当前全部任务定义的快照（无内部状态泄漏）。</summary>
    public IReadOnlyCollection<TaskDefinition> Items => _items.Values.ToArray();

    /// <summary>新增任务定义（Id 已存在则拒绝）。</summary>
    public TaskCollectionResult Add(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var validation = Validate(definition);
        if (!validation.Succeeded)
        {
            return validation;
        }

        if (_items.ContainsKey(definition.Id))
        {
            return Failure(
                TaskCollectionStatus.DuplicateId,
                $"A task with id {definition.Id} already exists.");
        }

        _items.Add(definition.Id, definition);
        RaiseChanged(TaskCollectionChangeKind.Added, definition.Id, definition);
        return Success(definition, "The task definition was added.");
    }

    /// <summary>更新已存在的任务定义（Id 必须存在且不变）。</summary>
    public TaskCollectionResult Update(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var validation = Validate(definition);
        if (!validation.Succeeded)
        {
            return validation;
        }

        if (!_items.ContainsKey(definition.Id))
        {
            return Failure(
                TaskCollectionStatus.NotFound,
                $"A task with id {definition.Id} does not exist.");
        }

        _items[definition.Id] = definition;
        RaiseChanged(TaskCollectionChangeKind.Updated, definition.Id, definition);
        return Success(definition, "The task definition was updated.");
    }

    /// <summary>移除任务定义（Id 不存在则拒绝）。</summary>
    public TaskCollectionResult Remove(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            return Failure(
                TaskCollectionStatus.InvalidDefinition,
                "taskId must not be an empty GUID.");
        }

        if (!_items.Remove(taskId))
        {
            return Failure(
                TaskCollectionStatus.NotFound,
                $"A task with id {taskId} does not exist.");
        }

        RaiseChanged(TaskCollectionChangeKind.Removed, taskId, null);
        return Success(null, "The task definition was removed.");
    }

    /// <summary>分别启用/禁用任务（Id 不存在则拒绝）。</summary>
    public TaskCollectionResult SetEnabled(Guid taskId, bool isEnabled)
    {
        if (taskId == Guid.Empty)
        {
            return Failure(
                TaskCollectionStatus.InvalidDefinition,
                "taskId must not be an empty GUID.");
        }

        if (!_items.TryGetValue(taskId, out var existing))
        {
            return Failure(
                TaskCollectionStatus.NotFound,
                $"A task with id {taskId} does not exist.");
        }

        var updated = existing with { IsEnabled = isEnabled };
        _items[taskId] = updated;
        RaiseChanged(
            isEnabled ? TaskCollectionChangeKind.Enabled : TaskCollectionChangeKind.Disabled,
            taskId,
            updated);
        return Success(updated, isEnabled ? "The task was enabled." : "The task was disabled.");
    }

    /// <summary>按 Id 获取任务定义；不存在返回 null。</summary>
    public TaskDefinition? Get(Guid taskId)
        => _items.TryGetValue(taskId, out var definition) ? definition : null;

    /// <summary>按 Id 尝试获取任务定义。</summary>
    public bool TryGet(Guid taskId, out TaskDefinition? definition)
        => _items.TryGetValue(taskId, out definition);

    /// <summary>是否存在指定 Id 的任务。</summary>
    public bool Contains(Guid taskId) => _items.ContainsKey(taskId);

    private static TaskCollectionResult Validate(TaskDefinition definition)
    {
        var error = TaskDefinitionValidator.GetStructuralError(definition);
        if (error is not null)
        {
            return Failure(TaskCollectionStatus.InvalidDefinition, error);
        }

        return Success(definition, "The task definition is valid.");
    }

    private void RaiseChanged(
        TaskCollectionChangeKind kind,
        Guid taskId,
        TaskDefinition? definition)
    {
        Changed?.Invoke(this, new TaskCollectionChangedEventArgs
        {
            Kind = kind,
            TaskId = taskId,
            Definition = definition
        });
    }

    private static TaskCollectionResult Success(TaskDefinition? definition, string message) => new()
    {
        Status = TaskCollectionStatus.Success,
        Definition = definition,
        Message = message
    };

    private static TaskCollectionResult Failure(TaskCollectionStatus status, string message) => new()
    {
        Status = status,
        Message = message
    };
}
