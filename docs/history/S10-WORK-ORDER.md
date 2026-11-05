# AutoShutdown S10 工作单：单实例、Named Pipe、托盘与应用生命周期

## 1. 目标

工作目录：`D:\电脑定时关机重建完整版`

实现 AppHost 生命周期：同一 Windows 会话只允许一个后台主实例；第二次启动通过 Named Pipe 向主实例发送固定的“打开界面”命令后退出；主实例持有唯一 SchedulerEngine 循环；关闭窗口只隐藏到托盘；只有明确选择“退出”才停止后台服务。

S10 不制作正式控制面板。只提供一个简洁的占位窗口用于验证唤醒、隐藏和托盘生命周期，S11 将替换其内容。

开始前列出文件、接口、生命周期顺序、测试清单和边界确认，然后直接实施。完成后停止，不进入 S11。

## 2. 绝对约束

- 不修改 S1–S9 Core 业务语义、状态机、调度、配置、存储或电源工作流。
- `SchedulerEngine.RunAsync` 只能由主实例启动一次；第二实例不得解析任务、读写 runtime/config 或创建调度循环。
- Named Pipe 只传递无状态 UI 命令：`ACTIVATE`。不得传任务、配置、RuntimeState、电源命令或任意 JSON。
- 不引入 claim、lease、Revision、CAS 或分布式机制。
- `IPowerService` 继续只注册 `FakePowerService`；不得创建 Win32PowerService、P/Invoke、shutdown.exe、Process.Start 或真实电源路径。
- 不新增第三方 NuGet 包。托盘使用 Windows 自带 `System.Windows.Forms.NotifyIcon`；App 项目可启用 `<UseWindowsForms>true</UseWindowsForms>`。
- 不实现正式 UI、提醒窗口、任务编辑、开机自启、日志留存、安装器或旧版迁移。
- Core 不引用 WPF/Windows Forms/NamedPipe/Mutex。
- 不运行 dotnet build/test，不实际启动 GUI；由 GPT 验收。

## 3. 单实例设计

在 App/Infrastructure 新增 `SingleInstanceCoordinator`（可拆分小类）：

- 使用命名 Mutex，名称固定且限定当前会话，例如 `Local\AutoShutdown.Desktop.Singleton.v1`。
- `TryAcquirePrimary()` 返回明确结果：Primary / Secondary / Error，附非空 Message。
- 主实例在整个 App 生命周期持有 Mutex；Dispose 时释放。
- 捕获 `AbandonedMutexException` 时视为成功接管主实例，并记录结果信息；不得因此启动两个实例。
- 不得递归获取 Mutex，不得用轮询判断进程。
- Secondary 不等待 Mutex，不抢占，不启动 SchedulerEngine。
- 同一 coordinator 不允许重复 Acquire；重复调用安全拒绝。

## 4. Named Pipe 激活通道

新增 `ActivationPipeServer` 与 `ActivationPipeClient`，协议固定：

- Pipe 名固定，例如 `AutoShutdown.Desktop.Activation.v1`，仅当前机器。
- 唯一合法消息：一行 UTF-8 文本 `ACTIVATE`，最大 64 字节。
- Client 连接总超时不超过 1500ms；发送后等待简短 ACK（`OK`），无无限等待。
- Server 单监听循环，支持取消；每次读取有长度上限；未知、空、超长消息返回 `ERROR` 并忽略。
- Server 收到 ACTIVATE 后只调用 UI 激活抽象，不接触 SchedulerEngine、Storage、Configuration、Workflow 或 PowerService。
- 断线、格式错误、Pipe IOException 不得导致主实例退出；继续下一次监听，除非生命周期取消。
- 停止时取消监听并等待循环结束，不留下后台任务或未观察异常。

## 5. UI 激活抽象与占位窗口

在 App 内新增 `IWindowActivationService` / `WindowActivationService`：

- 所有窗口操作必须切换到 WPF Dispatcher。
- `ActivateMainWindow()`：若窗口未创建则创建；若隐藏则 Show；若最小化则恢复 Normal；最后 Activate 并尽力置前。
- 只维护一个主窗口实例。
- 用户点击窗口关闭按钮时取消关闭并 Hide，不退出应用、不停止 SchedulerEngine。
- 应用正在明确退出时允许窗口真正关闭。

占位 `MainWindow` 只包含：应用标题、“控制面板将在 S11 完成”、当前为安全测试模式的提示，以及“隐藏到托盘”按钮。不要提前写任务表单或正式样式。

## 6. 托盘

新增 `TrayIconService`，使用系统内置图标，不添加资源包：

- Tooltip：`自动关机助手（安全测试模式）`。
- 双击托盘图标 → ActivateMainWindow。
- 菜单只含：`打开控制面板`、分隔线、`退出程序`。
- “打开”只激活窗口。
- “退出”调用统一生命周期退出入口；不得直接 Environment.Exit。
- Dispose 时隐藏并释放 NotifyIcon/Menu，不能留下僵尸托盘图标。
- S10 不提供“立即关机”等危险菜单。

## 7. App 生命周期统一协调

新增 `ApplicationLifetimeCoordinator`（名称可等价），负责唯一启动/停止顺序。

### 启动

1. App.OnStartup 首先创建最小基础设施并获取 Mutex。
2. Secondary：不构建完整 DI、不启动托盘/窗口/调度；调用 ActivationPipeClient 发送 ACTIVATE；无论成功失败都退出本进程，返回信息可记录到 Debug/Trace。
3. Primary：构建 DI；设置 `Application.ShutdownMode = OnExplicitShutdown`。
4. 启动 ActivationPipeServer。
5. 启动且只启动一次 `ISchedulerEngine.RunAsync(appCancellationToken)`，保存 Task，不 fire-and-forget 丢失异常。
6. 创建 TrayIconService。
7. 激活占位主窗口。

### 运行

- 主窗口关闭只隐藏；调度和 Pipe 继续运行。
- 第二次启动的 ACTIVATE 使主窗口重新显示并激活。
- SchedulerEngine 意外结束或异常必须被观察。Faulted 状态不自动重启循环；应用仍可留在托盘供后续 UI显示错误。

### 明确退出

1. 用一次性互锁防止重复退出。
2. 标记 WindowActivationService 正在退出。
3. 禁用并 Dispose 托盘。
4. 取消应用 CancellationToken（同时停止 Pipe 和 SchedulerEngine）。
5. 等待 Pipe 监听任务和 SchedulerEngine Task 在合理时间内结束；不得 UI 线程死锁。
6. Dispose DI Provider、Mutex、CancellationTokenSource。
7. 在 Dispatcher 上调用 Application.Shutdown。

OnExit 再执行幂等清理作为兜底。所有清理操作可重复调用且不抛出未处理异常。

## 8. DI 与项目设置

允许修改：

- `AutoShutdown.App.csproj`：启用 Windows Forms，不加包。
- `App.xaml`：不得设置 StartupUri，避免 Secondary 自动创建窗口。
- `App.xaml.cs`：只做组合根启动/退出转发，不塞业务逻辑。
- `ServiceRegistration.cs`：注册 App 生命周期相关 Singleton；Core 注册保持不变，IPowerService 仍 Fake。

生命周期类不得放入 Core。

## 9. 文件范围

允许新增 App 文件：

- `MainWindow.xaml` / `MainWindow.xaml.cs`（占位）
- `AppHost/ApplicationLifetimeCoordinator.cs`
- `Infrastructure/SingleInstanceCoordinator.cs`
- `Infrastructure/ActivationPipeServer.cs`
- `Infrastructure/ActivationPipeClient.cs`
- `Infrastructure/IWindowActivationService.cs`
- `Infrastructure/WindowActivationService.cs`
- `Infrastructure/TrayIconService.cs`
- 必要的结果枚举/小型协议常量文件

允许修改：

- `AutoShutdown.App.csproj`
- `App.xaml`
- `App.xaml.cs`
- `AppHost/ServiceRegistration.cs`

允许新增测试：

- `tests/AutoShutdown.Tests/AppHostSourceContractTests.cs`
- `tools/Test-S10-Lifecycle.ps1`（只生成脚本，不运行；供 GPT 双进程验收）

不得修改 Core 生产文件。若认为必须修改，停止并报告。

## 10. 测试与可验收性

现有 Tests 项目不新增对 WPF App 的项目引用，避免破坏 Core 测试隔离。新增源码契约测试可读取项目源码验证：

1. App 默认 IPowerService 仍为 FakePowerService。
2. App 未出现 Win32PowerService、P/Invoke、shutdown.exe、Process.Start。
3. App.xaml 无 StartupUri。
4. Secondary 分支位于完整 DI、Scheduler、Tray、Window 启动之前。
5. SchedulerEngine.RunAsync 只有生命周期协调器一个调用位置。
6. Named Pipe 协议只允许 ACTIVATE，且有长度/连接超时。
7. 托盘菜单不存在关机/重启/睡眠/休眠命令。
8. Window Closing 在非退出状态下 Hide 并取消关闭。
9. 生命周期清理具有幂等互锁并 Dispose 托盘/Pipe/Mutex/Provider。

`Test-S10-Lifecycle.ps1` 供 GPT 后续执行，必须：

- 定位 Release App exe，不下载、不安装、不修改注册表。
- 启动第一个实例后确认进程存在。
- 启动第二实例后确认其在限定时间内退出，主实例仍仅一个。
- 可通过 Pipe 探针验证 ACK；不得发送任务或电源命令。
- 最终温和请求退出；若无法自动退出，只报告并由 GPT 手工结束，不使用危险广泛杀进程命令。
- 所有临时文件放项目测试临时目录并清理。

现有 237 项测试必须全部保留。新增测试不得通过源码字符串伪装业务行为；源码契约只验证 App 边界，核心业务仍由已有集成测试负责。

## 11. 最终汇报

报告：新增/修改文件、Primary/Secondary 启动顺序、Pipe 协议、窗口隐藏与激活、托盘菜单、调度唯一启动位置、退出清理、测试数量、是否触及禁止范围和未完成项。

明确注明未运行 build/test、未启动 GUI，等待 GPT 验收。完成后停止，不进入 S11。
