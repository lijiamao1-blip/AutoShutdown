using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoShutdown.App;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Appearance;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// 本地渲染探针（默认跳过）：设置环境变量 AUTOSHUTDOWN_RENDER_PROBE_OUT=目录 后运行，把主窗口各页、
/// 紧急态首页、皮肤画廊浮层与提醒窗口（倒计时 / 紧急 / 执行中）离屏渲染为 PNG，用于皮肤改造前后逐像素比对
/// 与逐套皮肤验收。AUTOSHUTDOWN_RENDER_PROBE_SKIN 为空时只渲染当前（经典）皮肤且文件名不带前缀；
/// 为 all 时渲染目录中每套皮肤，各自输出到 &lt;目录&gt;/&lt;皮肤Id&gt;/。不执行任何电源操作。
/// </summary>
public sealed class SkinRenderProbe
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public void RenderAllPages()
    {
        var output = Environment.GetEnvironmentVariable("AUTOSHUTDOWN_RENDER_PROBE_OUT");
        Skip.If(string.IsNullOrWhiteSpace(output), "未设置 AUTOSHUTDOWN_RENDER_PROBE_OUT。");
        Directory.CreateDirectory(output!);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run(output!);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("渲染失败", failure);
        }
    }

    private static void Run(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        PointResourceAssemblyAtApp();
        var app = new AutoShutdown.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var request = Environment.GetEnvironmentVariable("AUTOSHUTDOWN_RENDER_PROBE_SKIN");
        var appearanceRoot = Path.Combine(Path.GetTempPath(), "AutoShutdown.RenderProbe." + Guid.NewGuid().ToString("N"));
        var skins = new SkinService(new AppearanceSettingsStore(new FileStorage(appearanceRoot)), new WpfSkinResourceApplier(), new NullLogger());
        var targets = string.IsNullOrWhiteSpace(request)
            ? [(Id: (string?)null, Folder: output)]
            : SkinCatalog.All
                .Where(skin => request == "all" || request.Split(',').Contains(skin.Id))
                .Select(skin => (Id: (string?)skin.Id, Folder: Path.Combine(output, skin.Id)))
                .ToList();

        foreach (var (id, folder) in targets)
        {
            Directory.CreateDirectory(folder);
            if (id is not null)
            {
                Wait(skins.SelectAsync(id, CancellationToken.None));
            }

            RenderSkin(folder, skins, full: id is not null);
        }

        try
        {
            Directory.Delete(appearanceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void RenderSkin(string folder, ISkinService skins, bool full)
    {
        var instance = new TaskInstance
        {
            InstanceId = Guid.Parse("aaaa0000-0000-0000-0000-000000000011"),
            SourceTaskId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001"),
            ActionSnapshot = PowerAction.Shutdown,
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = Now.AddHours(1).AddMinutes(23).AddSeconds(45),
            StageToken = Guid.Parse("aaaa0000-0000-0000-0000-000000000012"),
            HasExecuted = false,
            CreatedAt = Now,
            IsEnabled = true
        };
        var viewModel = CreateViewModel(instance, full ? new SkinSectionViewModel(skins) : null);

        foreach (var (width, height) in new[] { (1080, 640), (900, 580) })
        {
            var window = Show(viewModel, width, height);
            for (var index = 0; index < viewModel.NavItems.Count; index++)
            {
                viewModel.SelectedNav = viewModel.NavItems[index];
                Save(window, Path.Combine(folder, $"{width}x{height}-{index}-{viewModel.NavItems[index].PageKey}.png"));
            }

            viewModel.SelectedNav = viewModel.NavItems[0];
            if (full && width == 1080)
            {
                viewModel.SkinSection!.IsGalleryOpen = true;
                var popup = (Popup)window.FindName("SkinGalleryPopup");
                Pump();
                SaveElement((FrameworkElement)popup.Child, Path.Combine(folder, "gallery.png"));
                viewModel.SkinSection.IsGalleryOpen = false;
            }

            window.Hide();
        }

        if (full)
        {
            var urgent = instance with { ScheduledFireTime = Now.AddSeconds(45) };
            var urgentWindow = Show(CreateViewModel(urgent, null), 1080, 640);
            Save(urgentWindow, Path.Combine(folder, "1080x640-urgent-home.png"));
            urgentWindow.Hide();
        }

        var reminders = new List<(string Name, TaskInstance Instance)>
        {
            ("reminder-Waiting", instance with { ScheduledFireTime = Now.AddMinutes(4).AddSeconds(30) }),
            ("reminder-Executing", instance with { State = TaskInstanceState.Executing, ScheduledFireTime = Now.AddMinutes(4).AddSeconds(30) })
        };
        if (full)
        {
            reminders.Add(("reminder-Urgent", instance with { ScheduledFireTime = Now.AddSeconds(45) }));
        }

        foreach (var (name, reminderInstance) in reminders)
        {
            var reminder = new ReminderWindow(reminderInstance, new FakeSchedulerEngine(reminderInstance), new FakeClock(Now), new NullLogger())
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000
            };
            reminder.Show();
            Save(reminder, Path.Combine(folder, name + ".png"));
            reminder.Close();
        }
    }

    private static MainWindowViewModel CreateViewModel(TaskInstance instance, SkinSectionViewModel? skinSection)
    {
        var viewModel = new MainWindowViewModel(
            new FakeSchedulerEngine(instance),
            new FakeConfigurationService(),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            skinSection: skinSection);
        Wait(viewModel.InitializeAsync());
        return viewModel;
    }

    private static MainWindow Show(MainWindowViewModel viewModel, int width, int height)
    {
        var window = new MainWindow(viewModel, new FakeActivation())
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.SourceInitialized += (_, _) =>
        {
            window.Left = -20000;
            window.Top = -20000;
            window.Width = width;
            window.Height = height;
        };
        window.Show();
        return window;
    }

    private static void PointResourceAssemblyAtApp()
    {
        // 测试宿主的入口程序集不是 App：把「/assets/icon.ico」这类应用根相对资源指回 App 程序集。
        var assembly = typeof(MainWindow).Assembly;
        typeof(System.Windows.Application)
            .GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, assembly);
        var helper = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Navigation.BaseUriHelper", throwOnError: true)!;
        helper.GetProperty("ResourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, assembly);
    }

    private static void Save(Window window, string path)
    {
        Pump();
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        Encode(bitmap, path);
    }

    private static void SaveElement(FrameworkElement element, string path)
    {
        Pump();
        var width = (int)Math.Ceiling(element.ActualWidth + element.Margin.Left + element.Margin.Right);
        var height = (int)Math.Ceiling(element.ActualHeight + element.Margin.Top + element.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        Encode(bitmap, path);
    }

    private static void Encode(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class FakeSchedulerEngine(TaskInstance instance) : ISchedulerEngine
    {
        private readonly SchedulerSnapshot _snapshot = new()
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
            LastUpdatedAt = Now
        };

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(SchedulerCommand command, CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult { Status = SchedulerCommandStatus.Success, Snapshot = _snapshot, Message = "ok" });

        public SchedulerSnapshot GetSnapshot() => _snapshot;
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = new AppConfig
                {
                    SchemaVersion = 1,
                    TestMode = true,
                    AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                    Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
                }
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TasksLoadResult { Status = TasksLoadStatus.NotFound });

        public Task<TasksSaveResult> SaveTasksAsync(TasksDocument document, CancellationToken cancellationToken)
            => Task.FromResult(new TasksSaveResult { Status = TasksSaveStatus.Success });
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NoOpAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable() => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable() => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair() => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => false;

        public void Log(ApplicationLogLevel level, string eventName, string message, Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeActivation : IWindowActivationService
    {
        public bool IsExiting => true;

        public void ActivateMainWindow()
        {
        }

        public void ActivateMainWindowDeferred()
        {
        }

        public void MarkExiting()
        {
        }
    }
}
