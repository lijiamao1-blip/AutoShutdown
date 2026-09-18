using System.Text.Json;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Storage;

public sealed class FileStorage : IStorage, IDisposable
{
    /// <summary>
    /// 每个逻辑文件保留的 .bak 备份份数上限（S-STORE-D1）。
    ///
    /// 此前每次写入都会生成一个以 GUID 命名的备份，且全代码库没有任何清理逻辑
    /// （日志有 LogRetentionService，备份没有）。任务每次状态迁移都会写一次运行时状态，
    /// 长期运行的机器上 backups 目录会累积成千上万个小文件——单个只有几 KB，撑不爆磁盘，
    /// 但会让目录枚举变慢、也让"出问题时翻备份"这件事实际不可用。
    ///
    /// 保留最近 N 份即可满足备份的真实用途（回滚到上一个好状态）。按份数而不是按天数保留：
    /// 份数是确定性的，不依赖系统时钟，也便于测试断言。
    /// </summary>
    private const int MaxBackupsPerFile = 10;

    private readonly string _dataRoot;
    private readonly string _backupRoot;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FileStorage(string dataRoot, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        _dataRoot = Path.GetFullPath(dataRoot);
        _backupRoot = Path.Combine(_dataRoot, "backups");
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public async Task<StorageReadResult<T>> ReadAsync<T>(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = ResolveDataPath(relativePath);

        if (!File.Exists(path))
        {
            return new StorageReadResult<T> { Status = StorageReadStatus.NotFound };
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var value = await JsonSerializer.DeserializeAsync<T>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);

            return value is null
                ? new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Corrupt,
                    Error = "The JSON document contains no value."
                }
                : new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = value
                };
        }
        catch (JsonException exception)
        {
            return new StorageReadResult<T>
            {
                Status = StorageReadStatus.Corrupt,
                Error = exception.Message
            };
        }
        catch (IOException exception)
        {
            return new StorageReadResult<T>
            {
                Status = StorageReadStatus.IoFailure,
                Error = exception.Message
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            return new StorageReadResult<T>
            {
                Status = StorageReadStatus.IoFailure,
                Error = exception.Message
            };
        }
    }

    public async Task<StorageWriteResult> WriteAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var targetPath = ResolveDataPath(relativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(targetDirectory);

            try
            {
                await WriteAndFlushAsync(temporaryPath, value, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return FailedWrite(StorageWriteStatus.SerializationFailure, exception);
            }
            catch (NotSupportedException exception)
            {
                return FailedWrite(StorageWriteStatus.SerializationFailure, exception);
            }

            if (!await CanDeserializeAsync<T>(temporaryPath, cancellationToken).ConfigureAwait(false))
            {
                return new StorageWriteResult
                {
                    Status = StorageWriteStatus.ValidationFailure,
                    Error = "The temporary file could not be deserialized."
                };
            }

            string? backupPath = null;
            if (File.Exists(targetPath))
            {
                Directory.CreateDirectory(_backupRoot);
                backupPath = CreateBackupPath(targetPath);
                File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
                // 备份保留：尽力而为，绝不因清理失败影响这次写入的成功判定。
                PruneBackups(Path.GetFileName(targetPath));
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }

            return new StorageWriteResult
            {
                Status = StorageWriteStatus.Success,
                BackupPath = backupPath
            };
        }
        catch (IOException exception)
        {
            return FailedWrite(StorageWriteStatus.IoFailure, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return FailedWrite(StorageWriteStatus.IoFailure, exception);
        }
        finally
        {
            TryDelete(temporaryPath);
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();

    private async Task WriteAndFlushAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough);

        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task<bool> CanDeserializeAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private string ResolveDataPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The storage path must be relative.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_dataRoot, relativePath));
        var rootPrefix = _dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _dataRoot
            : _dataRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The storage path leaves the data root.", nameof(relativePath));
        }

        return fullPath;
    }

    /// <summary>
    /// 只保留某个逻辑文件最近 <see cref="MaxBackupsPerFile"/> 份备份，其余按最后写入时间删除。
    /// 全程尽力而为：目录不存在、枚举失败、单个文件被占用都直接跳过，绝不抛出——
    /// 备份清理是卫生工作，不能反过来让一次成功的写入被判为失败。
    /// 调用方须持有 <see cref="_writeGate"/>（在 WriteAsync 的写入临界区内调用），
    /// 因此不会与并发写入产生竞争。
    /// </summary>
    private void PruneBackups(string targetFileName)
    {
        try
        {
            if (!Directory.Exists(_backupRoot))
            {
                return;
            }

            var prefix = targetFileName + ".";
            // 通配符筛选后再在托管侧复核前后缀：Windows 的通配符匹配会连带命中 8.3 短名，
            // 单靠 GetFiles 的模式可能误伤同目录下的其他文件。
            var candidates = Directory.GetFiles(_backupRoot, prefix + "*.bak")
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(MaxBackupsPerFile)
                .ToList();

            foreach (var stale in candidates)
            {
                try
                {
                    stale.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateBackupPath(string targetPath)
    {
        var fileName = Path.GetFileName(targetPath);
        return Path.Combine(
            _backupRoot,
            $"{fileName}.{Guid.NewGuid():N}.bak");
    }

    private static StorageWriteResult FailedWrite(
        StorageWriteStatus status,
        Exception exception) => new()
        {
            Status = status,
            Error = exception.Message
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
