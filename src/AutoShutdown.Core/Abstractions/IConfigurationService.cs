using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Abstractions;

public interface IConfigurationService
{
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task<ConfigurationSaveResult> SaveAsync(
        AppConfig config,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a safe test-mode default configuration and persists it through
    /// the regular validation and atomic storage path. Must never pretend a
    /// missing configuration loaded successfully.
    /// </summary>
    Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads tasks.json (independent of config.json). Distinguishes NotFound
    /// (file absent) from Corrupt (file present but broken); corrupt data is
    /// never silently turned into an empty or default task set.
    /// </summary>
    /// <remarks>
    /// Provided as a default interface method (fail-fast) so existing
    /// test doubles of <see cref="IConfigurationService"/> remain valid without
    /// being forced to grow a tasks.json surface. The production
    /// <see cref="ConfigurationService"/> overrides both methods.
    /// </remarks>
    Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    /// <summary>Writes tasks.json through the same atomic storage path as config.json.</summary>
    Task<TasksSaveResult> SaveTasksAsync(
        TasksDocument document,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
