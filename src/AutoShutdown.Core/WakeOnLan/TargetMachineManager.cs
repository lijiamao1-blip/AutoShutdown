namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// 目标机器管理（S21）。提供稳定 id/名称/MAC/可选广播地址与端口的配置读写。
/// 写入复用 <see cref="TargetMachineStore"/>（IStorage 原子写入 + 备份证据）；
/// 读取严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion。任何损坏/非法/版本不支持
/// 的当前文档都会阻止后续增删改（fail-closed，绝不静默覆盖证据），只读查询返回空清单。
/// </summary>
public sealed class TargetMachineManager
{
    private readonly TargetMachineStore _store;

    public TargetMachineManager(TargetMachineStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<TargetMachinesLoadResult> LoadAsync(CancellationToken cancellationToken)
        => _store.LoadAsync(cancellationToken);

    public async Task<IReadOnlyList<WakeOnLanTarget>> GetAllAsync(CancellationToken cancellationToken)
    {
        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return load.Status == TargetMachinesLoadStatus.Success
            ? load.Document!.Machines
            : Array.Empty<WakeOnLanTarget>();
    }

    public async Task<WakeOnLanTarget?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return load.Status == TargetMachinesLoadStatus.Success
            ? load.Document!.Machines.FirstOrDefault(machine => machine.Id == id)
            : null;
    }

    public async Task<TargetMachineMutationResult> AddAsync(
        WakeOnLanTarget machine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var structuralError = WakeOnLanTarget.GetStructuralError(machine);
        if (structuralError is not null)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.Invalid,
                Error = structuralError
            };
        }

        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = await RequireWritableDocumentAsync(load, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.LoadUnavailable,
                Error = DescribeLoadFailure(load)
            };
        }

        if (current.Machines.Any(existing => existing.Id == machine.Id))
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.DuplicateId,
                Error = "A target machine with this id already exists."
            };
        }

        var updated = current with { Machines = current.Machines.Append(machine).ToList() };
        return await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TargetMachineMutationResult> UpdateAsync(
        WakeOnLanTarget machine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var structuralError = WakeOnLanTarget.GetStructuralError(machine);
        if (structuralError is not null)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.Invalid,
                Error = structuralError
            };
        }

        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = await RequireWritableDocumentAsync(load, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.LoadUnavailable,
                Error = DescribeLoadFailure(load)
            };
        }

        if (!current.Machines.Any(existing => existing.Id == machine.Id))
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.NotFound,
                Error = "The target machine to update does not exist."
            };
        }

        var updated = current with
        {
            Machines = current.Machines
                .Select(existing => existing.Id == machine.Id ? machine : existing)
                .ToList()
        };
        return await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TargetMachineMutationResult> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = await RequireWritableDocumentAsync(load, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.LoadUnavailable,
                Error = DescribeLoadFailure(load)
            };
        }

        var remaining = current.Machines.Where(machine => machine.Id != id).ToList();
        if (remaining.Count == current.Machines.Count)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.NotFound,
                Error = "The target machine to remove does not exist."
            };
        }

        var updated = current with { Machines = remaining };
        return await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TargetMachineMutationResult> PersistAsync(
        TargetMachinesDocument document,
        CancellationToken cancellationToken)
    {
        var save = await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        if (!save.Succeeded)
        {
            return new TargetMachineMutationResult
            {
                Status = TargetMachineMutationStatus.IoFailure,
                Error = string.Join(" ", save.Errors)
            };
        }

        return new TargetMachineMutationResult
        {
            Status = TargetMachineMutationStatus.Success,
            Document = document,
            Error = save.BackupPath is null ? null : "Backup: " + save.BackupPath
        };
    }

    private static Task<TargetMachinesDocument?> RequireWritableDocumentAsync(
        TargetMachinesLoadResult load,
        CancellationToken cancellationToken)
    {
        // NotFound 视为空清单（可首次写入）；Success 直接使用；其余状态一律拒绝写入。
        if (load.Status == TargetMachinesLoadStatus.NotFound)
        {
            return Task.FromResult<TargetMachinesDocument?>(new TargetMachinesDocument
            {
                SchemaVersion = TargetMachinesDocument.CurrentSchemaVersion,
                Machines = []
            });
        }

        if (load.Status == TargetMachinesLoadStatus.Success)
        {
            return Task.FromResult(load.Document);
        }

        return Task.FromResult<TargetMachinesDocument?>(null);
    }

    private static string DescribeLoadFailure(TargetMachinesLoadResult load)
        => load.Errors.Count > 0
            ? string.Join(" ", load.Errors)
            : $"The target machines document is not writable (status {load.Status}).";
}
