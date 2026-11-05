# AutoShutdown S4 工作单：任务状态机与转换白名单

## 角色和范围

你是机械实现助手。本轮只实现纯逻辑任务状态机、明确的转换原因、结构化允许/拒绝结果及单元测试。

工作目录：`D:\电脑定时关机重建完整版`

不得进入 S5，不得实现时间计算、任务服务、调度循环、持久化运行状态、WPF 页面、托盘或真实电源操作。

开始前列出准备新增/修改的文件、测试清单和范围确认，然后直接实现，不等待批准。

## 禁止事项

- 不修改 `IStorage`、`FileStorage`、`IPowerService`、`IConfigurationService` 的公开语义。
- 不修改现有 `TaskState` 枚举成员及数值。
- 不引入 claim、lease、Revision、CAS、跨进程锁、Mutex、后台线程或循环。
- 不创建真实电源实现；不得使用 P/Invoke、`shutdown.exe`、`Process.Start`。
- 不增加 NuGet 包。
- 状态机不得读写文件、不得读取系统时间、不得调用 `DateTime.Now`/`UtcNow`。
- 不在状态机中实现调度、延迟计时或业务副作用。
- 不运行 `dotnet build` 或 `dotnet test`；本轮只写文件，编译测试由 GPT 验收，避免 WorkBuddy 命令适配层再次卡住。
- 完成后停止，不进入 S5。

## 必须实现

### 1. 转换原因枚举

新增 `TaskTransitionCause`，零值必须为 `Unknown`，至少包含：

- `Schedule`
- `WarningDue`
- `ExecuteDue`
- `CancelByUser`
- `SnoozeByUser`
- `Reschedule`
- `PowerAccepted`
- `PowerFailed`
- `RecoveryInterrupted`
- `ClearTerminalState`

不得使用自由字符串决定转换是否合法。

### 2. 状态机接口与结果

新增：

- `ITaskStateMachine`
- `TaskStateMachine`
- `TaskTransitionResult`
- 稳定的拒绝码枚举，例如 `TaskTransitionDecisionCode`

推荐接口：

```csharp
TaskTransitionResult TryTransition(
    TaskState current,
    TaskState target,
    TaskTransitionCause cause);
```

结果至少包含：

- `Allowed`
- `CurrentState`
- `TargetState`
- `Cause`
- `DecisionCode`
- 可读说明 `Message`

结果本身就是后续日志系统可记录的完整审计事实。S4 不实现日志文件写入。

### 3. 唯一合法转换表

只有以下三元组合法：

| 当前状态 | 目标状态 | 原因 |
|---|---|---|
| Idle | Scheduled | Schedule |
| Scheduled | Warning | WarningDue |
| Scheduled | Executing | ExecuteDue |
| Scheduled | Cancelled | CancelByUser |
| Scheduled | Scheduled | Reschedule |
| Warning | Scheduled | SnoozeByUser |
| Warning | Cancelled | CancelByUser |
| Warning | Executing | ExecuteDue |
| Executing | Completed | PowerAccepted |
| Executing | Failed | PowerFailed |
| Executing | Interrupted | RecoveryInterrupted |
| Warning | Interrupted | RecoveryInterrupted |
| Cancelled | Idle | ClearTerminalState |
| Completed | Idle | ClearTerminalState |
| Failed | Idle | ClearTerminalState |
| Interrupted | Idle | ClearTerminalState |

任何未列出的状态、目标、原因或组合必须拒绝。即使状态对相同但原因错误，也必须拒绝。

### 4. 稳定拒绝语义

至少区分：

- `Allowed`
- `UnknownCurrentState`
- `UnknownTargetState`
- `UnknownCause`
- `TransitionNotAllowed`
- `CauseMismatch`

判定顺序必须稳定：先检查未知当前状态、未知目标状态、未知原因，再检查状态对是否存在，最后检查原因是否匹配。

如果状态对完全不存在，返回 `TransitionNotAllowed`；状态对存在但原因错误，返回 `CauseMismatch`。

### 5. 纯逻辑要求

- 实现应使用不可变静态白名单或同等清晰结构。
- 每次调用只根据三个输入得出结果。
- 不修改 `TaskInstance`，不修改全局状态。
- 不抛出异常表达普通非法转换。
- 相同输入必须得到相同结果。

### 6. DI

在 App 组合根把 `ITaskStateMachine` 注册为 Singleton。只注册，不启动、不调用。

## 必须新增的测试

1. 上表16个合法三元组逐项全部允许。
2. 每个合法状态对使用错误原因时返回 `CauseMismatch`。
3. 至少覆盖以下非法状态对：Idle→Executing、Idle→Completed、Scheduled→Completed、Warning→Completed、Executing→Scheduled、Cancelled→Scheduled、Completed→Executing、Failed→Executing、Interrupted→Executing。
4. Unknown作为当前状态返回 `UnknownCurrentState`。
5. Unknown作为目标状态返回 `UnknownTargetState`。
6. Unknown原因返回 `UnknownCause`。
7. 终态只允许 `ClearTerminalState` 回到 Idle。
8. Scheduled→Scheduled只允许 `Reschedule`。
9. Warning→Scheduled只允许 `SnoozeByUser`。
10. 重复调用同一输入得到完全一致的结果。
11. 测试结果对象包含完整输入和非空消息。

不要删除或弱化现有39项测试。

## 文件范围建议

允许新增：

- `src/AutoShutdown.Core/Abstractions/ITaskStateMachine.cs`
- `src/AutoShutdown.Core/State/TaskTransitionCause.cs`
- `src/AutoShutdown.Core/State/TaskTransitionResult.cs`
- `src/AutoShutdown.Core/State/TaskStateMachine.cs`
- `tests/AutoShutdown.Tests/TaskStateMachineTests.cs`

允许修改：

- `src/AutoShutdown.App/AppHost/ServiceRegistration.cs`，仅增加状态机 Singleton 注册。

若必须超出上述文件范围，立即停止并报告原因，不自行扩大。

## 最终回复

完成后只报告：

1. 新增/修改文件。
2. 实现的合法转换数量和测试用例数量。
3. 是否触及禁止范围。
4. 未完成项或疑问。

明确注明“未运行构建和测试，等待 GPT 验收”。不要继续 S5。
