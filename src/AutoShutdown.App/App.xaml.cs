using System.IO;
using System.Windows;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace AutoShutdown.App;

public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    private ApplicationLifetimeCoordinator? _coordinator;
    private IApplicationLogger? _bootstrapLogger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logger = new FileApplicationLogger(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoShutdown",
            "logs"));
        _bootstrapLogger = logger;
        logger.Info("ApplicationStarting", "应用启动。");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogUnhandled(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnhandled(args.Exception);
            // Do not swallow the exception; keep the default crash behavior.
        };

        var singleInstance = new SingleInstanceCoordinator();
        var acquire = singleInstance.TryAcquirePrimary();
        if (acquire.Result != SingleInstanceResult.Primary)
        {
            if (acquire.Result == SingleInstanceResult.Secondary)
            {
                logger.Warning(
                    "SecondaryInstanceDetected",
                    "检测到主实例已在运行，发送激活通知后退出。");

                try
                {
                    ActivationPipeClient.TryActivateAsync(CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine("Activation failed: " + exception.Message);
                }
            }
            else
            {
                logger.Warning(
                    "ApplicationStartupFailed",
                    "单实例互斥获取失败：" + acquire.Message);
            }

            singleInstance.Dispose();
            Shutdown();
            return;
        }

        logger.Info("PrimaryInstanceAcquired", "本实例成为主实例。");

        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        services.AddSingleton(singleInstance);
        // Override the default logger registration so the bootstrap instance
        // (which already wrote startup events) remains the single sink.
        services.AddSingleton<IApplicationLogger>(logger);
        _serviceProvider = services.BuildServiceProvider();

        _coordinator = _serviceProvider.GetRequiredService<ApplicationLifetimeCoordinator>();
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrapLogger?.Info("ApplicationStopped", "应用已退出。");

        try
        {
            _coordinator?.Dispose();
            (_serviceProvider as IDisposable)?.Dispose();
        }
        catch
        {
            // Best-effort cleanup must never throw an unhandled exception.
        }

        _bootstrapLogger?.Dispose();

        base.OnExit(e);
    }

    private void LogUnhandled(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        _bootstrapLogger?.Error("UnhandledException", "捕获到未处理异常", exception);
    }
}
