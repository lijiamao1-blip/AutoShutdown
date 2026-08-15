# AutoShutdown S5 工作单：下一次执行时间纯函数

## 范围

工作目录：`D:\电脑定时关机重建完整版`

本轮只实现根据 `TaskDefinition + 当前时刻 + 时区` 计算下一次执行时间的纯逻辑服务及测试。

不得进入 S6：不创建、修改、延迟或取消任务；不生成 StageToken；不启动 Timer、Task.Delay、线程、Channel或调度循环；不读写文件；不调用电源服务。

开始前列出新增/修改文件、测试清单和范围确认，然后直接实现。

## 禁止事项

- 不改变现有 `TaskDefinition`、`TaskKind`、`TaskState`、状态机和配置/存储接口的公开语义。
- 不引入 claim、lease、Revision、CAS、Mutex或跨进程机制。
- 不使用 `DateTime.Now`、`DateTime.UtcNow`、`DateTimeOffset.Now`、`DateTimeOffset.UtcNow`。
- 计算器不得直接依赖 `IClock`；当前时间必须由参数注入，确保纯函数可重复测试。
- 不使用系统本地时区作为隐式默认值；时区必须由参数明确传入。
- 不增加 NuGet 包。
- 不运行 `dotnet build/test`，由 GPT 统一验收。
- 不进入 S6。

## 必须新增

### 1. 接口

在 `AutoShutdown.Core.Abstractions` 新增 `INextExecutionCalculator`：

```csharp
NextExecutionResult Calculate(
    TaskDefinition definition,
    DateTimeOffset now,
    TimeZoneInfo timeZone);
```

### 2. 结构化结果

新增 `NextExecutionStatus`，零值必须为 `Unknown`，至少包含：

- `Success`
- `InvalidTaskKind`
- `MissingCountdownDuration`
- `InvalidCountdownDuration`
- `MissingTargetTimeOfDay`
- `NoFutureOccurrence`
- `InvalidLocalTime`

`NextExecutionResult` 至少包含：

- `Status`
- 可空 `ScheduledFireTime`
- 非空 `Message`
- `Succeeded` 只在 Success 时为 true

失败时 `ScheduledFireTime` 必须为 null；成功时必须为 UTC 的 `DateTimeOffset`（Offset为零）。

### 3. 三种任务语义

#### Countdown

- 必须有 `CountdownDuration`。
- 时长必须严格大于零。
- 目标时间固定为 `CreatedAt + CountdownDuration`，不得使用 `now + duration`，保证重复计算不会不断后移。
- 目标时间必须严格晚于 `now`；等于或早于 now 返回 `NoFutureOccurrence`。
- 不使用 `TargetTimeOfDay`。

#### TodayAt

- 必须有 `TargetTimeOfDay`。
- 以 `now` 在传入时区中的当地日期，组合出当地目标时间。
- 目标必须严格晚于 now；相等或已过返回 `NoFutureOccurrence`，不得自动安排到明天。
- 不使用 `CountdownDuration`。

#### DailyAt

- 必须有 `TargetTimeOfDay`。
- 先尝试传入时区的“当地今天”。
- 若今天目标严格晚于 now，则使用今天；否则使用当地明天。
- 相等时安排到明天。
- 必须正确跨月、跨年和闰日。

### 4. 时区和夏令时安全规则

- 组合出的 `DateTime` 必须为 `DateTimeKind.Unspecified`，再由传入 `TimeZoneInfo` 转换。
- 如果当地目标时间是无效时间（夏令时跳过区间），返回 `InvalidLocalTime`，不得猜测或自动提前/推迟。
- 如果当地目标时间有两个可能时刻（夏令时回拨歧义），选择对应 UTC 更晚的那个，防止提前执行。
- DailyAt 如果今天目标无效，直接返回 `InvalidLocalTime`；不得偷偷改用明天。
- 所有成功结果规范化为 UTC。

### 5. 输入判定

- `definition` 或 `timeZone` 为 null：使用标准参数异常；这属于程序员错误，不属于普通计算失败。
- `TaskKind.Unknown` 以及任何未定义枚举值返回 `InvalidTaskKind`，不抛异常。
- 与当前任务类型无关的可空字段不得影响结果。
- 不校验 PowerAction；动作合法性不属于 S5 时间计算职责。

### 6. DI

在 App 组合根新增 `INextExecutionCalculator -> NextExecutionCalculator` Singleton注册，只注册、不调用。

## 必须测试

至少覆盖：

1. Countdown正常计算为 CreatedAt+Duration。
2. Countdown重复使用不同now计算，目标不漂移。
3. Countdown缺时长。
4. Countdown时长为零和负数。
5. Countdown目标早于now和等于now。
6. TodayAt当天未来时间成功。
7. TodayAt当天已过和恰好相等均为 NoFutureOccurrence。
8. TodayAt缺目标时间。
9. DailyAt今天未来时间。
10. DailyAt今天已过安排到明天。
11. DailyAt恰好相等安排到明天。
12. DailyAt跨午夜、跨月、12月31日跨年。
13. 闰年2月28日/29日边界。
14. now自身偏移量不同但代表同一UTC瞬间时结果一致。
15. 至少使用一个非系统本地时区验证换算。
16. 构造自定义含夏令时的 TimeZoneInfo，验证无效当地时间返回 InvalidLocalTime。
17. 验证歧义当地时间选择更晚UTC时刻。
18. Unknown任务类型和未定义枚举值。
19. null definition和null timeZone抛标准参数异常。
20. 无关字段不会改变结果。
21. 成功结果 Offset为零；失败结果时间为null且Message非空。
22. 相同输入重复调用结果完全一致。

不得删除或弱化现有92项测试。

## 建议文件范围

允许新增：

- `src/AutoShutdown.Core/Abstractions/INextExecutionCalculator.cs`
- `src/AutoShutdown.Core/Scheduling/NextExecutionResult.cs`
- `src/AutoShutdown.Core/Scheduling/NextExecutionCalculator.cs`
- `tests/AutoShutdown.Tests/NextExecutionCalculatorTests.cs`

允许修改：

- `src/AutoShutdown.App/AppHost/ServiceRegistration.cs`，仅新增 Singleton注册。

若必须超出这些文件，停止并报告，不自行扩大。

## 最终回复

只报告新增/修改文件、覆盖的计算场景和测试用例数量、是否触及禁止范围、未完成项。明确写明“未运行构建和测试，等待GPT验收”。完成后停止，不进入S6。
