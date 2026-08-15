# AutoShutdown S9 工作单：核心链路集成测试与安全验收

## 1. 目标与范围

工作目录：`D:\电脑定时关机重建完整版`

S9 只建立完整核心链路集成测试，不增加用户功能。测试必须运行真实的：

`SchedulerEngine → TaskService → NextExecutionCalculator → TaskStateMachine → ShutdownScheduledTaskHandler → ShutdownWorkflow`

只替换最外层依赖：`IClock`、`IAsyncDeadline`、`IStorage`、`IConfigurationService`、`IPowerService`。电源替身必须纯内存记录，绝不触碰系统。

开始前列出新增/修改文件、测试场景和边界确认，然后直接实施。完成后停止，不进入 S10。

## 2. 禁止事项

- 不修改 S1–S8 生产代码、公开接口、枚举或 DI 注册。
- 若测试暴露生产缺陷，不得为了通过而削弱断言或自行修改生产代码；停止并报告证据，交 GPT 决定。
- 不新增真实电源实现、P/Invoke、`shutdown.exe`、`Process.Start`。
- 不引入 claim、lease、Revision、CAS、Mutex、Named Pipe、跨进程机制、后台新循环或重试。
- 不实现 GUI、托盘、提醒窗口、Office 保存、进程关闭、开机自启或日志功能。
- 不新增 NuGet 包。
- 不读取或写入用户目录、正式 config.json/runtime.json；全部使用内存替身。
- 不使用 `Task.Delay` 表示业务时间流逝；通过 FakeClock 和可控 Deadline 推进。
- 不运行 `dotnet build/test`，由 GPT 验收。
- 不进入 S10。

## 3. 文件范围

只允许新增：

- `tests/AutoShutdown.Tests/CorePipelineIntegrationTests.cs`

如公共测试替身确实需要拆分，允许新增：

- `tests/AutoShutdown.Tests/IntegrationTestDoubles.cs`

不允许修改现有生产文件。原则上也不修改已有测试文件；若编译所需，停止报告。

## 4. 集成测试装配要求

每个测试创建独立 Harness，内部实例化真实核心对象：

- `TaskStateMachine`
- `NextExecutionCalculator`
- `TaskService`
- `ShutdownWorkflow`
- `ShutdownScheduledTaskHandler`
- `SchedulerEngine`

替身要求：

- FakeClock：UtcNow 可由测试推进，LocalTimeZone 明确指定，禁止读取系统时间。
- ControllableDeadline：记录 deadline；测试显式释放；支持取消；不得真实等待业务时长。
- InMemoryStorage：按现有 IStorage 语义序列化/反序列化；记录读写次数和写入历史；可注入 NotFound/Corrupt/IoFailure/指定第 N 次写失败。
- StubConfigurationService：返回显式有效/无效配置，记录加载次数。
- RecordingPowerService：记录 PowerRequest，返回指定 PowerResult；无 Win32、无静态全局状态。
- SequentialIdentifierGenerator：确定性 GUID；不得用随机结果掩盖问题。

Harness 必须提供安全停止 SchedulerEngine 的方式；所有异步测试 `[Fact(Timeout = 2000)]`。同步测试不得设置 Timeout。

## 5. 必须覆盖的核心验收场景

### A. 合法执行恰好一次

1. TestMode=true、动作获准、Countdown 合法任务。
2. Create 命令成功并持久化 Scheduled。
3. 推进 FakeClock 到触发点并释放 Deadline。
4. 断言状态依次包含 Scheduled→Executing→Completed；最终 Engine Running。
5. RecordingPowerService 恰好 1 次；Action、InstanceId、Reason 正确。
6. runtime 写入历史中 Executing+HasExecuted=true 必须早于 Completed。
7. 重复释放 Deadline、再次推进时间、等待循环一轮后仍为 1 次。

### B. 取消后零执行

1. 创建未来任务。
2. 使用当前 InstanceId+StageToken 提交 Cancel。
3. 再推进到旧触发点并重复释放旧 Deadline。
4. 最终 Cancelled；Power 0 次；不存在 Executing/Completed 写入。

### C. 延迟后旧时间零执行，新时间一次

1. 创建带 Warning 的任务并进入 Warning。
2. Snooze 成功，必须产生新 StageToken 和新 ScheduledFireTime。
3. 释放旧 Warning/旧 Fire deadline，Power 仍 0 次。
4. 推进到新 Fire，Power 恰好 1 次，最终 Completed。

### D. 配置损坏或不可用零执行

至少分别测试：ConfigurationLoadStatus.Corrupt、Invalid、IoFailure、Missing、TestMode=false、AllowedActions 不含目标动作。

任务可进入 Executing，但 Workflow 必须拒绝；Power 0 次；最终 Failed；Engine 保持 Running；不自动重试。

### E. runtime 损坏恢复零执行

InMemoryStorage 启动读 runtime.json 返回 Corrupt 或 IoFailure：Engine Faulted、Power 0 次、配置加载 0 次、拒绝后续命令。

### F. 崩溃恢复绝不补执行

分别预置：

- Warning 实例
- Executing+HasExecuted=true 实例
- 已过期 Scheduled 实例

启动新 Engine：Warning/Executing 转 Interrupted，过期 Scheduled 进入 Faulted并保留原实例；全部 Power 0 次、Workflow/配置加载 0 次。

### G. 重复与过期回调

1. 同一个 Deadline 被重复完成。
2. Snooze/Cancel 后旧 StageToken 回调返回。
3. 命令与 Deadline 同时完成，命令优先。

每个场景 Power 最多 1 次；取消/延迟旧时间为 0 次。不得用概率循环验证竞态，必须用屏障或可控替身确定顺序。

### H. 持久化失败安全性

至少验证：

- 写 Scheduled 失败：Create 返回 PersistenceFailed，Power 0 次，Engine Faulted。
- 写 Executing 失败：内存仍 Scheduled/Warning 且 HasExecuted=false，Power 0 次，Engine Faulted。
- 写 Completed 失败：保留 Executing+HasExecuted=true，Power 1 次，Engine Faulted，绝不二次调用。
- 写 Failed 失败：保留 Executing+HasExecuted=true，按拒绝位置验证 Power 0 或 1 次，Engine Faulted，不重试。

### I. PowerService 行为

- Simulated+true → Completed。
- Rejected、Failed、Unknown、Accepted 或不一致 WasSimulated → Failed。
- 普通异常 → Failed。
- 每种情况请求最多 1 次，不自动重试。

### J. 每日任务与跨午夜

使用真实 NextExecutionCalculator：

- DailyAt 今天已过，安排到明天；旧的今天时间 Power 0 次。
- 跨午夜推进到明天目标后 Power 1 次并 Completed。
- 使用显式非系统时区，证明计算不依赖机器本地时区。

## 6. 不变量断言

每个相关测试同时断言：

- SchedulerEngine 是 runtime 状态唯一写入者。
- Workflow 不直接写 Storage。
- Handler 不直接调用 IPowerService。
- PowerRequest 只能来自 ShutdownWorkflow。
- 没有任何失败路径退回 Scheduled 后自动重试。
- HasExecuted 一旦为 true，后续状态不恢复 false。
- 终态不会自行清除为 Idle。
- 所有测试对象互相隔离，无静态共享调用记录。

## 7. 建议测试数量

新增不少于 20 个独立集成用例，覆盖 A–J；可以使用 Theory 展开多个配置/PowerResult 变体，但测试名称必须能说明场景。

现有 208 项测试必须全部保留。不得删除、跳过或降低旧测试断言。

## 8. 最终汇报

汇报：

- 新增文件与测试用例数量；
- A–J 各场景对应测试；
- 合法执行、取消、延迟、损坏、重复触发、崩溃恢复的调用次数；
- 是否触及生产代码和禁止范围；
- 未完成项或暴露的生产缺陷；
- 明确注明未运行 build/test，等待 GPT 验收。

完成后停止，不进入 S10。
