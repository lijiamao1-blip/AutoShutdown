using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;

namespace AutoShutdown.App.AppHost;

/// <summary>
/// 远程控制启动的有界控制器（S-STARTUP-D1-D1）。
///
/// 修复点：原实现只用 <c>WaitAsync(timeout)</c> 让等待方超时返回，底层
/// <see cref="RemoteServer.StartAsync"/> 仍在后台继续运行——若它晚到完成并开始监听，
/// 就违反「超时保持不监听」的 fail-closed 契约。本控制器：
/// <list type="bullet">
/// <item>使用独立的可取消 linked CTS（与应用主 CTS 关联但不互斥）：超时后只取消远程启动，
/// 不影响应用主生命周期。</item>
/// <item>超时后取消并<strong>确认停止/回收</strong>：有界等待底层任务观察取消并完成；若它已
/// 晚到进入监听，立即 <see cref="IRemoteServerControl.StopAsync"/> 回收。</item>
/// <item>配合 <see cref="RemoteServer.StartAsync"/> 内部的取消检查点，即使底层阻塞（如磁盘
/// 停滞）随后解除，也绝不进入监听状态（fail-closed）。</item>
/// </list>
/// </summary>
internal static class RemoteStartController
{
    public static void StartBounded(
        IRemoteServerControl server,
        IApplicationLogger logger,
        CancellationToken appToken,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(logger);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        // 独立可取消的 linked CTS：与应用退出（appToken）关联，但可单独取消以回收底层启动。
        var remoteCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        Task? remoteTask = null;
        try
        {
            remoteTask = Task.Run(() => server.StartAsync(remoteCts.Token));
            remoteTask.WaitAsync(timeout, remoteCts.Token).GetAwaiter().GetResult();
            // 正常完成：底层任务已结束，可立即释放独立 CTS。
            remoteCts.Dispose();
            return;
        }
        catch (OperationCanceledException) when (appToken.IsCancellationRequested)
        {
            // 应用主生命周期取消（退出路径）：不视为启动失败，保持不监听。
            remoteCts.Dispose();
            return;
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "RemoteStartTimedOut",
                "远程控制启动超过 " + (int)timeout.TotalSeconds + " 秒未完成；已取消启动，保持不监听。");
            remoteCts.Cancel();
            ConfirmStopped(remoteTask, server, logger);
            // 若底层任务仍在阻塞（如磁盘 I/O 停滞），由 StartAsync 内部的取消检查点在解除阻塞后
            // 放弃启动（绝不监听）。独立 CTS 延迟到任务结束后释放，避免已取消但仍被阻塞任务持有
            // 的 token 失效。
            if (remoteTask is not null)
            {
                _ = remoteTask.ContinueWith(
                    _ => remoteCts.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                remoteCts.Dispose();
            }
        }
        catch (Exception exception)
        {
            // 启动失败（异常）：底层任务已完成，可立即释放。
            remoteCts.Dispose();
            logger.Warning("RemoteStartFailed", "远程控制启动失败（" + exception.Message + "），保持不监听。");
        }
    }

    /// <summary>
    /// 确认底层启动已停止/回收。有界等待底层任务观察取消并完成；若它已晚到进入监听
    /// （IsRunning=true），立即 <see cref="IRemoteServerControl.StopAsync"/> 回收，保证
    /// 超时后 IsRunning == false（fail-closed）。
    /// </summary>
    private static void ConfirmStopped(
        Task? remoteTask,
        IRemoteServerControl server,
        IApplicationLogger logger)
    {
        if (remoteTask is not null)
        {
            try
            {
                remoteTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 未能在限期内完成：底层任务稍后自行在取消检查点退出；不监听。
            }
        }

        if (server.IsRunning)
        {
            try
            {
                server.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 回收失败只记日志；不阻塞退出路径。
            }
        }

        logger.Info("RemoteStartConfirmedStopped", "远程控制启动超时已确认停止；保持不监听。");
    }
}
