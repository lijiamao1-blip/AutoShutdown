using System.Text.Json;
using System.Text.Json.Serialization;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class FileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AutoShutdown.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_WhenFileIsMissing_ReturnsNotFound()
    {
        using var storage = new FileStorage(_root);

        var result = await storage.ReadAsync<StoredDocument>(
            "config.json",
            CancellationToken.None);

        Assert.Equal(StorageReadStatus.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsDocument()
    {
        using var storage = new FileStorage(_root);
        var document = new StoredDocument("safe", 7);

        var write = await storage.WriteAsync(
            "config.json",
            document,
            CancellationToken.None);
        var read = await storage.ReadAsync<StoredDocument>(
            "config.json",
            CancellationToken.None);

        Assert.Equal(StorageWriteStatus.Success, write.Status);
        Assert.Equal(StorageReadStatus.Success, read.Status);
        Assert.Equal(document, read.Value);
    }

    [Fact]
    public async Task ReadAsync_WhenJsonIsBroken_ReturnsCorrupt()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "config.json"),
            "{ incomplete",
            CancellationToken.None);
        using var storage = new FileStorage(_root);

        var result = await storage.ReadAsync<StoredDocument>(
            "config.json",
            CancellationToken.None);

        Assert.Equal(StorageReadStatus.Corrupt, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ReplacingFile_PreservesPreviousVersionInBackupsDirectory()
    {
        using var storage = new FileStorage(_root);
        var original = new StoredDocument("original", 1);
        var replacement = new StoredDocument("replacement", 2);
        await storage.WriteAsync("config.json", original, CancellationToken.None);

        var result = await storage.WriteAsync(
            "config.json",
            replacement,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        var backupJson = await File.ReadAllTextAsync(result.BackupPath);
        Assert.Equal(original, JsonSerializer.Deserialize<StoredDocument>(backupJson));
        var current = await storage.ReadAsync<StoredDocument>(
            "config.json",
            CancellationToken.None);
        Assert.Equal(replacement, current.Value);
    }

    [Fact]
    public async Task InterruptedSerialization_LeavesOfficialFileUntouchedAndNoTempFile()
    {
        using var initialStorage = new FileStorage(_root);
        var original = new StoredDocument("original", 1);
        await initialStorage.WriteAsync("config.json", original, CancellationToken.None);

        var options = new JsonSerializerOptions();
        options.Converters.Add(new ExplodingPayloadConverter());
        using var failingStorage = new FileStorage(_root, options);

        var result = await failingStorage.WriteAsync(
            "config.json",
            new ExplodingPayload(),
            CancellationToken.None);

        Assert.Equal(StorageWriteStatus.SerializationFailure, result.Status);
        var current = await initialStorage.ReadAsync<StoredDocument>(
            "config.json",
            CancellationToken.None);
        Assert.Equal(original, current.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("C:\\outside.json")]
    public async Task PathsOutsideDataRoot_AreRejected(string path)
    {
        using var storage = new FileStorage(_root);

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.WriteAsync(path, new StoredDocument("x", 1), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record StoredDocument(string Name, int Value);

    private sealed class ExplodingPayload;

    private sealed class ExplodingPayloadConverter : JsonConverter<ExplodingPayload>
    {
        public override ExplodingPayload? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new JsonException("Simulated corruption.");

        public override void Write(
            Utf8JsonWriter writer,
            ExplodingPayload value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("partial", "data");
            throw new JsonException("Simulated interrupted serialization.");
        }
    }
}
