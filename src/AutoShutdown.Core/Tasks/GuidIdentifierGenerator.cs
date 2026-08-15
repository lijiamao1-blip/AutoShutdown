using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Tasks;

public sealed class GuidIdentifierGenerator : IIdentifierGenerator
{
    public Guid NewId() => Guid.NewGuid();
}
