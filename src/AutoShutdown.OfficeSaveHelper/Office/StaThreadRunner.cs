using System.Runtime.ExceptionServices;

namespace AutoShutdown.OfficeSaveHelper.Office;

/// <summary>
/// 在专用 STA 线程上同步执行并等待返回，供 Office COM 自动化边界使用（S17）。
/// 辅助进程由父进程按单应用期限硬终止（绝不遗留），故此处不承担超时职责；
/// 仅保证 COM 调用在 STA 单元内执行。
/// </summary>
internal static class StaThreadRunner
{
    public static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }
}
