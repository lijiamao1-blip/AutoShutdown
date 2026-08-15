using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Abstractions;

public interface ITaskStateMachine
{
    TaskTransitionResult TryTransition(
        TaskState current,
        TaskState target,
        TaskTransitionCause cause);
}
