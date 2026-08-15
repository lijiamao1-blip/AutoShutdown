namespace AutoShutdown.Core.Abstractions;

public interface IIdentifierGenerator
{
    Guid NewId();
}
