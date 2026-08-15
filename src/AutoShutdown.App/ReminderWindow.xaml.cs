using System.Windows;
using System.Windows.Threading;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.App;

public partial class ReminderWindow : Window
{
    private readonly TaskInstance _instance;
    private readonly ISchedulerEngine _engine;
    private readonly IClock _clock;
    private readonly IApplicationLogger _logger;
    private readonly DispatcherTimer _timer;
    private bool _submitting;
    private bool _closeLogged;

    public ReminderWindow(
        TaskInstance instance,
        ISchedulerEngine engine,
        IClock clock,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        InitializeComponent();
        _instance = instance;
        _engine = engine;
        _clock = clock;
        _logger = logger;

        MessageText.Text = $"计划将在剩余时间后执行{UiTextMapper.Map(instance.ActionSnapshot)}操作。";
        UpdateCountdown();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
    }

    private void UpdateCountdown()
    {
        var remaining = _instance.ScheduledFireTime - _clock.UtcNow;
        CountdownText.Text = remaining > TimeSpan.Zero
            ? remaining.ToString(@"hh\:mm\:ss")
            : "即将执行";
    }

    private async void SnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting)
        {
            return;
        }

        _submitting = true;
        ShowError(string.Empty);

        try
        {
            var result = await _engine.SubmitAsync(
                new SnoozeTaskCommand(_instance.InstanceId, _instance.StageToken, TimeSpan.FromMinutes(10)),
                CancellationToken.None);

            if (result.Succeeded)
            {
                _closeLogged = true;
                _logger.Info("ReminderSnoozed", "提醒窗口：延迟 10 分钟已提交。");
                Close();
            }
            else
            {
                _logger.Warning(
                    "ReminderSnoozed",
                    "提醒窗口：延迟失败，结果：" + result.Status);
                ShowError("延迟失败：" + UiTextMapper.MapCommand(result.Status));
            }
        }
        catch (Exception exception)
        {
            _logger.Warning("ReminderSnoozed", "提醒窗口：延迟提交异常。");
            ShowError("延迟失败：" + exception.Message);
        }
        finally
        {
            _submitting = false;
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting)
        {
            return;
        }

        _submitting = true;
        ShowError(string.Empty);

        try
        {
            var result = await _engine.SubmitAsync(
                new CancelTaskCommand(_instance.InstanceId, _instance.StageToken),
                CancellationToken.None);

            if (result.Succeeded)
            {
                _closeLogged = true;
                _logger.Info("ReminderCancelled", "提醒窗口：取消任务已提交。");
                Close();
            }
            else
            {
                _logger.Warning(
                    "ReminderCancelled",
                    "提醒窗口：取消失败，结果：" + result.Status);
                ShowError("取消失败：" + UiTextMapper.MapCommand(result.Status));
            }
        }
        catch (Exception exception)
        {
            _logger.Warning("ReminderCancelled", "提醒窗口：取消提交异常。");
            ShowError("取消失败：" + exception.Message);
        }
        finally
        {
            _submitting = false;
        }
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (!_closeLogged)
        {
            _logger.Info("ReminderDismissed", "提醒窗口已关闭。");
        }

        base.OnClosed(e);
    }
}
