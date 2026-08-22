namespace AutoShutdown.App.Infrastructure;

public interface IWindowActivationService
{
    void ActivateMainWindow();

    /// <summary>
    /// 启动未就绪协议（S-STARTUP-D1-D1）：调用方先应答「请求已接收」，激活排队到 UI 线程
    /// 在就绪后执行；本方法绝不阻塞调用方（激活管道不得因主实例 UI 线程被启动步骤阻塞而挂起）。
    /// 若当前无 WPF 调度器或已进入退出，为有界失败，静默忽略。
    /// </summary>
    void ActivateMainWindowDeferred();

    bool IsExiting { get; }

    void MarkExiting();
}
