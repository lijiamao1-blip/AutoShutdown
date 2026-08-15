using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Abstractions;

public interface ITaskService
{
    TaskCommandResult Create(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone);

    TaskCommandResult Snooze(
        TaskInstance current,
        TimeSpan duration,
        DateTimeOffset now);

    TaskCommandResult Cancel(TaskInstance current);

    TaskCommandResult RescheduleDaily(
        TaskDefinition definition,
        TaskInstance current,
        DateTimeOffset now,
        TimeZoneInfo timeZone);
}
