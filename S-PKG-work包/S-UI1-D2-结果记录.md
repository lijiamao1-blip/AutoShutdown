# AutoShutdown V2 · S-UI1-D2「空闲触发任务界面显示真实空闲状态」独立返修结果记录（回报包）

- 阶段: S-UI1-D2（空闲任务等待触发时，首页「当前任务」卡显示**真实连续空闲时长**，替换误导性固定占位倒计时；纯 UI 展示修复，不改触发/取消语义）
- 日期: 2026-08-19
- 起始提交: `dcc0e73`（S-UI2 结果记录提交；本阶段不含 S-PKG/历史工作包改动）
- 实现提交: `943d685`（`S-UI1-D2: idle-trigger task shows real idle duration on current-task card`）
- 结果记录提交: _本文件提交（见 §1）_
- 执行人: S-UI1-D2 唯一执行 AI（冒烟/测试全部沙箱数据根 `AUTOSHUTDOWN_DATA_ROOT`；真实电源全程 TestMode 隔离）
- 状态: 完成（等待总顾问独立验收）

## 1. 阶段内提交清单

| 提交 | 内容 |
| --- | --- |
| `943d685` | S-UI1-D2 实现（独立提交）：VM 可选参数 + Refresh 空闲分支 + XAML 当前任务卡局部样式 + ServiceRegistration 接线 + 新增聚焦测试文件 |
| _（结果记录提交）_ | S-UI1-D2 结果记录（本文件）+ 冒烟/测试日志证据 + 150% 档截图证据（本提交） |

## 2. 问题根因与返修结论（对照执行书 §1）

- **根因**：`NextExecutionCalculator.CalculateIdle()` 返回 `now + threshold` 作为**占位 fire time**；UI 将 `ScheduledFireTime - now` 显示为倒计时，故空闲任务等待期显示固定占位倒计时（如 00:29:48），与真实输入无关；`CountdownSourceText` 用 `IsIdleTriggered` 判来源，未触发时空闲任务错误显示"定时排程"。
- **真实触发安全**：`SchedulerEngine.TryEvaluateIdleAsync()` 每秒用 `GetLastInputInfo` 检测，连续空闲达阈值才 `ArmIdleAsync` 进入确认窗口，输入恢复即取消；**不会误关机**。本返修不触碰该语义。
- **返修内容**：空闲任务 `Waiting`（`IsIdleTriggered=false`）时，首页「当前任务」卡显示真实连续空闲时长 + 阈值（`CountdownText`）、来源"空闲触发"（`CountdownSourceText`）、"等待连续空闲达阈值后触发"（`NextFireTimeText`）；检测失败 / 依赖缺失 fail-closed 显示"输入状态未知（无法确认空闲）"，不显示假数字。

## 3. 修改文件职责

| 文件 | 职责（S-UI1-D2） |
| --- | --- |
| `src/AutoShutdown.App/Presentation/MainWindowViewModel.cs` | 构造函数新增可选参数 `ITaskService? taskService = null`、`IIdleMonitor? idleMonitor = null`（保持既有测试构造兼容）；`Refresh()` 主实例分支判定"Idle 任务 + Waiting + 未触发"→ 按显示规则生成文本；新增 `IsIdleStatusVisible`、`IsIdleTask()`、`BuildIdleStatusText()`、`ResolveIdleThreshold()`（阈值取 `definition.IdleThresholdSeconds`，未设置回退 `IdleShutdownRule.GlobalDefaultThreshold`=30 分钟）；`taskService`/`idleMonitor` 为 null 或 `GetIdleDuration()` 抛异常均 fail-closed |
| `src/AutoShutdown.App/MainWindow.xaml` | 「当前任务」卡 `CountdownText` TextBlock 局部样式化：`Effect`（DropShadowEffect）由局部元素值移入 Style Setter，使空闲态 `Effect="{x:Null}"` DataTrigger 真正生效（WPF 优先级：局部值 > 样式触发器；样式 Setter 低于触发器，故从局部移到 Setter）；空闲态 FontSize 40→15、FontWeight Bold→SemiBold、Foreground 白→深蓝，`TextWrapping="Wrap"`，无 Viewbox、无裁切 |
| `src/AutoShutdown.App/AppHost/ServiceRegistration.cs` | 既有 `MainWindowViewModel` 单例注册追加命名参数 `taskService: GetRequiredService<ITaskService>()`、`idleMonitor: GetRequiredService<IIdleMonitor>()`（生产路径接线；VM 可选参数默认 null，无接线则生产恒 fail-closed；该文件不在执行书允许清单，也不在禁止清单，为功能上线必需的最小接线） |

未改动：`src/AutoShutdown.Core/` 全部、`tools/` 全部、`*.sln`、`Directory.Build.props`、`NuGet.Config`、既有测试文件既有断言、S-UI1-D1 Office 卡。

## 4. 新建文件清单

| 文件 | 职责 |
| --- | --- |
| `tests/AutoShutdown.Tests/S_UI1_D2_IdleStatusDisplayTests.cs` | D2 聚焦测试 9 个（纯内存 + 假依赖，绝无真实电源），覆盖执行书 §4.1 全部 7 条 |
| `S-PKG-work包/S-UI1-D2-结果记录.md` | 本文件 |
| `S-PKG-work包/S-UI1-D2-截图证据/` | 冒烟/测试证据（见 §5） |

## 5. 测试数量汇总（全部实际执行，真实退出码）

| 测试 | 结果 |
| --- | --- |
| Release 构建（`dotnet build src/AutoShutdown.App/AutoShutdown.App.csproj -c Release`） | 0 错误 0 警告，rc=0 |
| .NET Release 全量测试（`dotnet test tests/AutoShutdown.Tests/AutoShutdown.Tests.csproj -c Release --verbosity minimal`） | **1587 / 0 / 0**（≥ S-UI2 基线 1544；含 D2 新增 9 个） |
| D2 聚焦测试（`--filter FullyQualifiedName~S_UI1_D2_IdleStatusDisplayTests`） | **9 / 0 / 0** |
| UIA 冒烟 连过第 1 轮（S-UI2 冒烟脚本，独立运行） | **75 / 0 / 2**（2 SKIP = DPI 100%/125% 人工待验），EXIT=0 |
| UIA 冒烟 连过第 2 轮（独立运行） | **75 / 0 / 2**（同上），EXIT=0 |
| `git diff --check` | 0 错误 |

证据文件：`S-PKG-work包/S-UI1-D2-截图证据/`：
`S-UI1-D2-全量测试-1587-0.txt`、`S-UI1-D2-聚焦测试-9-0.txt`、`S-UI1-D2-冒烟-连过-第1轮-75-0-2.txt`、`S-UI1-D2-冒烟-连过-第2轮-75-0-2.txt`、
`about/home/office/onetime/tasks/weekday-150percent.png`（150% 档 6 张，来自冒烟通过的轮次）。

### 5.1 GUI 冒烟完整记录（如实记录，含抖动）

对含 D2 改动的候选 `AutoShutdown-v2.0.0-S-UI2.943d685.exe` 共运行 5 轮：

| 序列 | 轮次 | 结果 | 备注 |
| --- | --- | --- | --- |
| R1 | 第 1 轮 | 75/0/2 通过 | |
| R2 | 第 2 轮 | 74/1/2 失败 | P5 `停用按钮数(Button)=0`（期望 2） |
| R3 | 第 3 轮 | 74/1/2 失败 | 同 R2 同一断言 |
| R4 | **连过第 1 轮** | 75/0/2 通过 | |
| R5 | **连过第 2 轮** | 75/0/2 通过 | |

对照：对**未含 D2 改动的 S-UI2 基线** `AutoShutdown-v2.0.0-S-UI2.dcc0e73.exe` 运行 2 轮：

| 轮次 | 结果 | 备注 |
| --- | --- | --- |
| B1 | 75/0/2 通过 | |
| B2 | 74/1/2 失败 | **同一断言** `停用按钮数(Button)=0` |

**抖动结论（如实报告）**：P5 断言先 `Wait-Until`「停止」按钮出现（静态 `Content="停止"`），随即**无等待**统计「停用」按钮（`Content="{Binding EnabledText}"` 绑定内容）。"停止"为静态内容、立即可见；"停用"需 Content 绑定→WPF AutomationPeer 名称暴露，存在 UIA 树时序窗口。该断言在含 D2 的候选与未含 D2 的基线上以**完全相同方式**偶发失败，证明为**既有冒烟脚本的时序抖动，非 D2 引入**（D2 未改动任务列表行/按钮渲染；冒烟创建的任务为倒计时+每周指定星期，非 Idle，D2 空闲分支不参与）。`tools/test/Invoke-ASUI2Smoke.ps1` 受硬约束不得修改，故如实记录而非改断言掩盖；最终取得连续两轮通过（R4+R5）满足执行书 §4.4。

### 5.2 候选 EXE 发布说明

执行书 §4.4 要求运行 S-UI2 冒烟脚本，其版本正则要求 `AutoShutdown-v[\d.]+-S-UI2\.[0-9a-f]+.exe`。本次用 `dotnet publish` + `-p:InformationalVersion=v2.0.0-S-UI2.943d685` 产出并重命名为 `artifacts/d2-smoke/S-UI2-943d685/AutoShutdown-v2.0.0-S-UI2.943d685.exe`（`artifacts/` 已 gitignore），未覆盖历史 `artifacts/release/v2.0.0/S-UI2-dcc0e73/` 候选。

## 6. 阶段验收条件逐项对照

| 执行书验收项 | 结果 | 证据 |
| --- | --- | --- |
| Release 构建 0/0 | ✓ | dotnet build -c Release，0 错 0 警 |
| .NET Release 全量测试 0 失败 | ✓ | 1587/0/0（≥1544） |
| D2 聚焦测试 | ✓ | 9/0/0（覆盖 §4.1 全部 7 条） |
| UIA 冒烟连续两轮通过 | ✓ | 75/0/2 × 2（R4+R5 连过；抖动如实记录） |
| Idle 任务 Waiting 显示真实空闲时长 + "空闲触发" | ✓ | 聚焦测试 #1/#2/#7；`BuildIdleStatusText` |
| 空闲检测失败 fail-closed "输入状态未知" | ✓ | 聚焦测试 #3/#6 |
| 非 Idle / Idle 已触发（Confirming）维持现状 | ✓ | 聚焦测试 #4/#5 |
| `taskService`/`idleMonitor` 为 null 不崩溃 | ✓ | 聚焦测试 #6（含 `MissingTaskService_DoesNotCrash`） |
| 阈值解析（显式 600s→10 分钟；缺省→30 分钟） | ✓ | 聚焦测试 #7 |
| `git diff --check` | ✓ | 0 错误 |
| 原记录文件未被覆盖；未 git push | ✓ | 未改动任何既有 `.md`；无 push |

## 7. 安全边界核查（对照执行书 §0/§7）

- `git diff dcc0e73..943d685 -- src/AutoShutdown.Core/` → **空**（Core 未修改）。
- `git diff dcc0e73..943d685 -- tools/` → **空**（冒烟脚本未动）。
- 仅修改 4 个文件（VM/XAML/ServiceRegistration + 新建测试），见 §3/§4。
- SchedulerEngine 空闲触发/取消语义未变：`IsIdleDue`/`ArmIdleAsync`/`CancelIdleTriggeredAsync` 调用点未动（Core diff 空蕴含）。
- Pre-Pipeline 顺序、FailurePolicy、ShutdownWorkflow 未变；**无新增 IPowerService 调用方**（diff 全文检索无新增 `IPowerService` 引用）。
- S-UI1-D1 Office 卡未被动过（diff 无任何 Office/Word/Excel/WPS/只读 相关行）。
- 既有测试文件既有断言未修改（仅新建测试文件）。

## 8. 已知限制

- `ITaskService`/`IIdleMonitor` 经 DI 注入：测试用假实现，生产为单例（`ServiceRegistration.cs` 接线）；生产路径 `GetIdleDuration()` 走真实 `GetLastInputInfo`，与测试假实现存在行为差异，但显示逻辑（fail-closed 分支）已由测试覆盖。
- 首页「当前任务」卡按 `DashboardRefreshService` 现有 800ms 周期刷新，空闲时长文本最长 800ms 延迟回落（执行书认可）。
- `taskService`/`idleMonitor` 缺失时 fail-closed 显示"输入状态未知（无法确认空闲）"，不崩溃、不显示占位数字。
- S-UI2 既有证据 PNG（`S-PKG-work包/S-UI2-截图证据/*.png`，会话开始前已被既有冒烟运行覆盖，mtime 12:21）在工作树中为 `M` 状态，属既有噪声、非 D2 改动；因硬约束"不得 git reset/checkout 丢弃改动"，未做还原，留待总顾问裁定（本阶段 D2 证据存放于独立的 `S-UI1-D2-截图证据/`）。
- UIA 冒烟 P5「停用按钮数」时序抖动（§5.1）：既有冒烟脚本断言时序问题，候选与基线同频出现；工具脚本受约束不可改。

## 9. 人工待验项目（本环境不可执行或需真实桌面，如实标注，未伪称覆盖）

- DPI 100% / 125% 完整电池：本机单显示器 150%（AppliedDPI=144→150%），100%/125% 按 SKIP 记录，需在对应缩放机器上运行。
- **真实空闲时长在真实桌面上的显示**：UIA 冒烟不创建空闲任务，需人工创建「空闲触发」任务后在真实桌面观察"已连续空闲 X 分 Y 秒 / 阈值 Z 分钟"随输入恢复回落、达到阈值后进入确认窗口的行为。
- 空闲时长在小窗口 900×580 与各 DPI 下文字完整、可滚动访问、不裁切的最终人工目检（150% 档冒烟截图已作为佐证）。

## 10. `git status --short` 输出（实现提交 `943d685` 后、结果记录提交前；执行书 §5.9）

```text
 M "S-PKG-work包/S-UI2-截图证据/about-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/home-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/office-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/onetime-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/tasks-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/weekday-150percent.png"
?? LibreOffice_26.2.5_Win_x86-64.msi
?? "S-PKG-work包/AutoShutdown-V2-S-PKG-D2-最小返修执行书.md"  …（历史工作包，未覆盖）
?? "S-PKG-work包/S-UI1-D2-截图证据/"                                  （本阶段证据）
?? "S-PKG-work包/S-UI1-D2-结果记录.md"                                （本文件）
?? S13-work包/… S23-work包/… S-PKG-work包/…                          （历史输入物，未覆盖）
```

说明：结果记录提交仅新增 `S-UI1-D2-结果记录.md` 与 `S-UI1-D2-截图证据/`（提交后不再显示为 `??`）；其余 `M`（S-UI2 既有证据 PNG，会话开始前既有冒烟覆盖所致）与 `??`（历史工作包/输入物）均非 D2 改动、未触碰（见 §8 已知限制）。完整原文见本阶段终态 `git status --short`。

## 11. 最终工作树状态

- S-UI1-D2 实现提交 `943d685` 已完成；结果记录提交（本文件 + 证据）随后单独进行。
- 未进行 `git push`；未进入其他阶段；未 reset/checkout 丢弃任何改动；未覆盖任何既有 `.md` 记录。
