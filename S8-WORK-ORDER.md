# AutoShutdown S8 工作单：ShutdownWorkflow 八项安全检查与唯一电源出口

## 1. 范围

工作目录：`D:\电脑定时关机重建完整版`

本轮实现 `ShutdownWorkflow`、结构化决策结果及其 `IScheduledTaskHandler` 适配器，并补齐 SchedulerEngine 在处理器返回后的 Completed/Failed 持久化闭环。

S8 全程只使用现有 `FakePowerService`。不得创建真实电源实现，不得执行或引用任何真实关机 API。

开始前先列出准备新增/修改的文件、接口、结果类型、测试清单和范围确认，然后直接实施，不等待批准。完成后停止，不进入 S9。

## 2. 不可违反的约束

- 不修改 S1–S7 现有公开接口签名和枚举数值，尤其不得修改 `IScheduledTaskHandler`、`IPowerService`、`IConfigurationService`。
- `ShutdownWorkflow` 是 `IPowerService.ExecuteAsync` 的唯一业务调用者；全代码库其他类不得调用电源服务。
- App DI 中仍只注册 `FakePowerService`；不得新增 `Win32PowerService`、P/Invoke、`shutdown.exe`、`Process.Start` 或其他真实电源路径。
- 不引入 claim、lease、Revision、CAS、Mutex、跨进程协调、后台新循环或重试机制。
- 不实现 Office 保存、进程关闭、GUI、提醒窗口、托盘、Named Pipe、开机自启或日志系统。
- 不增加第三方 NuGet 包。
- 不运行 `dotnet build` / `dotnet test`；由 GPT 最终验收。
- Core 不引用 WPF，测试不接触用户目录、不进行真实等待。

## 3. 新增抽象与结果

### IShutdownWorkflow

放在 Core/Abstractions：

```csharp
Task<ShutdownWorkflowResult> ExecuteAsync(
    TaskInstance instance,
    CancellationToken cancellationToken);
```

### ShutdownWorkflowStatus

固定枚举值：

- Unknown = 0
- Simulated = 1
- Rejected = 2
- PowerFailed = 3

### ShutdownDecisionCode

固定枚举值：

- Unknown = 0
- Allowed = 1
- ConfigurationUnavailable = 2
- TestModeRequired = 3
- InvalidState = 4
- ExecutionFlagMissing = 5
- InvalidIdentity = 6
- InvalidAction = 7
- ActionNotAllowed = 8
- InvalidTiming = 9
- PowerServiceRejected = 10
- PowerServiceFailed = 11
- PowerServiceException = 12

### ShutdownWorkflowResult

不可变 record，至少包含：

- Status
- DecisionCode
- `PowerResult? PowerResult`
- 非空 Message
- `Succeeded`：仅 Status=Simulated 时为 true

结果中不得返回可修改集合，不得包含配置对象引用。

### ScheduledTaskHandlingException

新增一个 sealed 异常，仅供 `ShutdownScheduledTaskHandler` 把结构化工作流失败传回 SchedulerEngine。至少包含只读 `ShutdownWorkflowResult Result`。构造时 Result 不得为 null。

这不是重试信号；不得在循环外吞掉后重新执行。

## 4. 八项安全检查（顺序固定、默认拒绝）

`ShutdownWorkflow.ExecuteAsync` 必须按以下顺序执行。首项失败立即返回，不调用后续依赖；任何状态不明确都拒绝。

1. **配置可用**：调用 `IConfigurationService.LoadAsync`；仅 `Success + Config 非 null + SchemaVersion=1` 通过。其他状态或加载异常返回 ConfigurationUnavailable。
2. **测试模式**：S8 要求 `Config.TestMode == true`；false 返回 TestModeRequired。S12 才允许设计真实电源双闸门。
3. **执行状态**：`instance.State == Executing`；否则 InvalidState。
4. **执行标记**：`instance.HasExecuted == true`；否则 ExecutionFlagMissing。
5. **身份完整**：InstanceId、SourceTaskId、StageToken 均非空 GUID；否则 InvalidIdentity。
6. **动作有效**：ActionSnapshot 只能是 Shutdown/Restart/Sleep/Hibernate；Unknown 或未定义值返回 InvalidAction。
7. **动作获准**：ActionSnapshot 必须存在于配置 AllowedActions；否则 ActionNotAllowed。不得使用默认动作替代。
8. **时间有效**：CreatedAt 与 ScheduledFireTime 均非 default，均规范化可比较，且 ScheduledFireTime >= CreatedAt；否则 InvalidTiming。

八项全部通过后，构造且只构造一次 `PowerRequest`：

- Action = instance.ActionSnapshot
- InstanceId = instance.InstanceId
- Reason 为稳定非空文本，明确来自已到期任务；不得包含敏感配置内容

然后只调用一次 `IPowerService.ExecuteAsync`，不得自动重试。

## 5. PowerResult 映射

S8 生产 DI 使用 Fake，因此正常结果必须为：Outcome=Simulated、WasSimulated=true。映射为 Status=Simulated、DecisionCode=Allowed、Succeeded=true。

其他结果默认拒绝：

- Outcome=Rejected → Rejected / PowerServiceRejected
- Outcome=Failed → PowerFailed / PowerServiceFailed
- Outcome=Unknown、Accepted，或 Outcome/WasSimulated 组合不一致 → PowerFailed / PowerServiceFailed
- `IPowerService` 抛非取消异常 → PowerFailed / PowerServiceException
- 调用方取消产生的 OperationCanceledException 必须继续抛出，不得改写成业务失败

S8 不接受 `Accepted + WasSimulated=false`，因为真实电源尚未启用。

## 6. 到期处理器适配

新增 `ShutdownScheduledTaskHandler : IScheduledTaskHandler`：

- 只依赖 `IShutdownWorkflow`。
- `HandleDueAsync` 调用工作流一次。
- 结果 Succeeded=true 时正常返回。
- 结果失败时抛 `ScheduledTaskHandlingException`，携带原结果。
- 不写文件、不改 TaskInstance、不调用 IPowerService。

App DI：

- 注册 `IShutdownWorkflow -> ShutdownWorkflow` Singleton。
- 将 `IScheduledTaskHandler -> NullScheduledTaskHandler` 替换为 `ShutdownScheduledTaskHandler` Singleton。
- `IPowerService` 仍为 FakePowerService。
- 只注册，不在本阶段启动 SchedulerEngine。

## 7. SchedulerEngine 完成/失败闭环

只允许对 S7 `SchedulerEngine` 做以下必要集成修改，不改变公开接口：

1. 已按 S7 规则进入 Executing、HasExecuted=true 并持久化后，调用 Handler。
2. Handler 正常返回：用 S4 状态机执行 Executing→Completed / PowerAccepted；构造候选 RuntimeState，先成功写 runtime.json，再提交内存。引擎保持 Running。
3. 捕获 `ScheduledTaskHandlingException`：用 S4 状态机执行 Executing→Failed / PowerFailed；先持久化，再提交内存。引擎保持 Running，不重试。
4. 完成/失败状态持久化失败：不得提交候选状态；引擎进入 Faulted，保留当前 Executing+HasExecuted=true，绝不再次调用 Handler。
5. Handler 其他异常：保持 S7 原语义——引擎 Faulted、Executing+HasExecuted=true、不重试。
6. Handler 正常返回后若状态机意外拒绝：引擎 Faulted，不伪造 Completed。
7. 任一路径同一 InstanceId+StageToken 的 Handler 与 IPowerService 均最多调用一次。

不得让 Workflow 或 Handler 直接写 runtime.json；状态唯一写入者仍是 SchedulerEngine。

## 8. 文件范围

允许新增：

- `Core/Abstractions/IShutdownWorkflow.cs`
- `Core/Workflow/ShutdownWorkflowStatus.cs`
- `Core/Workflow/ShutdownDecisionCode.cs`
- `Core/Workflow/ShutdownWorkflowResult.cs`
- `Core/Workflow/ScheduledTaskHandlingException.cs`
- `Core/Workflow/ShutdownWorkflow.cs`
- `Core/Workflow/ShutdownScheduledTaskHandler.cs`
- `tests/AutoShutdown.Tests/ShutdownWorkflowTests.cs`
- `tests/AutoShutdown.Tests/ShutdownScheduledTaskHandlerTests.cs`

允许修改：

- `Core/Scheduling/SchedulerEngine.cs`（仅第 7 节闭环）
- `App/AppHost/ServiceRegistration.cs`（仅 DI 替换/新增）
- `tests/AutoShutdown.Tests/SchedulerEngineTests.cs`（仅新增闭环测试或适配 FakeHandler）

若认为必须修改其他生产文件，停止并报告，不自行扩展。

## 9. 必须测试

全部测试使用 Fake/内存替身，单项等待不得超过 2 秒：

### 工作流纯链路

1. 八项检查各自失败时返回对应 DecisionCode，IPowerService 调用 0 次（8 组）。
2. 配置 Missing/Corrupt/IoFailure/Invalid/UnsupportedVersion/null Config/加载抛异常均 ConfigurationUnavailable，电源 0 次。
3. TestMode=false 拒绝，电源 0 次。
4. 四种合法 PowerAction 各自通过，PowerRequest 参数完全匹配，每次恰好调用 1 次。
5. AllowedActions 不含目标动作时拒绝，不替换成 Shutdown。
6. 三个身份字段分别为空均拒绝。
7. CreatedAt/ScheduledFireTime 分别 default、目标早于创建时间均拒绝。
8. Fake Simulated 结果成功；Rejected/Failed/Unknown/Accepted 及不一致 WasSimulated 均安全失败。
9. PowerService 抛普通异常转 PowerServiceException，调用 1 次且不重试。
10. 取消异常向上传播，不重试。
11. 重复调用同一个 Workflow 是调用方的两个请求，各执行一次；Workflow 内部没有静态去重、全局状态或隐藏重试。

### Handler 与 Scheduler 集成

12. Handler 对成功结果正常返回；失败结果抛携带同一 Result 的受控异常。
13. 到期合法任务：Executing 持久化→FakePowerService 1 次→Completed 持久化，最终引擎 Running、状态 Completed。
14. 工作流安全拒绝：FakePowerService 0 次（若拒绝发生在调用前）→Failed 持久化，最终引擎 Running。
15. FakePowerService 返回失败：调用 1 次→Failed，绝不二次发送。
16. Completed 持久化失败：引擎 Faulted、当前仍 Executing+HasExecuted=true、调用仅 1 次。
17. Failed 持久化失败：同样 Faulted、调用次数不增加。
18. Handler 未知异常保持 S7 Faulted 语义，不转回 Scheduled、不重试。
19. 重复释放 deadline、旧 StageToken 回调、并发命令不得造成二次 Workflow/Power 调用。
20. 恢复期 Warning/Executing/错过 Scheduled 仍然 0 次 Workflow/Power 调用。
21. DI 解析的 IPowerService 是 FakePowerService，IScheduledTaskHandler 是 ShutdownScheduledTaskHandler；源码中不存在 Win32PowerService/PInvoke/shutdown.exe/Process.Start。

现有 170 项测试必须全部保留通过。新测试要使用清楚的场景名称，不得通过削弱旧断言换取全绿。

## 10. 最终汇报

报告：新增/修改文件、八项检查、唯一出口证据、成功/失败状态闭环、新增测试数量、禁止范围确认、未完成项。明确注明未运行 build/test，等待 GPT 验收。

完成后停止，不进入 S9。
