using AutoShutdown.Core.Configuration;

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
}
