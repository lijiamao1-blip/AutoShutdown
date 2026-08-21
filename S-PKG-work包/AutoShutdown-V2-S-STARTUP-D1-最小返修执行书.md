# AutoShutdown V2 · S-STARTUP-D1「无界面半启动实例与单实例激活失败最小返修」执行书

- **阶段编号**: S-STARTUP-D1
- **返修名称**: 无界面半启动实例与单实例激活失败最小返修
- **基线提交**: `f2175a7`（S-PKG2 结果记录）
- **起止时间**: 2026-08-21 ~ 2026-08-22
- **执行 AI**: AutoShutdown Dev（执行 AI，非总顾问）

## 0. 前置说明与强制约束

1. 本次返修由总顾问独立立项：启动生命周期存在「主实例获得单实例互斥体后启动卡死 → 半启动无界面实例长期占用所有权 → 后续双击的次实例激活转发失败」的发布阻塞缺陷。**只允许接触启动生命周期、单实例就绪与诊断能力**；不得改动业务逻辑、调度器状态机、电源流程、Office、远程控制、Windows 任务计划程序同步语义或 UI 布局。
2. **开始前只读门槛**（已核验）：
   - 分支 `master`，HEAD=`f2175a7983c31d12b3090ad9af1a366846debc2d`；
   - 暂存区为空，`git diff --check` 退出码 0；
   - **保持既有 dirty worktree 不变**：6 张已跟踪但被修改过的旧 S-UI2 截图（`S-PKG-work包/S-UI2-截图证据/*-150percent.png`）、`LibreOffice_26.2.5_Win_x86-64.msi`、所有历史工作包/诊断脚本/解压文件/旧候选。**绝不 reset/checkout/delete/overwrite/stage/commit 它们。**
3. **先诊断，不要猜测**：用证据确认实际阻塞点；添加至少 8 个检查点日志：`ServiceProviderBuilt`、`LifetimeCoordinatorResolving`、`LifetimeCoordinatorResolved`、`ActivationPipeStarting`、`ActivationPipeStarted`、`SchedulerStarting`、`MainWindowActivating`、`MainWindowActivated`。绝不把推测写成根因；若无法精确定位阻塞点，停止并提交诊断证据等待总顾问裁决（不得用重试/长等待/强杀进程掩盖缺陷）。
4. **修复要求**：
   - 不允许半启动实例无限存活：主实例获得互斥体后的关键初始化失败/超时 → 写明确错误日志 → 不执行真实电源操作 → 释放资源与互斥体 → 非零退出码退出 → 不留无窗口后台进程；
   - 不自动杀死其他实例、不使用 taskkill、不按进程名杀进程、不把测试实例请求转发给正式实例、不削弱单实例安全边界；
   - 正常主实例启动必须允许第二次双击激活现有窗口；次实例必须在有限时间内退出；记录转发失败；不创建第二个后台实例；
   - **不通过长等待掩盖问题**：消除不安全的构造阶段同步等待，或将阻塞初始化移到可取消/可超时/可记录失败的启动步骤；
   - Task Scheduler 同步默认关闭；测试中不进行真实 Windows Task Scheduler 写入。
5. **保留安全不变量**：ShutdownWorkflow 是唯一 IPowerService outlet；TestMode/RealPowerEnabled/user-confirmation 双门保持不变；调度器引擎是唯一任务仲裁路径；TaskCollection 是唯一事实来源；Windows Task Scheduler 同步保持单向（outbound）；RunCommands whitelist 默认空；CloseApps force-kill authorization 默认空；remote listener 默认关闭；unknown/corrupt state fail-closed。
6. **提交纪律**：(1) 先提交实现（只包含本阶段源码/测试/执行文档，显式 staging，不掺旧工件）；(2) 从干净实现提交发布候选到新目录 `artifacts/release/v2.0.0/S-STARTUP-D1-<shorthash>/`，绝不重用/覆盖 S-PKG2-e50afcb；(3) 候选必须显示新 version+build commit，且 version/HEAD/文件名/manifest/SHA-256 一致；(4) 单独提交 `S-STARTUP-D1-结果记录.md` + 必要且被接受的证据；(5) 结果提交后立即停止——不 git push、不 upload/distribute、不 final packaging、不进入下一阶段。
7. **Windows Task Scheduler 真实机器状态必须诚实描述**：backend implemented / UI present / automated via Fake / user real-machine unverified / 本阶段不得写真实系统任务。

## 1. 现状与问题

### 1.1 现象（用户复现证据 + 事件日志）

`%LOCALAPPDATA%\AutoShutdown\logs\autoshutdown-2026-08-21.log`：

```
23:43:00.065 ApplicationStopped 应用已退出。          ← 前一个正常实例
23:43:37.380 ApplicationStarting 应用启动。
23:43:37.402 PrimaryInstanceAcquired 本实例成为主实例。
                ── 此后该实例再未输出任何日志 ──
23:43:41.384 SecondaryInstanceDetected 检测到主实例已在运行，发送激活通知后退出。
23:43:42.463 SecondaryInstanceDetected 检测到主实例已在运行，发送激活通知后退出。
23:43:42.918 ActivationForwardFailed 主实例转发未确认。
23:43:42.921 ApplicationStopped 应用已退出。          ← 次实例 A 退出
23:43:43.989 ActivationForwardFailed 主实例转发未确认。
23:43:43.991 ApplicationStopped 应用已退出。          ← 次实例 B 退出
```

- 主实例在 `PrimaryInstanceAcquired`（互斥体获取成功）之后、`SchedulerStarting` 之前**卡死**，且从未输出 `ApplicationStopped` → **半启动无窗口后台进程长期占用单实例所有权**。
- 两次双击产生的次实例均因激活管道不可达而 `ActivationForwardFailed`，激活现有窗口失败。
- 事件日志佐证：正常实例从 `PrimaryInstanceAcquired` 到 `ApplicationStarted` 仅约 85 ms（如 23:36:14.014 → 23:36:14.103，及 23:41:52.518 → 23:41:53.976），说明该卡点不是正常耗时，而是**异常停滞**。

### 1.2 根因（确认）

- 卡点区间 = `PrimaryInstanceAcquired` 之后 → `SchedulerStarting` 之前 = **DI 服务容器构建阶段**（`App.xaml.cs` 主路径中 `services.BuildServiceProvider()`，其后才 `LifetimeCoordinatorResolving`、`_coordinator.Start()`）。
- 该阶段唯一**无界同步等待**位于 `src/AutoShutdown.App/AppHost/ServiceRegistration.cs` 的 `TaskSyncCoordinator` factory：

  ```csharp
  var load = provider.GetRequiredService<TaskSyncSettingsStore>()
      .LoadAsync(CancellationToken.None)
      .GetAwaiter().GetResult();   // ← UI 线程上无界同步文件 I/O
  ```

- 真实数据根 `C:\Users\李佳茂\AppData\Local\AutoShutdown` 存在 `task-sync.json`（72 字节，`Enabled:true`），构造阶段读取它即发生一次真实文件 I/O；FileStorage 是磁盘存储，任何文件系统停滞（磁盘/杀软/网络重定向）都会让 `.GetResult()` **无限期挂起 UI 线程**，与「互斥体已获取、管道未起、调度未起」的卡点完全吻合。此等待是该阶段唯一能把 UI 线程永久卡死的同步阻塞点。
- 由于激活管道在 `coordinator.Start()` 内（且在崩溃恢复之后）才启动，主实例卡死时管道从未监听 → 次实例转发必然失败 → 缺陷闭环。

### 1.3 排除的假设

- 不是崩溃恢复死锁：崩溃恢复本身在启动步骤内（`RecoverFromCrash`），且卡点在其之前（DI 构建阶段）；与正常启动路径对比，恢复路径在此次事件中并非入口。
- 不是调度引擎/任务加载死锁：调度引擎在 `coordinator.Start()` 内 `SchedulerStarting` 之后启动；卡点在其之前。
- 不是远程/Office/UI 布局：全部在 `SchedulerStarting` 之后，均不在卡点区间。
- 不是命名管道自身死锁：主实例卡死时管道尚未创建；次实例 `ActivationForwardFailed` 是「管道不存在」的必然结果，而非管道实现缺陷。

### 1.4 副实例 23:43:41 实例的完整性说明

23:43:41 那次 `SecondaryInstanceDetected` 之后直至 23:43:42.918/43.989 出现两次 `ActivationForwardFailed + ApplicationStopped` 对。23:43:41 与 23:43:42 两个次实例的转发失败与退出均已记录（无第三个后台实例残留）。两次失败均为同一根因：主实例管道未监听。

## 2. 返修目标（逐条对应工作单验收项）

| # | 目标 | 验证方式 |
|---|---|---|
| 1 | 协调器在启动前可解析（有界，不触碰存储） | 聚焦测试 (1)(2) |
| 2 | 模拟挂起/超时依赖 → 有限时间失败，释放互斥体，不建窗口，不执行真实 power/remote/task-sync；下一实例可成为 primary | 聚焦测试 (2)(3)(4)(6) |
| 3 | 初始化异常 → 日志含 checkpoint+exception，非零退出，无残留 | 聚焦测试 (3) + 源码契约 |
| 4 | 运行中的第二次启动 → 激活现有窗口，次实例退出，只留一个进程 | 聚焦测试 (5) + 20 轮真机循环 |
| 5 | TestMode 隔离规则不回归 | S-UI3 聚焦测试 |
| 6 | 不执行真实 IPowerService | 聚焦测试 + 源码契约 + S-UI3 |
| 7 | 不进行真实 Windows Task Scheduler 写入 | 聚焦测试 (6) + S22（Fake + 内存） |

## 3. 允许修改的文件（本次改动清单）

| 文件 | 修改内容 |
|---|---|
| `src/AutoShutdown.App/AppHost/ServiceRegistration.cs` | **根因修复**：TaskSyncCoordinator factory 移除构造期同步 `LoadAsync().GetAwaiter().GetResult()`，改为 fail-closed `coordinator.Enabled = false`（开关读取移入启动步骤） |
| `src/AutoShutdown.App/AppHost/ApplicationLifetimeCoordinator.cs` | 新增 `TaskSyncSettingsStore` 依赖 + `StartupStepTimeout=20s`；`Start()` 重构：激活管道最先启动（`ActivationPipeStarting/Started`）→ 日志保留有界（`LogRetentionFailed`）→ `LoadTaskSyncSettings()`（`StartupTaskSyncSettingsGate`，`TaskSyncSettingsLoaded/LoadFailed`）→ `SchedulerStarting` → `RecoverFromCrash()` 有界（`CrashRecoveryTimeout` 中止启动）→ `SchedulerRunning` → `StartRemoteServer()` 有界（`RemoteStartFailed`，fail-closed 不监听）→ 托盘/仪表盘/通知 → `MainWindowActivating/Activated` → `ApplicationStarted`；启动步骤一律 `WaitAsync(StartupStepTimeout)` |
| `src/AutoShutdown.App/App.xaml.cs` | 新增 `StartupFailureExitCode=5`、`HeadlessStepTimeoutSeconds=20`；主路径 `using var startupGuard = new StartupTimeoutGuard(logger); startupGuard.Arm();` + try/catch：`ServiceProviderBuilt` → headless 分支 / `LifetimeCoordinatorResolving/Resolved` → `_coordinator.Start()` → `Disarm()`；catch → `StartupFailed` 日志 + `Shutdown(5)`；`HandleExternalTriggerAndExit` 失败也非零退出；`RunHeadlessCrashRecovery` `WaitAsync(20s)`，超时 `CrashRecoveryTimeout` + rethrow |
| `src/AutoShutdown.App/Infrastructure/StartupTimeoutGuard.cs`（新增） | 看门狗：Arm/Disarm；默认 30s；触发记 `StartupTimedOut` 并 `Environment.Exit(4)`（进程退出 → OS 释放互斥体） |
| `src/AutoShutdown.App/AppHost/StartupTaskSyncSettingsGate.cs`（新增） | 有界 + fail-closed 装载 task-sync 开关：`Task.Run(LoadAsync).WaitAsync(timeout)`；异常/超时一律关闭并记 `TaskSyncSettingsLoadFailed` |
| `src/AutoShutdown.App/Infrastructure/ActivationPipeServer.cs` | 可选 `pipeName` 构造参数（默认协议名不变）→ 聚焦测试隔离管道名 |
| `src/AutoShutdown.App/Notifications/NotificationCoordinator.cs` | `Dispose()` 幂等（`Interlocked` guard）——协调器与 DI 容器先后双释放不抛 ObjectDisposedException |
| `src/AutoShutdown.App/Presentation/DashboardRefreshService.cs` | `Dispose()` 幂等（同上） |
| `src/AutoShutdown.App/Infrastructure/Remote/RemoteServer.cs` | `Dispose()` 幂等（同上） |
| `tests/AutoShutdown.Tests/S_STARTUP_D1_StartupLifecycleTests.cs`（新增） | 11 个聚焦测试（见 §5） |
| `tests/AutoShutdown.Tests/S_UI3_UiTestLauncherTests.cs` | 已有 S-UI3 聚焦契约测试，未改动（回归验证） |
| `tools/test/Invoke-SStartupD1Probe.ps1`（新增） | 真机探针：启动候选 → 确认主窗口 → 枚举进程窗口 → 托盘退出 |
| `tools/test/Invoke-SStartupD1Loop.ps1`（新增） | 20 轮真机循环（见 §7） |

**明确不改**：SchedulerEngine / TaskCollection / ShutdownWorkflow / IPowerService / TestMode 与 RealPowerEnabled 双门 / TaskSyncCoordinator 同步语义 / Office / 远程白名单 / S22 同步行为 / MainWindow.xaml 布局 / TaskSyncSectionViewModel / RemoteSectionViewModel（回退到原始 fire-and-forget 形式，见 §4.4）。

## 4. 设计要点与实现说明

### 4.1 根因修复（构造期不再做同步文件 I/O）

`ServiceRegistration.cs` 的 factory 不再触碰存储：

```csharp
// S-STARTUP-D1：……开关读取移入启动生命周期步骤（Start → StartupTaskSyncSettingsGate）……
coordinator.Enabled = false;
```

fail-closed 默认关闭保持不变；真实开关读取移到 `ApplicationLifetimeCoordinator.Start()` 内、调度引擎加载任务之前，通过 `StartupTaskSyncSettingsGate.LoadEnabled(store, logger, 20s)` 完成：线程池读取（不在 UI 线程同步文件 I/O）+ `Task.WaitAsync(20s)` 限时 + 任何异常/超时 → 保持关闭。

### 4.2 有界启动步骤 + 启动超时卫兵（双保险）

- 每个可能阻塞的启动初始化统一 `WaitAsync(StartupStepTimeout=20s)`：日志保留、崩溃恢复（`CrashRecoveryTimeout` 视为状态未知 → 中止启动）、远程控制启动（`RemoteStartFailed` → 保持不监听）。
- `StartupTimeoutGuard`（默认 30s）作为**兜底**：若整个启动生命周期超时仍未完成，记录 `StartupTimedOut` 并以退出码 4 终止进程 → OS 释放互斥体 → 下一实例可立即成为主实例。它只兜底，不替代各步骤自身的超时处理；正常启动在 `ApplicationStarted` 后 `Disarm()`。
- `App.xaml.cs` catch → `StartupFailed` + `Shutdown(StartupFailureExitCode=5)`，`OnExit` 释放协调器/容器（含幂等 Dispose）与互斥体，无窗口、非零退出、无残留。

### 4.3 激活管道最先启动

`Start()` 顺序把 `_pipeServer.Start()` 移到最前，使后续启动步骤即使偏慢，次实例的 ACTIVATE 请求也能立即到达并被应答（转发到 UI 线程排队处理），杜绝「主实例卡住但管道未起 → 次实例转发失败」。

### 4.4 已回退的改动（§ 3 之外的说明）

初始曾把 `TaskSyncSectionViewModel` / `RemoteSectionViewModel` 构造期 fire-and-forget 的 `LoadAsync` 包进 `Task.Run`，但这强制线程池跳转，破坏了 `S23_CP5_RemoteSectionTests.Save_EnableButStartFails_HonestError` 的刷新时序（Save 后 continuation 异步完成并覆盖 StatusText）。经评估：这两处是 fire-and-forget（非构造阶段同步等待，非根因），最小返修范围不要求改动它们 → **已回退到原始 `await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);`**。

### 4.5 幂等 Dispose

`ApplicationLifetimeCoordinator.Dispose()` 与 DI 容器都会释放 singletons，`NotificationCoordinator` / `DashboardRefreshService` / `RemoteServer` 在已 Dispose 的 CTS/SemaphoreSlim 上二次 Dispose 会抛 `ObjectDisposedException`，破坏「失败路径完整释放」的保证。三处均加 `Interlocked.Exchange(ref _disposed, 1)` 幂等守卫。

### 4.6 单实例边界不变

仍由 `SingleInstanceCoordinator`（`Local\AutoShutdown.Desktop.Singleton.v1`）+ `ActivationPipeServer/Client`（`AutoShutdown.Desktop.Activation.v1`）承载。未削弱任何安全边界；不自动杀进程、不按进程名杀进程、不转发测试实例请求（`UiTestExistingInstanceRefused` 在 `ActivationPipeClient.Try` 之前的不变量由 S-UI3 源码契约测试守住）。

## 5. 聚焦测试（`S_STARTUP_D1_StartupLifecycleTests.cs`，11 个）

| 测试 | 验证点 |
|---|---|
| `TaskSyncCoordinatorFactory_WithHangingSettingsStore_ResolvesBounded_AndFailClosed` | (1) 根因回归：工厂不得在构造期同步读取（挂起）存储；解析有界；fail-closed 关闭 |
| `CompositionRoot_ResolvesApplicationLifetimeCoordinator_WithoutBlocking` | (2) 协调器在启动前可从组合根有界解析（STA + WPF Application，与真实启动同形态） |
| `StartupTimeoutGuard_Fires_ExitsNonZero_WhenStartupNeverCompletes` | (3) 卫兵触发非零退出（timeout=4）、记 `StartupTimedOut` |
| `StartupTimeoutGuard_DoesNotFire_WhenDisarmedBeforeDeadline` | (3) 正常完成 Disarm 后不误触发 |
| `SingleInstance_MutexReleasedOnFailureCleanup_NextInstanceBecomesPrimary` | (2) 失败清理释放互斥体后下一实例立即成为主实例 |
| `ActivationPipe_ForwardsActivate_AndAcknowledgesOk` | (4) 激活管道把 ACTIVATE 转发到窗口激活服务并应答 OK（测试专用管道名） |
| `StartupTaskSyncSettingsGate_IsBounded_AndFailClosedOnHang` | (6) 挂起存储 → 有界返回 + fail-closed 关闭 + `TaskSyncSettingsLoadFailed` |
| `StartupTaskSyncSettingsGate_LoadsEnabledFromFile_AndFailClosedOnCorrupt` | (6) 首次使用关闭 / 显式启用打开 / 损坏关闭 |
| `TaskSyncCoordinatorFactory_DoesNotSyncLoadSettings_InSource` | 源码契约：工厂段不含 `GetResult`/`LoadAsync`，含 `coordinator.Enabled = false` |
| `AppStartup_HasCheckpointLogs_AndCleanNonZeroFailure` | 源码契约：`PrimaryInstanceAcquired`/`ServiceProviderBuilt`/`LifetimeCoordinatorResolving/Resolved`、卫兵、`StartupFailed`、`Shutdown(5)`、S-UI3 拒绝转发不变量顺序 |
| `CoordinatorStart_HasCheckpointLogs_AndBoundedStartupSteps` | 源码契约：`ActivationPipeStarting/Started`/`SchedulerStarting`/`SchedulerRunning`/`MainWindowActivating/Activated`/`ApplicationStarted`、`StartupTaskSyncSettingsGate`、`WaitAsync(StartupStepTimeout)` |

测试只读/内存，不触碰真实电源、远程监听或系统任务计划；使用 `HangingStorage`（永久挂起）、`RecordingLogger`、`RecordingWindowActivation`、隔离临时数据根。

## 6. 自动化验收清单（本阶段执行）

- [x] Release 还原构建（TreatWarningsAsErrors）：0 错误 0 警告
- [x] .NET Release 全量测试：**1757/1757 通过**（失败 0 / 跳过 0）
- [x] 聚焦测试：S_STARTUP_D1 11/11；S-UI3+S22+S-CLOSEUI1+S13 262/262
- [x] `git diff --check` 退出码 0
- [ ] S-UI3 launcher 测试（S_UI3_UiTestLauncherTests，已含于 262）
- [ ] S22 Task-Scheduler-sync 自动化（仅 Fake + 内存，无真实写入）
- [ ] S-CLOSEUI1 聚焦测试
- [ ] 单实例/激活聚焦测试
- [ ] 20 轮真机循环（§7）
- [ ] ≥2 连续 UIA 冒烟轮（S-UI3）

## 7. 20 轮真机循环（§7 详细流程）

**前置**：从干净实现提交发布候选（`artifacts/release/v2.0.0/S-STARTUP-D1-<shorthash>/AutoShutdown-v2.0.0-S-STARTUP-D1.<shorthash>.exe`），循环对候选 EXE 执行。

每轮（隔离数据根 + `config.json` TestMode=true / RealPowerEnabled=false / StartWithWindows=false / RunCommands 空 / CloseApps 空；**不设 `AUTOSHUTDOWN_UI_TEST`**——循环需验证正常单实例转发，而 UI 测试模式会拒绝转发）：
1. 新建**全新**隔离数据根（临时目录），写入安全 config.json；记正式数据目录前后快照（`%LOCALAPPDATA%\AutoShutdown`，排除 UiTestSandbox）；
2. 启动候选（`AUTOSHUTDOWN_DATA_ROOT=<隔离根>`），记录 PID、等待 `MainWindowHandle != 0`（30s 上限）并记录窗口出现耗时；
3. 重新启动同一候选（次实例）：确认原窗口被激活（记录激活耗时），确认次实例**在有限时间内退出**，确认只留原 PID；
4. 托盘退出主实例（UIA 托盘按钮右键 →「退出程序」）：确认原 PID 消失，记录退出耗时；
5. 残留检查：确认无任何 AutoShutdown.App 进程残留；
6. 记录每轮：PID、窗口出现时间、副实例退出时间、托盘退出时间、残留进程数、正式数据目录前后状态；
7. 每轮使用全新/已验证隔离目录，绝不用正式数据根，**不 taskkill、不按名称清理**。
