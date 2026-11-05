# AutoShutdown S6 工作单：TaskService 与 StageToken

## 范围

工作目录：`D:\电脑定时关机重建完整版`

本轮只实现任务命令的纯逻辑服务：创建执行实例、延迟、取消、每日重新排程，并确保每次阶段失效操作刷新 `StageToken`。

不得进入 S7：不实现 SchedulerEngine、Channel、队列、Timer、Task.Delay、后台线程、触发回调、恢复流程或运行状态文件写入。

## 核心架构约束

- SchedulerEngine 将在 S7 成为唯一运行状态权威和唯一写入者。
- 因此 S6 的 `TaskService` 不得持有“当前任务”，不得使用可变集合或全局状态。
- 每个方法必须接收当前输入并返回一个新的不可变 `TaskInstance`；不得原地修改传入对象。
- S7 以后负责决定是否接受并保存返回结果。

开始前列出新增/修改文件、公开方法和测试清单，然后直接实现。

## 禁止事项

- 不修改现有 `TaskDefinition`、`TaskInstance`、状态枚举、状态机、时间计算器、配置或存储接口的公开语义。
- 不使用 claim、lease、Revision、CAS、Mutex、跨进程锁。
- 不使用系统当前时间；所有当前时间通过参数传入。
- 不直接使用 `Guid.NewGuid()`；GUID必须通过可注入的生成器产生，便于测试。
- 不读写文件，不调用 IStorage，不调用 IPowerService。
- 不增加 NuGet 包。
- 不运行 dotnet build/test，由 GPT 验收。
- 不进入 S7。

## 必须新增

### 1. GUID生成抽象

在 Abstractions 新增 `IIdentifierGenerator`：

```csharp
Guid NewId();
```

在 Core 中新增无状态生产实现 `GuidIdentifierGenerator`，其唯一职责是返回新GUID。测试使用确定性替身。

### 2. TaskService接口

新增 `ITaskService`，至少提供同步纯逻辑方法：

```csharp
TaskCommandResult Create(
    TaskDefinition definition,
    DateTimeOffset now,
    TimeZoneInfo timeZone);

TaskCommandResult Snooze(
    TaskInstance current,
    TimeSpan duration,
    DateTimeOffset now);

TaskCommandResult Cancel(TaskInstance current);

TaskCommandResult RescheduleDaily(
    TaskDefinition definition,
    TaskInstance current,
    DateTimeOffset now,
    TimeZoneInfo timeZone);
```

实现构造函数注入：

- `INextExecutionCalculator`
- `ITaskStateMachine`
- `IIdentifierGenerator`

### 3. 结构化结果

新增 `TaskCommandStatus`，零值必须为 Unknown，至少包括：

- `Success`
- `InvalidDefinition`
- `ScheduleCalculationFailed`
- `InvalidCurrentInstance`
- `InvalidDuration`
- `TransitionRejected`
- `AlreadyExecuted`

`TaskCommandResult` 至少包含：

- `Status`
- 可空 `Instance`
- 可空 `ScheduleStatus`（保留 S5 失败原因）
- 可空 `TransitionDecisionCode`（保留 S4 拒绝原因）
- 非空 `Message`
- `Succeeded` 仅 Success 时为 true

失败时 `Instance` 必须为 null；不得返回部分修改实例。

## 方法语义

### Create

验证：

- definition非null，timeZone非null；null为标准参数异常。
- `definition.Id`不得为空GUID。
- `definition.Action`必须是 Shutdown/Restart/Sleep/Hibernate，Unknown或未定义值拒绝。
- `WarningSeconds`若存在，必须在0–86400。
- 使用 `INextExecutionCalculator` 计算时间；失败映射为 `ScheduleCalculationFailed` 并保留 `ScheduleStatus`。
- 使用状态机验证 `Idle -> Scheduled / Schedule`；拒绝时保留状态机拒绝码。

成功创建新的 TaskInstance：

- `InstanceId` = 生成器新GUID，必须非空。
- `SourceTaskId` = definition.Id。
- `ActionSnapshot` = definition.Action。
- `State` = Scheduled。
- `ScheduledFireTime` = S5结果。
- `WarningStartTime`：WarningSeconds缺失或为0时为null；大于0时为 `ScheduledFireTime - WarningSeconds`，如果早于now则钳制为now；规范化为UTC。
- `StageToken` = 生成器另一个新GUID，必须非空且不得等于 InstanceId。
- `HasExecuted=false`。
- `CreatedAt` = now规范化为UTC。

如果生成器返回空GUID或 InstanceId与StageToken相同，返回 InvalidCurrentInstance，不返回实例。

### Snooze

- current非null，null为标准参数异常。
- duration必须严格大于0，且不超过7天；否则 InvalidDuration。
- current.HasExecuted为true时返回 AlreadyExecuted。
- 只允许：Warning -> Scheduled / SnoozeByUser；Scheduled -> Scheduled / Reschedule。
- 其他状态通过状态机返回 TransitionRejected，并保留拒绝码。
- 新 `ScheduledFireTime = now + duration`，规范化UTC。
- 生成全新非空 StageToken，必须不同于旧StageToken和InstanceId；否则 InvalidCurrentInstance。
- 返回 current with 新State、新时间、新StageToken。
- WarningStartTime设为null；InstanceId、SourceTaskId、ActionSnapshot、CreatedAt保持不变；HasExecuted保持false。

### Cancel

- current非null；HasExecuted=true返回 AlreadyExecuted。
- 只允许 Scheduled/Warning -> Cancelled / CancelByUser。
- 其他状态返回 TransitionRejected并保留拒绝码。
- 必须生成全新非空 StageToken，且不同于旧StageToken和InstanceId，使所有旧回调失效。
- 返回 current with State=Cancelled、StageToken=新值、WarningStartTime=null。
- 其他身份、时间和动作快照保持不变。

### RescheduleDaily

- 仅用于 `TaskKind.DailyAt`；其他类型 InvalidDefinition。
- definition.Id必须等于current.SourceTaskId，Action必须等于current.ActionSnapshot，否则 InvalidDefinition。
- current.HasExecuted必须为false；否则 AlreadyExecuted。
- 只接受处于 Idle 的清理后实例作为下一次每日排程输入；使用状态机 `Idle -> Scheduled / Schedule`。
- 使用 S5计算器按now计算下一次未来时间。
- 保持同一个 InstanceId和CreatedAt，刷新全新StageToken。
- State=Scheduled、ScheduledFireTime=新时间、HasExecuted=false。
- WarningStartTime按Create相同规则重新计算。

## StageToken不变量

- Create产生新InstanceId和新StageToken。
- Snooze、Cancel、RescheduleDaily每次成功都必须刷新StageToken。
- 任何失败不得消耗或暴露部分实例；为保证这一点，必须先完成所有无需GUID的验证，再调用生成器。
- 生成器异常不捕获为业务失败；这是基础设施错误。

## DI

App组合根新增Singleton：

- `IIdentifierGenerator -> GuidIdentifierGenerator`
- `ITaskService -> TaskService`

只注册，不启动、不调用。

## 必须测试

至少覆盖：

1. Create所有字段正确，且调用生成器恰好2次。
2. Create的两个GUID非空且不同。
3. Create的definition ID为空、动作Unknown/未定义、WarningSeconds越界。
4. Create传播每类时间计算失败状态。
5. Create验证WarningStartTime正常、0/null、早于now钳制。
6. Create状态机拒绝时无实例。
7. Snooze从Warning成功，刷新Token和时间。
8. Snooze从Scheduled成功且使用Reschedule原因。
9. Snooze零/负/超过7天。
10. Snooze从其他状态被拒绝并保留DecisionCode。
11. Snooze已执行拒绝。
12. Cancel从Scheduled和Warning成功并刷新Token。
13. Cancel从其他状态拒绝；已执行拒绝。
14. RescheduleDaily成功、刷新Token、保持InstanceId/CreatedAt。
15. RescheduleDaily错误类型、身份/动作不匹配、非Idle、已执行、计算失败。
16. 所有失败调用生成器0次（Create时间计算通过后状态机拒绝也不得调用生成器）。
17. 生成器返回空GUID、重复GUID或与旧Token/InstanceId冲突时安全失败。
18. 输入实例始终保持不变（record相等）。
19. 所有成功时间Offset为零；失败Instance=null且Message非空。
20. 相同输入配合相同确定性GUID序列得到相同结果。

不得删除或弱化现有117项测试。

## 建议文件范围

允许新增：

- `src/AutoShutdown.Core/Abstractions/IIdentifierGenerator.cs`
- `src/AutoShutdown.Core/Abstractions/ITaskService.cs`
- `src/AutoShutdown.Core/Tasks/GuidIdentifierGenerator.cs`
- `src/AutoShutdown.Core/Tasks/TaskCommandResult.cs`
- `src/AutoShutdown.Core/Tasks/TaskService.cs`
- `tests/AutoShutdown.Tests/TaskServiceTests.cs`

允许修改：

- `src/AutoShutdown.App/AppHost/ServiceRegistration.cs`，仅新增两个Singleton注册。

若需超出范围，停止并报告。

## 最终回复

只报告新增/修改文件、四个方法的实现摘要、StageToken测试数量、是否触及禁止范围和未完成项。明确写“未运行构建和测试，等待GPT验收”。完成后停止，不进入S7。
