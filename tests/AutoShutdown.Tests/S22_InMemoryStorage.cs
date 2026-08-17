using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Tests;

/// <summary>S22 测试共享 Fake IStorage：内存文档 + 注入读/写失败；记录写入路径。</summary>
internal sealed class InMemoryStorage : IStorage
{
    private readonly Dictionary<string, JsonElement> _documents = new();
    private readonly List<string> _writePaths = new();

    public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

    public StorageWriteStatus WriteStatus { get; init; } = StorageWriteStatus.Success;

    public int WriteCount => _writePaths.Count;

    public IReadOnlyList<string> WritePaths => _writePaths;

    public void Seed(string relativePath, string json)
    {
        using var document = JsonDocument.Parse(json);
        _documents[relativePath] = document.RootElement.Clone();
    }

    public Task<StorageReadResult<T>> ReadAsync<T>(
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (ReadStatus != StorageReadStatus.Success)
        {
            return Task.FromResult(new StorageReadResult<T>
            {
                Status = ReadStatus,
                Error = "Simulated read failure."
            });
        }

        if (!_documents.TryGetValue(relativePath, out var element))
        {
            return Task.FromResult(new StorageReadResult<T>
            {
                Status = StorageReadStatus.NotFound
            });
        }

        return Task.FromResult(new StorageReadResult<T>
        {
            Status = StorageReadStatus.Success,
            Value = element.Deserialize<T>()
        });
    }

    public Task<StorageWriteResult> WriteAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken)
    {
        if (WriteStatus != StorageWriteStatus.Success)
        {
            return Task.FromResult(new StorageWriteResult
            {
                Status = WriteStatus,
                Error = "Simulated write failure."
            });
        }

        _writePaths.Add(relativePath);
        return Task.FromResult(new StorageWriteResult
        {
            Status = StorageWriteStatus.Success,
            BackupPath = relativePath + ".bak"
        });
    }
}
