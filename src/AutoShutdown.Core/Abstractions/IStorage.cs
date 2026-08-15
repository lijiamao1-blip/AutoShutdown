using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Abstractions;

public interface IStorage
{
    Task<StorageReadResult<T>> ReadAsync<T>(
        string relativePath,
        CancellationToken cancellationToken);

    Task<StorageWriteResult> WriteAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken);
}
