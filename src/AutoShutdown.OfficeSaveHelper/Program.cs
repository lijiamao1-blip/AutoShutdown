using AutoShutdown.Core.Office;
using AutoShutdown.OfficeSaveHelper.Office;

namespace AutoShutdown.OfficeSaveHelper;

/// <summary>
/// Office 保存辅助进程入口（S17 独立验收 D2）。每次只处理一个 Office 应用：仅附加
/// Running Object Table 中已运行实例（绝不创建/绝不 Quit），保存后向 stdout 输出
/// 固定最小结果并立即退出。内置最大生存期看门狗：即使父进程异常退出也会在期限后
/// 自终止，不遗留进程。
/// </summary>
public static class Program
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(60);

    public static int Main(string[] args)
    {
        if (!HelperArguments.TryParse(args, out var application, out var correlationId))
        {
            // 参数非法：stdout 不写任何结果；stderr 仅固定错误码，不泄露参数内容。
            Console.Error.WriteLine("helper-error: invalid arguments");
            return ExitCodes.BadArguments;
        }

        // 最大生存期看门狗：父进程异常退出/遗留时自终止，绝不留存后台进程。
        using var watchdog = StartWatchdog(MaxLifetime);

        var worker = new OfficeComSaveWorker(new RotOfficeComGateway());
        OfficeApplicationSaveResult result;
        try
        {
            result = worker.Save(application, CancellationToken.None);
        }
        catch (Exception)
        {
            // 不写未脱敏异常/路径/文档信息；固定 NotDetected 结果 + 非零退出码。
            result = new OfficeApplicationSaveResult
            {
                Application = application,
                Status = OfficeAppStatus.NotDetected
            };
            Console.Out.WriteLine(OfficeSaveHelperProtocol.Format(result));
            return ExitCodes.UnexpectedError;
        }

        Console.Out.WriteLine(OfficeSaveHelperProtocol.Format(result));
        return ExitCodes.Success;
    }

    private static IDisposable StartWatchdog(TimeSpan lifetime)
    {
        var timer = new System.Threading.Timer(
            _ => Environment.Exit(ExitCodes.WatchdogExpired),
            null,
            lifetime,
            Timeout.InfiniteTimeSpan);
        return new TimerDisposer(timer);
    }

    private sealed class TimerDisposer : IDisposable
    {
        private readonly System.Threading.Timer _timer;

        public TimerDisposer(System.Threading.Timer timer) => _timer = timer;

        public void Dispose() => _timer.Dispose();
    }
}

/// <summary>辅助进程退出码（固定、非负；父进程据此区分失败与看门狗超时）。</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int BadArguments = 2;
    public const int WatchdogExpired = 3;
}
