# AutoShutdown S7 工作单：SchedulerEngine 单循环、触发与安全恢复

## 范围

工作目录：`D:\电脑定时关机重建完整版`

本轮实现单进程内唯一 SchedulerEngine：单一命令队列、唯一运行状态、到点触发、StageToken旧回调丢弃、runtime.json持久化和启动恢复。

不得进入 S8：不实现 ShutdownWorkflow、安全检查清单、保存Office、关闭进程或真实/模拟电源调用。S7只把“已安全进入Executing的实例”交给抽象的到期处理器，测试使用Fake。

开始前列出文件、接口、命令类型、测试清单和范围确认，然后直接实现。

## 不可违反的架构约束

- SchedulerEngine是运行期间唯一状态权威、唯一 `RuntimeState` 写入者。
- UI/未来Named Pipe/其他服务只能提交命令，不得获得可写状态对象。
- 只有一个 Channel、一个Reader、一个循环；不得为每个任务创建线程或Timer。
- 内存状态不得与runtime.json并列为两个权威；运行期间以内存为准，文件只是持久化镜像。
- 进入Executing前必须在同一个循环中检查StageToken和HasExecuted，立即置HasExecuted=true并先成功持久化，之后才能调用到期处理器。
- 恢复时绝不补执行。

## 禁止事项

- 不修改S1–S6现有公开语义。
- 不引入claim、lease、Revision、CAS、Mutex或跨进程协调。
- 不调用IPowerService，不创建Win32PowerService，不使用P/Invoke/shutdown.exe/Process.Start。
- Core不引用WPF。
- 不使用系统当前时间或系统本地时区；通过IClock。
- 不在测试中使用真实等待或用户目录。
- 不增加NuGet包。
- 不运行dotnet build/test，由GPT验收。
- 不进入S8。

## 必须新增的抽象

### IAsyncDeadline

```csharp
Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken);
```

生产实现 `SystemAsyncDeadline` 可以使用 `Task.Delay`，但必须：

- 通过注入的IClock读取UtcNow；
- deadline<=now时立即完成；
- 支持取消；
- 单次等待不得超过 `TimeSpan.FromMilliseconds(int.MaxValue)`，超长等待分段重新计算。

### IScheduledTaskHandler

```csharp
Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken);
```

S7不提供业务实现。App组合根注册一个 `NullScheduledTaskHandler`，它只安全返回，不调用电源；S8将替换。

### ISchedulerEngine

至少提供：

- `Task RunAsync(CancellationToken)`：一个实例只能运行一次。
- `Task<SchedulerCommandResult> SubmitAsync(SchedulerCommand command, CancellationToken)`。
- `SchedulerSnapshot GetSnapshot()`：返回不可变快照，不泄漏可写对象。

## 命令模型

新增封闭命令类型：

- `CreateTaskCommand(TaskDefinition Definition)`
- `SnoozeTaskCommand(Guid ExpectedInstanceId, Guid ExpectedStageToken, TimeSpan Duration)`
- `CancelTaskCommand(Guid ExpectedInstanceId, Guid ExpectedStageToken)`
- `ClearTerminalTaskCommand(Guid ExpectedInstanceId)`

命令不得携带now；循环处理命令时从IClock读取一次当前UTC时间。

`Snooze/Cancel`必须同时匹配当前InstanceId和StageToken，否则以 `StaleCommand` 拒绝，且不调用TaskService、不写文件。

## 引擎与命令结果状态

新增 `SchedulerEngineStatus`：Unknown、Created、Running、Faulted、Stopped。

新增 `SchedulerCommandStatus` 至少包含：Unknown、Success、NotRunning、Faulted、NoCurrentTask、ActiveTaskExists、StaleCommand、TaskServiceRejected、PersistenceFailed、InvalidCommand、TransitionRejected。

命令结果包含：Status、TaskCommandStatus?、TaskTransitionDecisionCode?、不可变Snapshot、非空Message。失败不得返回部分更新状态。

Snapshot至少包含EngineStatus、可空CurrentInstance、LastUpdatedAt、可空FaultMessage。返回record快照；不得返回内部可变集合。

## 初始化和恢复

`RunAsync`进入循环前只读取一次 `runtime.json`：

1. NotFound：创建SchemaVersion=1、CurrentInstance=null、LastUpdatedAt=clock.UtcNow的内存状态；无需立即写文件。
2. Corrupt/IoFailure/Unknown：EngineStatus=Faulted，不处理命令、不触发，不用默认Idle继续。
3. Success但RuntimeState为null、SchemaVersion!=1、LastUpdatedAt无效，或实例关键身份字段为空GUID：Faulted。
4. CurrentInstance为Warning或Executing：通过状态机使用RecoveryInterrupted转为Interrupted；Executing保持HasExecuted=true，Warning保持原HasExecuted值；刷新LastUpdatedAt并持久化。持久化失败则Faulted。
5. CurrentInstance为Scheduled且ScheduledFireTime<=clock.UtcNow：EngineStatus=Faulted，保留原持久化实例并记录“恢复期发现已错过任务”；绝不补执行、不得伪造状态转换。S4白名单没有Scheduled->Interrupted，本阶段不得修改冻结的S4状态机。后续由明确的人工恢复流程清除或重新安排。
6. Scheduled未来时间：恢复等待，但不调用TaskService重新计算。
7. Idle/Cancelled/Completed/Failed/Interrupted：原样恢复，不自动清除。
8. 恢复过程不得调用IScheduledTaskHandler。

## 命令处理

所有命令只能在Channel单Reader循环内处理。

### Create

- 只有CurrentInstance为null或Idle时允许；Scheduled/Warning/Executing以及未清理终态均返回ActiveTaskExists。
- 调用TaskService.Create(definition, clock.UtcNow, clock.LocalTimeZone)。
- 成功后构造新的RuntimeState并先写runtime.json；写成功才提交内存状态并返回Success。
- 写失败：保持原内存状态不变，Engine进入Faulted，返回PersistenceFailed。

### Snooze/Cancel

- 必须存在CurrentInstance；身份和Token先匹配。
- 调用S6 TaskService。
- 成功后先持久化候选RuntimeState，再替换内存状态。
- 失败保持内存状态不变。

### ClearTerminal

- 仅Cancelled/Completed/Failed/Interrupted允许，调用状态机 `终态 -> Idle / ClearTerminalState`。
- 成功后保留实例身份和审计字段，仅State=Idle、WarningStartTime=null、StageToken刷新。
- StageToken必须通过IIdentifierGenerator生成，非空且区别于旧Token/InstanceId。
- 先持久化再提交。
- Scheduled/Warning/Executing拒绝。

## 到点处理

循环每轮根据当前快照计算最近deadline：

- Scheduled且WarningStartTime存在并晚于now：等待WarningStartTime。
- Scheduled且WarningStartTime<=now且ScheduledFireTime>now：立即尝试Scheduled->Warning/WarningDue，持久化后继续。
- Scheduled且ScheduledFireTime<=now：尝试Scheduled->Executing/ExecuteDue。
- Warning且ScheduledFireTime<=now：尝试Warning->Executing/ExecuteDue。
- 其他状态无deadline，只等待命令。

每次创建等待时捕获 `InstanceId + StageToken + 预期State + deadline`。等待返回后重新比较；任一不匹配即静默丢弃旧回调，不写状态、不调用Handler。

### 进入Warning

- 状态机允许后构造候选状态，先持久化，再提交内存；不调用Handler。

### 进入Executing

- 再次检查HasExecuted=false。
- 状态机允许后，在同一个循环中构造 State=Executing、HasExecuted=true、LastUpdatedAt=now。
- 先持久化成功，再提交内存状态，再调用 `IScheduledTaskHandler.HandleDueAsync`。
- Handler异常不得回退HasExecuted、不得自动重试；将Engine置Faulted并保存FaultMessage。S8以后负责完成/失败状态。
- 同一InstanceId+StageToken最多调用Handler一次。

## 等待与命令竞争

- 循环用 `Task.WhenAny(Channel等待, deadline等待)`，不得轮询或Sleep。
- 命令到达时取消当前deadline等待并优先处理命令；旧等待即使稍后完成也必须因捕获Token不匹配或取消而无效。
- Channel必须SingleReader=true；Writer可多线程。
- 每条命令用RunContinuationsAsynchronously的TaskCompletionSource返回结果。
- SubmitAsync在未运行、已停止或Faulted时必须快速返回，不得永久等待。
- RunAsync取消后完成所有待处理命令为NotRunning，状态变Stopped；不得吞掉OperationCanceledException导致僵尸循环。

## 持久化

- 固定路径 `runtime.json`。
- 每次接受的状态变化都调用IStorage.WriteAsync。
- 写失败不提交候选内存状态，并进入Faulted。
- LastUpdatedAt每次变化使用clock.UtcNow并规范化UTC。
- 不写config.json/tasks.json。

## DI

App组合根新增Singleton：SystemClock、SystemAsyncDeadline、NullScheduledTaskHandler、ISchedulerEngine。

若IClock尚无生产实现，新增 `SystemClock`，UtcNow返回UTC，LocalTimeZone返回TimeZoneInfo.Local；只有这个基础设施实现可以读取系统时间/本地时区，Core其他业务不得直接读取。

只注册，不自动启动；App生命周期启动留到S10。

## 必须测试（不得真实等待）

使用FakeClock、ControllableDeadline、InMemoryStorage、FakeHandler：

1. NotFound初始化Running且无任务。
2. Corrupt/IoFailure/非法RuntimeState进入Faulted，不处理命令。
3. 恢复Warning/Executing转Interrupted并持久化，Handler 0次；Scheduled已过期进入Faulted且Handler 0次。
4. 恢复未来Scheduled并建立等待。
5. Create成功；活动任务存在拒绝；持久化失败内存不变且Faulted。
6. Snooze/Cancel身份或Token错误返回StaleCommand且零写入。
7. Snooze刷新Token后旧deadline完成不改变状态。
8. Cancel后旧deadline完成不触发Handler。
9. Scheduled到Warning持久化一次。
10. Scheduled无提醒直接到Executing，持久化后Handler恰好1次。
11. Warning到Executing，Handler恰好1次。
12. 重复释放同一deadline、重复唤醒或并发命令不会二次Handler。
13. Executing前持久化失败：Handler 0次，HasExecuted仍false，Engine Faulted。
14. Handler抛异常：HasExecuted保持true、不重试、Engine Faulted。
15. 命令与deadline同时完成时，取消/延迟命令优先且不误触发（用可控屏障确定顺序，不依赖概率）。
16. ClearTerminal合法/非法、Token刷新和持久化失败。
17. 所有命令在循环线程串行处理；两个并发Create最多一个成功。
18. SubmitAsync在未运行/Faulted/Stopped快速返回。
19. 取消RunAsync后待处理命令全部完成，不悬挂。
20. Snapshot不可修改内部状态，相同快照读取稳定。
21. 全测试设置短超时，任何等待失败必须在2秒内结束，禁止测试挂死。

现有146项测试必须全部保留通过。

## 文件范围

允许新增Core中的Scheduling/Abstractions相关引擎、命令、结果、SystemAsyncDeadline/NullHandler文件，及一个SchedulerEngineTests文件；允许在App基础设施中新增SystemClock；允许修改App DI。

不得修改存储、电源、配置、S5计算器或S6 TaskService实现。若认为必须修改，停止并报告。

## 最终回复

报告新增/修改文件、恢复规则、单循环和一次触发保证、测试场景数量、是否触及禁止范围、未完成项。明确注明未运行build/test，等待GPT验收。完成后停止，不进入S8。
