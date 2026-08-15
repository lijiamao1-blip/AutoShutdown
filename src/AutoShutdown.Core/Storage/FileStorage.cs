using System.Text.Json;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Storage;

public sealed class FileStorage : IStorage, IDisposable
{
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
