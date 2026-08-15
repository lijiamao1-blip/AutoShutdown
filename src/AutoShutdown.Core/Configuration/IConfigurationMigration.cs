using System.Text.Json;

namespace AutoShutdown.Core.Configuration;

public interface IConfigurationMigration
{
    int SourceVersion { get; }

    int TargetVersion { get; }

    Task<JsonElement> MigrateAsync(
        JsonElement source,
        CancellationToken cancellationToken);
}
