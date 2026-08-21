using AutoShutdown.App.Infrastructure.Logging;

namespace AutoShutdown.App.Infrastructure;

/// <summary>
/// 启动超时卫兵（S-STARTUP-D1）。主实例获得单实例互斥体后武装，启动生命周期完成
/// （ApplicationStarted，或外部触发 headless 完成）前若超过超时上限，则记录明确错误并
/// 以非零退出码终止进程——进程退出即由操作系统释放命名单实例互斥体，保证：不留任何
/// 无窗口后台进程、后续实例可立即成为主实例。卫兵只是「启动卡死」的兜底，不替代各启动
/// 步骤自身的超时/失败处理；正常启动必须在超时前主动 <see cref="Disarm"/>。
/// </summary>
internal sealed class StartupTimeoutGuard : IDisposable
{
    /// <summary>卫兵触发时的退出码（非零，明确区别于正常退出 0 / UI 测试拒绝 2 / 3）。</summary>
    public const int TimeoutExitCode = 4;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout;
    private readonly IApplicationLogger _logger;
    private readonly Action<int> _terminate;
    private readonly Thread _thread;
    private int _armed;
    private int _disarmed;

    public StartupTimeoutGuard(
        IApplicationLogger logger,
        Action<int>? terminate = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _logger = logger;
        _terminate = terminate ?? Environment.Exit;
        _thread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "AutoShutdownStartupGuard"
        };
    }

    /// <summary>武装卫兵并启动看门狗线程。重复调用为 no-op。</summary>
    public void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) == 1)
        {
            return;
        }

        _thread.Start();
    }

    /// <summary>解除武装：启动生命周期已完成，禁止卫兵误触发。</summary>
    public void Disarm() => Interlocked.Exchange(ref _disarmed, 1);

    public void Dispose() => Disarm();

    private void WatchdogLoop()
    {
        var deadline = Environment.TickCount64 + (long)_timeout.TotalMilliseconds;
        while (Volatile.Read(ref _disarmed) == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                // 已触发：尽力写日志（有界 2 秒；若 UI 线程正占用日志锁，超时后仍须退出），
                // 随后强制非零退出。即使日志写不进去也必须退出，不能留下卡死且不可见的进程。
                try
                {
                    Task.Run(() => _logger.Error(
                        "StartupTimedOut",
                        "启动生命周期超过 " + (int)_timeout.TotalSeconds + " 秒未完成；以非零退出码 "
                        + TimeoutExitCode + " 终止并释放单实例所有权，不留无窗口后台进程。"))
                        .Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // 日志尽力而为；退出优先。
                }

                try
                {
                    _terminate(TimeoutExitCode);
                }
                catch
                {
                    // 进程已退出或终止失败：不再重试。
                }

                return;
            }

            Thread.Sleep(100);
        }
    }
}
