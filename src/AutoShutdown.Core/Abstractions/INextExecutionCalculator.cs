using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Abstractions;

public interface INextExecutionCalculator
{
    NextExecutionResult Calculate(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone);
}
