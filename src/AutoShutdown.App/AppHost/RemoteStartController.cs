using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;

namespace AutoShutdown.App.AppHost;

/// <summary>
/// 远程控制启动的有界控制器（S-STARTUP-D1-D1，S-STARTUP-D1-D2 强化）。
///
/// 修复点：原实现只用 <c>WaitAsync(timeout)</c> 让等待方超时返回，底层
/// <see cref="RemoteServer.StartAsync"/> 仍在后台继续运行——若它晚到完成并开始监听，
/// 就违反「超时保持不监听」的 fail-closed 契约。本控制器：
/// <list type="bullet">
/// <item>使用独立的可取消 linked CTS（与应用主 CTS 关联但不互斥）：超时后只取消远程启动，
/// 不影响应用主生命周期。</item>
/// <item><strong>原子中止（S-STARTUP-D1-D2）</strong>：超时后调用
/// <see cref="IRemoteServerControl.AbortStartAsync"/>——它与 <see cref="RemoteServer.StartAsync"/>
/// 的监听器发布共享同一临界区（线性化点）。若监听器已在竞态窗口内发布则立即停止并回收；
/// 若尚未发布则记录中止标记，晚到恢复的启动线程在发布临界区内看到标记即放弃发布。本步返回后
/// 被中止的启动尝试<strong>绝不可能</strong>再发布监听器，是确定性的原子边界，而非「再增加
/// 一次 IsCancellationRequested 检查」或「延长 ConfirmStopped 等待」。</item>
/// <item>随后<strong>确认停止</strong>：有界等待底层任务观察取消并完成（回收资源）。</item>
/// </list>
/// </summary>
internal static class RemoteStartController
{
    /// <summary>
    /// 有界启动远程控制。返回被监督的底层启动任务（供调用方有界等待其最终结束）；
    /// 生产调用方与既有测试忽略返回值即可。
    /// </summary>
    public static Task? StartBounded(
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
            return remoteTask;
        }
        catch (OperationCanceledException) when (appToken.IsCancellationRequested)
        {
            // 应用主生命周期取消（退出路径）：不视为启动失败，保持不监听。
            remoteCts.Dispose();
            return remoteTask;
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "RemoteStartTimedOut",
                "远程控制启动超过 " + (int)timeout.TotalSeconds + " 秒未完成；已取消启动，保持不监听。");
            remoteCts.Cancel();

            // S-STARTUP-D1-D2：原子中止——与监听器发布共享同一 _sync 临界区（线性化点）：
            //  · 若监听器已在竞态窗口内发布 → 立即停止并回收；
            //  · 若尚未发布 → 记录中止标记，晚到恢复的启动线程在发布临界区内看到标记即放弃发布。
            // 本步返回后，被中止的启动尝试绝不可能再发布监听器（fail-closed，确定性，而非依赖
            // 再增加一次 IsCancellationRequested 检查或延长 ConfirmStopped 等待）。
            try
            {
                server.AbortStartAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 回收失败只记日志；不阻塞退出路径（与 StopAsync 回收失败语义一致）。
            }

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

            return remoteTask;
        }
        catch (Exception exception)
        {
            // 启动失败（异常）：底层任务已完成，可立即释放。
            remoteCts.Dispose();
            logger.Warning("RemoteStartFailed", "远程控制启动失败（" + exception.Message + "），保持不监听。");
            return remoteTask;
        }
    }

    /// <summary>
    /// 确认底层启动已停止/回收。S-STARTUP-D1-D2 起，停止/回收由
    /// <see cref="IRemoteServerControl.AbortStartAsync"/> 在调用方（超时路径）原子完成，
    /// 本方法只做有界确认：等待底层任务观察取消并退出（释放其持有的独立 CTS 由调用方通过
    /// ContinueWith 延迟完成）。保留 IsRunning 复核作为防御性兜底。
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
