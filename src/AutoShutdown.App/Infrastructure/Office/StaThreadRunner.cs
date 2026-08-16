using System.Runtime.ExceptionServices;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// 在专用 STA 线程上同步执行并等待返回，供 Office COM 自动化边界使用（S17）。
/// 取消为协作式：工作负载在循环内检查令牌；无法强杀卡死的 COM 调用（真机限制，
/// 已记录为未执行项）。
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
