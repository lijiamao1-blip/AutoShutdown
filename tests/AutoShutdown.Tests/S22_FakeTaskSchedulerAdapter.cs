using AutoShutdown.Core.Scheduling.TaskSchedulerSync;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 测试共享 Fake 适配器：内存外部任务；记录全部外部变更（只 outbound）；
/// 可注入查询失败、创建/更新/删除失败与创建异常，用于验证幂等同步、部分失败与只删本应用。
/// </summary>
internal sealed class FakeTaskSchedulerAdapter : ITaskSchedulerAdapter
{
    private readonly Dictionary<string, ExternalTaskState> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalTaskMutationStatus> _createFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalTaskMutationStatus> _updateFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalTaskMutationStatus> _deleteFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _createThrows = new(StringComparer.Ordinal);

    public ExternalTaskQueryStatus QueryStatus { get; set; } = ExternalTaskQueryStatus.Success;
    public string? QueryMessage { get; set; }

    private readonly List<(string Op, string Name, ExternalTaskSpec? Spec)> _operations = new();
    public IReadOnlyList<(string Op, string Name, ExternalTaskSpec? Spec)> Operations => _operations;

    public void Seed(ExternalTaskState task) => _tasks[task.Name] = task;

    public void FailCreate(string name, ExternalTaskMutationStatus status) => _createFailures[name] = status;
    public void FailUpdate(string name, ExternalTaskMutationStatus status) => _updateFailures[name] = status;
    public void FailDelete(string name, ExternalTaskMutationStatus status) => _deleteFailures[name] = status;
    public void ThrowOnCreate(string name) => _createThrows.Add(name);

    public Task<ExternalTaskQueryResult> QueryOwnedAsync(CancellationToken cancellationToken)
    {
        if (QueryStatus != ExternalTaskQueryStatus.Success)
        {
            return Task.FromResult(new ExternalTaskQueryResult
            {
                Status = QueryStatus,
                Message = QueryMessage
            });
        }

        return Task.FromResult(new ExternalTaskQueryResult
        {
            Status = ExternalTaskQueryStatus.Success,
            Tasks = _tasks.Values.ToArray()
        });
    }

    public Task<ExternalTaskMutationResult> CreateAsync(ExternalTaskSpec spec, CancellationToken cancellationToken)
    {
        Record("Create", spec.Name, spec);
        if (_createThrows.Contains(spec.Name))
        {
            throw new InvalidOperationException("injected create failure.");
        }

        if (_createFailures.TryGetValue(spec.Name, out var status))
        {
            return Task.FromResult(Fail(status));
        }

        _tasks[spec.Name] = ToState(spec);
        return Task.FromResult(new ExternalTaskMutationResult { Status = ExternalTaskMutationStatus.Success });
    }

    public Task<ExternalTaskMutationResult> UpdateAsync(ExternalTaskSpec spec, CancellationToken cancellationToken)
    {
        Record("Update", spec.Name, spec);
        if (_updateFailures.TryGetValue(spec.Name, out var status))
        {
            return Task.FromResult(Fail(status));
        }

        if (!_tasks.ContainsKey(spec.Name))
        {
            return Task.FromResult(Fail(ExternalTaskMutationStatus.NotFound));
        }

        _tasks[spec.Name] = ToState(spec);
        return Task.FromResult(new ExternalTaskMutationResult { Status = ExternalTaskMutationStatus.Success });
    }

    public Task<ExternalTaskMutationResult> DeleteAsync(string taskName, CancellationToken cancellationToken)
    {
        Record("Delete", taskName, null);
        if (_deleteFailures.TryGetValue(taskName, out var status))
        {
            return Task.FromResult(Fail(status));
        }

        if (!_tasks.Remove(taskName))
        {
            return Task.FromResult(Fail(ExternalTaskMutationStatus.NotFound));
        }

        return Task.FromResult(new ExternalTaskMutationResult { Status = ExternalTaskMutationStatus.Success });
    }

    private void Record(string op, string name, ExternalTaskSpec? spec)
        => _operations.Add((op, name, spec));

    private static ExternalTaskMutationResult Fail(ExternalTaskMutationStatus status) => new()
    {
        Status = status,
        Message = status.ToString()
    };

    private static ExternalTaskState ToState(ExternalTaskSpec spec) => new()
    {
        Name = spec.Name,
        Enabled = spec.Enabled,
        TriggerSignature = ExternalTriggerSignature.Build(spec.Trigger),
        ActionPath = spec.AppExePath,
        Arguments = spec.TriggerArgument
    };
}
