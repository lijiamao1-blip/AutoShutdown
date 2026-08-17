using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Remote;

/// <summary>
/// 一次已解析的远程请求上下文。WhiteList 为请求时刻的只读白名单快照（来自 remote-settings.json，
/// 由服务器加载后传入）；本处理器对任何配置/白名单/无人值守策略均只读不写。
/// </summary>
public sealed record RemoteRequestContext
{
    /// <summary>请求时刻的远程命令白名单快照（只读）。</summary>
    public required RemoteCommandWhiteList WhiteList { get; init; }

    /// <summary>是否经 TLS 连接（配对与无人值守等效确认的前置）。</summary>
    public required bool IsTls { get; init; }

    /// <summary>来源 IP（仅审计，不用于鉴权）。</summary>
    public required string SourceIp { get; init; }

    /// <summary>已解析的信封（含被签名的 payload 原始字符串与 HMAC）。</summary>
    public required RemoteRequestEnvelope Envelope { get; init; }

    /// <summary>已解析的 payload。</summary>
    public required RemotePayload Payload { get; init; }
}

/// <summary>
/// S23 请求分发器（CP3/CP4）：把经 TLS + 信封解析后的远程请求路由到
/// 配对 / 只读查询 / 受控触发 / 取消。硬性边界：
/// <list type="bullet">
/// <item>pair 仅在 TLS 连接上受理（sharedSecret 只经 TLS 交付）。</item>
/// <item>其它方法必须通过 HMAC 鉴权（时间窗 + nonce 防重放 + 常量时间校验）。</item>
/// <item>任何命令先查独立远程白名单（默认仅 queryStatus/listTasks；触发命令需本地显式启用）。</item>
/// <item>触发命令经 <see cref="ISchedulerEngine.SubmitAsync"/> 进入本地唯一仲裁/执行路径，
/// 本处理器绝不直接调用 IPowerService/IShutdownWorkflow；无人值守等效确认仅在
/// TLS + 白名单 + 任务 UseUnattended 三者齐备时授予。</item>
/// <item>任何远程请求不得修改本地配置、无人值守策略或任何白名单。</item>
/// </list>
/// 审计条目绝不包含 PIN / sharedSecret / HMAC / 证书私钥。
/// </summary>
public sealed class RemoteRequestHandler
{
    private readonly PairingService _pairing;
    private readonly RemoteAuthenticator _authenticator;
    private readonly ISchedulerEngine _engine;
    private readonly ITaskService _taskService;
    private readonly IClock _clock;
    private readonly IRemoteAuditLog _auditLog;
    private readonly Func<string> _serverNameProvider;

    public RemoteRequestHandler(
        PairingService pairing,
        RemoteAuthenticator authenticator,
        ISchedulerEngine engine,
        ITaskService taskService,
        IClock clock,
        IRemoteAuditLog auditLog,
        Func<string> serverNameProvider)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(taskService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(serverNameProvider);

        _pairing = pairing;
        _authenticator = authenticator;
        _engine = engine;
        _taskService = taskService;
        _clock = clock;
        _auditLog = auditLog;
        _serverNameProvider = serverNameProvider;
    }

    public async Task<RemoteResponse> HandleAsync(
        RemoteRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 协议版本门（对所有方法，含 pair）：未知版本一律拒绝。
        if (context.Payload.Version != RemoteProtocol.ProtocolVersion)
        {
            return Error(context, RemoteErrorCode.UnsupportedVersion, "Unsupported protocol version.");
        }

        if (context.Payload.Method == RemoteProtocol.MethodPair)
        {
            return await HandlePairAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // 其余方法必须先通过 HMAC 鉴权。
        var auth = await _authenticator.VerifyAsync(
            context.Envelope.Payload,
            context.Envelope.Hmac,
            context.Payload,
            cancellationToken).ConfigureAwait(false);

        if (auth.Status != RemoteAuthStatus.Succeeded)
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = _clock.UtcNow,
                DeviceId = context.Payload.DeviceId,
                SourceIp = context.SourceIp,
                Method = context.Payload.Method,
                Outcome = RemoteAuditOutcome.Unauthorized,
                Message = "Request rejected: " + (auth.Reason ?? "authentication failed.")
            });
            return Error(context, RemoteErrorCode.Unauthorized, "Authentication failed.");
        }

        return context.Payload.Method switch
        {
            RemoteProtocol.MethodQueryStatus => HandleQueryStatus(context),
            RemoteProtocol.MethodListTasks => HandleListTasks(context),
            RemoteProtocol.MethodTriggerShutdown => await HandleTriggerShutdownAsync(context, cancellationToken).ConfigureAwait(false),
            RemoteProtocol.MethodCancelShutdown => await HandleCancelShutdownAsync(context, cancellationToken).ConfigureAwait(false),
            _ => HandleUnknownMethod(context)
        };
    }

    // ===== pair =====

    private async Task<RemoteResponse> HandlePairAsync(
        RemoteRequestContext context,
        CancellationToken cancellationToken)
    {
        // 配对必须 TLS：sharedSecret 只在 TLS 连接上交付。
        if (!context.IsTls)
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = _clock.UtcNow,
                SourceIp = context.SourceIp,
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.TlsBlocked,
                Message = "Pairing rejected: TLS is required."
            });
            return Error(context, RemoteErrorCode.TlsRequired, "Pairing requires a TLS connection.");
        }

        var pairParams = ParseParams<RemotePairParams>(context.Payload);
        if (pairParams is null)
        {
            return Error(context, RemoteErrorCode.InvalidParams, "pair requires deviceName and pin.");
        }

        var result = await _pairing.AttemptPairAsync(
            pairParams.DeviceName,
            pairParams.Pin,
            context.SourceIp,
            cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case PairingStatus.Succeeded:
                return Ok(context, new RemotePairResult
                {
                    DeviceId = result.DeviceId ?? string.Empty,
                    SharedSecret = result.SharedSecret ?? string.Empty,
                    ServerName = _serverNameProvider()
                });
            case PairingStatus.Locked:
                return Error(context, RemoteErrorCode.PairingLocked, result.Message);
            case PairingStatus.PinRejected:
                return Error(context, RemoteErrorCode.PairingFailed, result.Message);
            default:
                return Error(context, RemoteErrorCode.PairingFailed, result.Message);
        }
    }

    // ===== 只读方法 =====

    private RemoteResponse HandleQueryStatus(RemoteRequestContext context)
    {
        if (!context.WhiteList.QueryStatus)
        {
            return Forbidden(context, RemoteProtocol.MethodQueryStatus, context.Payload.DeviceId);
        }

        var snapshot = _engine.GetSnapshot();
        var active = snapshot.Instances.Values.Count(instance =>
            instance.State is TaskInstanceState.Waiting
                or TaskInstanceState.Confirming
                or TaskInstanceState.Running
                or TaskInstanceState.Executing);
        var countdown = snapshot.Instances.Values.Count(instance =>
            instance.State == TaskInstanceState.Confirming);

        Audit(context, RemoteAuditOutcome.Succeeded, "queryStatus served.");

        return Ok(context, new RemoteStatusResult
        {
            ServerName = _serverNameProvider(),
            EngineStatus = snapshot.EngineStatus.ToString(),
            ActiveCount = active,
            PendingCountdownCount = countdown,
            FaultMessage = snapshot.FaultMessage
        });
    }

    private RemoteResponse HandleListTasks(RemoteRequestContext context)
    {
        if (!context.WhiteList.ListTasks)
        {
            return Forbidden(context, RemoteProtocol.MethodListTasks, context.Payload.DeviceId);
        }

        var snapshot = _engine.GetSnapshot();
        var tasks = _taskService.GetAll()
            .OrderBy(definition => definition.CreatedAt)
            .Select(definition =>
            {
                snapshot.Instances.TryGetValue(definition.Id, out var instance);
                return new RemoteTaskInfo
                {
                    TaskId = definition.Id.ToString("D"),
                    Kind = definition.Kind.ToString(),
                    Action = definition.Action.ToString(),
                    Enabled = definition.IsEnabled,
                    State = instance?.State.ToString(),
                    ScheduledFireTimeUtc = instance?.ScheduledFireTime
                };
            })
            .ToList();

        Audit(context, RemoteAuditOutcome.Succeeded, "listTasks served.");

        return Ok(context, new RemoteListTasksResult { Tasks = tasks });
    }

    // ===== 受控触发 / 取消 =====

    private async Task<RemoteResponse> HandleTriggerShutdownAsync(
        RemoteRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!context.WhiteList.TriggerShutdown)
        {
            return Forbidden(context, RemoteProtocol.MethodTriggerShutdown, context.Payload.DeviceId);
        }

        var taskParams = ParseParams<RemoteTaskParams>(context.Payload);
        if (taskParams is null || !Guid.TryParse(taskParams.TaskId, out var taskId))
        {
            return Error(context, RemoteErrorCode.InvalidParams, "triggerShutdown requires a valid taskId.");
        }

        // 只读前置检查（映射精确错误码），随后仍由引擎做防纵深再校验。
        var definition = _taskService.Get(taskId);
        if (definition is null)
        {
            return Error(context, RemoteErrorCode.TaskNotFound, "The local task does not exist.");
        }

        if (!definition.IsEnabled)
        {
            return Error(context, RemoteErrorCode.TaskDisabled, "The local task is disabled.");
        }

        // 无人值守等效确认 = TLS + 白名单(已过) + 任务 UseUnattended。无等效的无人值守
        // 远程触发 fail-closed：不得以本地倒计时绕过无人值守策略。
        var equivalent = context.IsTls && definition.UseUnattended;
        if (definition.UseUnattended && !equivalent)
        {
            Audit(context, RemoteAuditOutcome.Rejected,
                "triggerShutdown rejected: unattended task requires a TLS connection.");
            return Error(context, RemoteErrorCode.ExecutionRejected,
                "Unattended remote triggers require a TLS connection.");
        }

        var engineResult = await _engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, equivalent),
            cancellationToken).ConfigureAwait(false);

        switch (engineResult.Status)
        {
            case SchedulerCommandStatus.Success:
                var mode = CountdownFallbackMode(engineResult.Snapshot, taskId)
                    ? "countdown"
                    : "equivalent";
                Audit(context, RemoteAuditOutcome.Succeeded,
                    "triggerShutdown routed through the local scheduler engine (mode=" + mode + ").");
                return Ok(context, new RemoteTriggerResult
                {
                    Mode = mode,
                    Message = engineResult.Message
                });
            case SchedulerCommandStatus.NoCurrentTask:
                return Error(context, RemoteErrorCode.TaskNotFound, engineResult.Message);
            case SchedulerCommandStatus.ActiveTaskExists:
                return Error(context, RemoteErrorCode.ExecutionRejected, engineResult.Message);
            case SchedulerCommandStatus.NotRunning:
            case SchedulerCommandStatus.Faulted:
                return Error(context, RemoteErrorCode.ServerNotReady, engineResult.Message);
            default:
                Audit(context, RemoteAuditOutcome.Rejected,
                    "triggerShutdown rejected by the local scheduler engine: " + engineResult.Message);
                return Error(context, RemoteErrorCode.ExecutionRejected, engineResult.Message);
        }
    }

    private async Task<RemoteResponse> HandleCancelShutdownAsync(
        RemoteRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!context.WhiteList.CancelShutdown)
        {
            return Forbidden(context, RemoteProtocol.MethodCancelShutdown, context.Payload.DeviceId);
        }

        var taskParams = ParseParams<RemoteTaskParams>(context.Payload);
        if (taskParams is null || !Guid.TryParse(taskParams.TaskId, out var taskId))
        {
            return Error(context, RemoteErrorCode.InvalidParams, "cancelShutdown requires a valid taskId.");
        }

        var engineResult = await _engine.SubmitAsync(
            new RemoteCancelTaskCommand(taskId),
            cancellationToken).ConfigureAwait(false);

        switch (engineResult.Status)
        {
            case SchedulerCommandStatus.Success:
                Audit(context, RemoteAuditOutcome.Succeeded, "cancelShutdown applied to the pending countdown.");
                return Ok(context, new RemoteActionResult
                {
                    Ok = true,
                    Message = engineResult.Message
                });
            case SchedulerCommandStatus.NoCurrentTask:
                return Error(context, RemoteErrorCode.TaskNotFound, engineResult.Message);
            case SchedulerCommandStatus.NotRunning:
            case SchedulerCommandStatus.Faulted:
                return Error(context, RemoteErrorCode.ServerNotReady, engineResult.Message);
            default:
                Audit(context, RemoteAuditOutcome.Rejected,
                    "cancelShutdown rejected: " + engineResult.Message);
                return Error(context, RemoteErrorCode.ExecutionRejected, engineResult.Message);
        }
    }

    private RemoteResponse HandleUnknownMethod(RemoteRequestContext context)
    {
        Audit(context, RemoteAuditOutcome.Invalid,
            "Unknown remote method '" + context.Payload.Method + "'.");
        return Error(context, RemoteErrorCode.MethodNotFound, "Unknown remote method.");
    }

    // ===== 工具 =====

    private static bool CountdownFallbackMode(SchedulerSnapshot snapshot, Guid taskId)
    {
        // 成功回退本地倒计时：实例仍为 Confirming 且 fire 在未来；等效确认路径在命令返回前
        // 已同步执行完毕（Executed / 周期任务已改期 Waiting），实例不再是未来倒计时。
        return snapshot.Instances.TryGetValue(taskId, out var instance)
            && instance.State == TaskInstanceState.Confirming
            && instance.ScheduledFireTime > instance.CreatedAt;
    }

    private static T? ParseParams<T>(RemotePayload payload)
    {
        if (payload.Params.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        try
        {
            return payload.Params.Deserialize<T>();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private void Audit(RemoteRequestContext context, RemoteAuditOutcome outcome, string message)
    {
        _auditLog.Write(new RemoteAuditEntry
        {
            TimestampUtc = _clock.UtcNow,
            DeviceId = context.Payload.DeviceId,
            SourceIp = context.SourceIp,
            Method = context.Payload.Method,
            Outcome = outcome,
            Message = message
        });
    }

    private RemoteResponse Forbidden(RemoteRequestContext context, string method, string deviceId)
    {
        Audit(context, RemoteAuditOutcome.Forbidden,
            "Method '" + method + "' is not authorized by the remote whitelist.");
        return Error(context, RemoteErrorCode.Forbidden,
            "The remote whitelist does not authorize method '" + method + "'.");
    }

    private static RemoteResponse Ok(RemoteRequestContext context, object result) => new()
    {
        JsonRpc = RemoteProtocol.JsonRpcVersion,
        Id = context.Envelope.Id,
        Result = JsonSerializer.SerializeToElement(result),
        Error = null
    };

    private static RemoteResponse Error(RemoteRequestContext context, RemoteErrorCode code, string message) => new()
    {
        JsonRpc = RemoteProtocol.JsonRpcVersion,
        Id = context.Envelope.Id,
        Result = null,
        Error = new RemoteError
        {
            Code = (int)code,
            Message = message
        }
    };
}
