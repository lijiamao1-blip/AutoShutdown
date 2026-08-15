using AutoShutdown.Core.State;

namespace AutoShutdown.App.Notifications;

public interface INotificationService
{
    void ShowReminder(TaskInstance instance);

    void CloseReminder(Guid instanceId);
}
