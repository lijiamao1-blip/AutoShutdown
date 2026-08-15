# AutoShutdown S11 机械执行工作单

## 0. 角色、目标与硬边界

你是机械执行助手。严格按本工作单实现，不重新设计架构，不自行增加功能，不进入 S12。

工作目录：`D:\电脑定时关机重建完整版`

S11 目标：在 S10 已验收的单实例、托盘与生命周期基础上，实现可实际操作的 WPF 基础版控制面板、关机前提醒窗口和只读状态展示。**唯一主视觉基准**为项目内 `docs/ui-reference/S11-main-visual.jpg`：横向蓝紫渐变、左侧可扩展导航、右侧双主卡片、下方状态卡片与最近活动。实现时必须尽量还原该图的布局比例、玻璃卡片层次、色彩关系和按钮层级，同时以文字可读性、WPF 稳定性和高 DPI 适配为硬约束。

本轮必须保持：

- `AutoShutdown.Core` 的所有公开接口、模型、枚举和行为不变。
- `IPowerService` 仍只注册 `FakePowerService`。
- 不新增 `Win32PowerService`、P/Invoke、`shutdown.exe`、`Process.Start` 或任何真实电源操作。
- 不新增 claim、lease、Revision、CAS、跨进程状态共享、第二个调度循环或重试机制。
- UI 只能读取 `ISchedulerEngine.GetSnapshot()`，并通过 `SubmitAsync` 提交现有命令；不得直接修改任务状态、runtime.json 或配置文件。
- 托盘菜单仍只有“打开控制面板”和“退出程序”，不得增加快速关机入口。
- 不添加第三方 NuGet UI 包，不使用 WebView，不把界面实现成网页。
- 不运行 `dotnet build`、`dotnet test`，不启动 GUI；完成后等待 GPT 验收。

## 1. 开工前必须先报告

先只读检查当前文件，然后在修改前输出：

1. 准备新增/修改的文件清单。
2. UI 可调用的现有命令清单。
3. 测试清单。
4. 明确重申禁止范围。

报告后直接实施，不等待批准。

## 2. 必须先修复的 S10 中文乱码

当前以下文件存在 mojibake，S11 必须修复：

- `src/AutoShutdown.App/MainWindow.xaml`
- `src/AutoShutdown.App/Infrastructure/TrayIconService.cs`

统一使用 UTF-8 保存，正确文字至少包括：

- `电脑自动关机助手`
- `打开控制面板`
- `退出程序`
- `电脑自动关机助手（安全测试模式）`

全体新增/修改的 XAML、C#、测试文件不得包含 `�`，也不得出现典型乱码片段（如“鑷”“鍏”“鎵”“閫€”等）。

## 3. 最终页面结构

### 3.1 窗口

- 标题：`电脑自动关机助手`
- 默认尺寸建议约 `1280×800`，最小尺寸不得低于 `1050×680`。
- `WindowStartupLocation=CenterScreen`，允许缩放。
- 主内容必须使用 `ScrollViewer` 或可响应布局，较低分辨率下不能裁掉主要按钮。
- 关闭窗口继续沿用 S10 行为：隐藏到托盘，不退出后台。
- 不实现自绘标题栏，不破坏系统最小化、最大化、缩放和高 DPI 行为。

### 3.2 顶部栏

显示：

- 应用名 `电脑自动关机助手`
- 胶囊标签 `安全测试模式`
- 说明 `当前不会执行真实系统电源操作`
- 调度服务状态（根据快照显示：启动中/运行正常/故障/已停止）
- `设置`入口（S11 可跳转到占位页）
- `托盘菜单`按钮（只展示说明或打开托盘引导，不模拟系统托盘右键）

### 3.3 左侧可折叠导航

导航顺序固定：

1. 首页
2. 任务管理
3. 高级功能
4. 网络唤醒
5. 日志与诊断
6. 软件设置
7. 关于软件

首页默认选中。导航栏必须预留折叠能力，折叠后至少保留图标或短标识和 ToolTip。

S11 只完整实现“首页”。其他入口进入统一占位页，明确显示：

- 页面名称
- `该功能将在后台能力通过测试后开放`
- 不得放置可执行的假按钮

“任务管理”允许只读展示当前任务摘要，但不得伪装成已经支持多任务。

### 3.4 首页两张主卡片

左卡片：`创建定时任务`

- 时间模式：`倒计时`、`今天指定时间`、`每天固定时间`
- 倒计时输入：小时、分钟、秒；有效范围分别为 0–168、0–59、0–59；总时长必须大于 0。
- 今天/每天模式：使用可输入或可选择的时、分、秒；不得读取系统时间作为业务时间，创建时统一使用注入的 `IClock.UtcNow` 与 `IClock.LocalTimeZone`。
- 任务类型：`关机`、`重启`、`睡眠`、`休眠`，与 `PowerAction` 一一对应，不得使用字符串猜测。
- 提前提醒：S11 提供受控选项 `不提醒`、`1分钟`、`5分钟`、`10分钟`、`30分钟`；转换成 `WarningSeconds`。
- 主按钮：`创建任务`。
- 当前存在活动任务、引擎非 Running、配置不可用、输入无效或操作正在提交时，按钮必须禁用并显示原因。

创建时构造现有 `TaskDefinition`：

- `Id` 使用 `Guid.NewGuid()`，只作为 UI 新定义标识；不得自行生成 InstanceId/StageToken。
- `Kind` 与模式对应 `Countdown/TodayAt/DailyAt`。
- `Action` 与用户选择对应。
- `CreatedAt` 使用 `IClock.UtcNow`。
- 倒计时只填写 `CountdownDuration`；指定时间只填写 `TargetTimeOfDay`。
- 通过 `ISchedulerEngine.SubmitAsync(new CreateTaskCommand(definition), ct)` 提交。

右卡片：`当前任务`

- 状态中文映射：等待执行、提醒中、正在执行、已完成、已取消、执行失败、恢复后已中断、状态未知。
- 显示只读倒计时、下次执行时间、任务类型、提前提醒时间。
- 倒计时只用于显示，由 UI 时钟刷新；不得作为真正触发器。
- `延迟10分钟`仅在 State 为 Scheduled 或 Warning 且身份字段有效时启用，提交 `SnoozeTaskCommand(Current.InstanceId, Current.StageToken, TimeSpan.FromMinutes(10))`。
- `取消任务`仅在 Scheduled 或 Warning 时启用，提交 `CancelTaskCommand(Current.InstanceId, Current.StageToken)`。
- 取消前弹出明确确认；文案为 `取消后任务将停止执行，可在任务记录中查看。`
- 终态允许提供不突出的 `清除当前记录`，仅提交现有 `ClearTerminalTaskCommand`；不得自动清除终态。
- 无任务时显示 `当前没有活动任务`，延迟和取消按钮禁用。

### 3.5 首页辅助区域

显示三张只读状态卡：

- 任务状态
- 调度服务
- 配置状态

配置状态必须通过 `IConfigurationService.LoadAsync` 获得：

- Success：显示 `安全有效` 或 `测试模式已关闭（拒绝执行）`。
- Missing/Corrupt/Invalid/IoFailure/UnsupportedVersion/MigrationUnavailable/Unknown：显示明确异常并禁用创建任务。
- 不得用 `new AppConfig()` 冒充成功，不得在 S11 自动创建或覆盖配置。

“最近活动”仅保存在 UI 进程内，最多显示最近 20 条本次会话操作结果。内容来自用户命令结果和快照状态变化，不读写日志文件，不伪造历史记录。

## 4. 视觉规范

- **主参考图固定为**：`docs/ui-reference/S11-main-visual.jpg`。不得再根据聊天中的“第几张图”自行判断，也不得切换成白色简约稿或紧凑小卡片稿。
- 窗口结构严格参考主图：顶部横向状态栏；左侧约占窗口 17%–20% 的导航栏；右侧首页上方为“创建定时任务”和“当前任务”两张等高主卡片；下方为三张状态卡片与“最近活动”；提醒窗口作为独立浮动窗口实现。
- 主色采用深蓝、湖蓝、紫色渐变；左侧导航比内容区更深；当前任务卡允许偏紫，创建任务卡允许偏蓝白；选中项使用明亮蓝色高亮。
- 玻璃质感应接近主图：半透明浅色卡片、细亮边框、柔和蓝紫阴影、适量内外层次。不得退化成纯白平面表单，也不得使用传统灰色 GroupBox 风格。
- 主图中的光效只作为质感参考。实际 WPF 不使用持续粒子、呼吸、高频动画或昂贵模糊动画；允许静态渐变、静态柔光和轻量阴影。
- 卡片内文字必须使用深色或高对比文字；倒计时可使用白色高亮。不得为了还原透明效果导致文字看不清。
- 主图存在的生成错误不得照搬：标题固定为“电脑自动关机助手”；导航固定为工作单第 3.3 节七项；“关于软件”“关机前提醒”“托盘菜单”等中文必须准确；不保留 AI 水印。
- 主图只规定视觉与布局，不是功能事实来源。所有倒计时、日期、状态、按钮启用状态必须绑定真实快照和配置结果，不得写死图片示例数据。
- 第二张紧凑效果图只允许参考低分辨率下的信息密度，不作为颜色、卡片或整体风格依据。
- 主按钮蓝色实心；普通次级按钮蓝色描边；危险按钮红色描边。
- 不直接使用图片作为界面背景，不保留任何 AI 水印。
- 所有样式集中到 `Themes/Colors.xaml`、`Themes/Controls.xaml`（或等价的两个资源字典），不要在每个控件重复大段样式。
- WPF 中不要在 `Button`、`TextBox` 上直接使用不存在的 `CornerRadius` 属性；需要圆角时使用合法模板。
- 图标优先使用简单的 Unicode/Path Geometry；不得引入第三方图标包。

## 5. ViewModel 与命令边界

不引入第三方 MVVM 包。可新增最小基础设施：

- `ObservableObject`
- `RelayCommand` / `AsyncRelayCommand`
- `MainWindowViewModel`

要求：

- ViewModel 不引用具体 `MainWindow`。
- 所有异步命令捕获可预期错误并转成用户可读状态，不允许 `async void`（WPF 事件入口除外）。
- 防重复点击：命令执行期间禁用同一命令。
- 窗口关闭与托盘激活仍由 S10 的 `IWindowActivationService` 管理。
- 为支持 DI，新增 `IMainWindowFactory`/`MainWindowFactory`（或等价的明确工厂），由 `WindowActivationService` 使用工厂创建唯一主窗口；不得在服务内部手写完整依赖链，也不得使用静态 ServiceLocator。
- App 级展示层服务注册为 Singleton 或明确工厂，不能创建第二个 SchedulerEngine。

## 6. 状态刷新

允许新增一个 App 层 `DashboardRefreshService`（名称可等价）：

- 每 500–1000ms 读取一次 `ISchedulerEngine.GetSnapshot()`，只读。
- 使用 `IClock.UtcNow` 计算显示倒计时。
- 通过 Dispatcher 更新 UI。
- 必须可取消、可 Dispose；窗口隐藏到托盘后仍可低频刷新，但应用退出时必须停止。
- 不调用配置保存，不修改任务，不创建新的业务调度循环。
- 相同快照不得重复追加“最近活动”。

也可以用 `DispatcherTimer` 实现，但必须由窗口/ViewModel 生命周期明确启动和停止，且测试中可以替换时间或直接调用单次刷新方法。

## 7. 关机前提醒窗口

新增独立 `ReminderWindow`，只在快照首次进入 `Warning` 状态时显示。

必须通过 App 层 `INotificationService` + WPF 实现（接口可位于 App，因为 Core 不依赖通知）。增加 `NotificationCoordinator` 监视只读快照：

- 去重键为 `InstanceId + StageToken + State`，同一阶段只显示一次。
- 提醒窗口已打开时不得再开第二个。
- 状态离开 Warning、任务取消、延迟、进入执行或应用退出时关闭旧提醒窗口。
- 应用恢复后若当前处于 Warning，可显示一次；不得执行或补执行任务。

提醒窗口显示：

- 标题 `关机前提醒`
- `计划将在剩余时间后执行{关机/重启/睡眠/休眠}操作。`
- 只读倒计时
- `延迟10分钟`、`取消任务`、`我知道了`

行为：

- 延迟和取消仍只提交现有 SchedulerCommand，且携带当前 InstanceId/StageToken。
- `我知道了`只关闭窗口，不改变任务。
- 不得出现“立即执行”或“立即关机”。
- 提交失败需在窗口内显示原因，不能假装成功。
- 不抢占全屏应用的系统焦点；允许普通激活但不得反复 Topmost。

## 8. 中文状态与错误映射

新增集中式映射器（名称自定），至少覆盖：

- `SchedulerEngineStatus`
- `SchedulerCommandStatus`
- `TaskCommandStatus`
- `TaskState`
- `PowerAction`
- `ConfigurationLoadStatus`

未知枚举必须显示 `未知状态` 或安全错误，不得默认为“关机”“成功”或“正常”。

## 9. 预期文件范围

允许新增/修改的生产文件限于 `src/AutoShutdown.App`，建议包括：

- 修改：`MainWindow.xaml`、`MainWindow.xaml.cs`
- 修改：`Infrastructure/WindowActivationService.cs`
- 修改：`Infrastructure/TrayIconService.cs`（仅修乱码，菜单语义不扩展）
- 修改：`AppHost/ServiceRegistration.cs`
- 新增：`Themes/Colors.xaml`、`Themes/Controls.xaml`
- 新增：`Presentation/MainWindowViewModel.cs`
- 新增：`Presentation/ObservableObject.cs`
- 新增：`Presentation/RelayCommand.cs`、`AsyncRelayCommand.cs`
- 新增：`Presentation/UiTextMapper.cs`
- 新增：`Infrastructure/IMainWindowFactory.cs`、`MainWindowFactory.cs`
- 新增：`Notifications/INotificationService.cs`
- 新增：`Notifications/WpfNotificationService.cs`
- 新增：`Notifications/NotificationCoordinator.cs`
- 新增：`ReminderWindow.xaml`、`ReminderWindow.xaml.cs`

如果需要极少量等价 App 文件可调整命名，但不得修改 `AutoShutdown.Core`。如发现必须修改 Core 才能完成，立即停止并报告，不得越权。

测试允许新增：

- `tests/AutoShutdown.Tests/S11UiSourceContractTests.cs`
- 如需要测试 App 类型，Tests 可以新增对 App 项目的引用；不得因此启动 WPF Application 或创建真实窗口。

## 10. 测试要求

至少新增以下契约/纯逻辑测试，测试不得启动 GUI，不得等待真实分钟：

1. 全 App 源码不含 mojibake 标记与 Unicode replacement character。
2. 全 src 仍无 `Win32PowerService`、`shutdown.exe`、`Process.Start`、`DllImport`。
3. `IPowerService` 仍注册为 `FakePowerService`。
4. 托盘菜单只含打开与退出，不含关机/重启/睡眠/休眠操作。
5. 导航包含 7 个规定入口，未实现页面含“后续开放”说明。
6. 首页不存在“立即执行”“立即关机”按钮。
7. 四种 PowerAction 中文映射准确，Unknown 不映射成关机。
8. 三种 TaskKind 构造字段互斥正确。
9. 倒计时 0、负数、分钟/秒越界被拒绝；合法值映射正确。
10. 引擎非 Running、配置失败、有活动任务、命令执行中时创建按钮不可用。
11. Snooze/Cancel 使用当前 InstanceId 与 StageToken。
12. 同一 Warning 去重，只请求显示一次提醒。
13. StageToken 改变后旧提醒关闭，新 Warning 可显示一次。
14. “我知道了”不提交任何 SchedulerCommand。
15. ReminderWindow 无立即执行按钮。
16. 状态刷新只调用 GetSnapshot，不直接访问 IStorage/runtime.json。
17. WindowActivationService 仍保证唯一 MainWindow。
18. 关闭主窗口仍 Hide，不退出应用。
19. App 源码中 `ISchedulerEngine.RunAsync` 仍只有 S10 生命周期协调器一个调用位置。
20. 所有新增资源字典与 XAML 均可由 WPF 编译器解析（由 GPT 后续 build 验收）。

不得删除或弱化现有测试。

## 11. 验收口径

WorkBuddy 完成后只提交：

1. 新增/修改文件清单。
2. 首页、导航、提醒窗口的实现摘要。
3. UI 到 SchedulerCommand 的映射表。
4. 配置异常与引擎故障时的禁用策略。
5. 新增测试名称和数量。
6. 明确声明未运行 build/test/GUI。
7. 禁止范围复核。
8. 尚存疑问或阻塞项。

完成后停止，不进入 S12，不生成真实电源实现。
