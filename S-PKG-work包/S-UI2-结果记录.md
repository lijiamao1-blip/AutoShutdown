# AutoShutdown V2 · S-UI2「首页重构、多任务可用性与发布信息收尾」独立结果记录（回报包）

- 阶段: S-UI2（首页重构、多任务可用性与发布信息收尾）
- 日期: 2026-08-19
- 起始提交: `b41fe3a`（S-UI1 结果记录提交；本阶段不含 S-PKG/历史工作包改动）
- 结束提交: `30b658e`（S-UI2 最终实现提交；结果记录提交随后）
- 执行人: S-UI2 唯一执行 AI（冒烟/测试全部沙箱数据根 `AUTOSHUTDOWN_DATA_ROOT`；真实电源全程 TestMode 隔离）
- 状态: 进行中

## 1. 阶段内提交清单

| 提交 | 内容 |
| --- | --- |
| `30b658e` | S-UI2 实现（独立提交）：XAML/VM/Core/测试/冒烟脚本/契约测试更新 |
| _（结果记录提交）_ | S-UI2 结果记录（本文件）+ 阶段执行书 + 截图/日志证据 |

## 2. 多任务可用性诊断结论

- **限制点（已探明）**：单任务限制不在调度引擎，而在 ViewModel 层的 `MainWindowViewModel.CanCreateNow`
  （`_currentInstance` 非终结即一刀切拒绝，UI 上表现为「已有活动任务」禁用创建按钮）。
- **底层支持（已确认）**：`SchedulerEngine` 以 `Dictionary<Guid,TaskInstance>` 按 `SourceTaskId` 运行多实例；
  `TaskCollection`（Dictionary）、`RuntimeState`/`TasksDocument`（字典/列表）、`TaskSchedulerMapper` 均支持多任务。
- **修复方式**：移除 VM 层 `_currentInstance` 非终结即拒绝的拦截，创建按钮不再被已有活动任务一刀切禁用；
  保留引擎级按任务 ID 的冲突检测与 `TaskArbitrator` 唯一仲裁路径；绝不静默覆盖/删除/停用旧任务、
  绝不直接改 JSON 绕过服务。
- **配套**：`SetTaskEnabledCommand`（已存在）接线为每任务启用/停用；`SchedulerEngine.GetActiveInstances()`
  增加启停双闸门（实例 `IsEnabled` + 定义 `IsEnabled`，停用任务不进入到期/空闲评估、不产生 deadline、
  绝不触发执行；定义缺失的孤儿实例按既有状态推进恢复，fail-open 不静默执行也不静默取消）；
  首页「当前任务」摘要与任务管理页共用 `TaskItems`，2+ 任务显示「共 N 个任务」。

## 3. 修改文件职责

| 文件 | 职责（S-UI2） |
| --- | --- |
| `Directory.Build.props` | 版本单一源：`Version/AssemblyVersion/FileVersion=2.0.0`（SDK 自动追加真实 `+{commit}`） |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/AppInfo.cs` | 版本回退串 `v1.0.0-dev` → `v2.0.0-dev` |
| `src/AutoShutdown.App/MainWindow.xaml` | 首页两列布局去最外层滚动条（左=创建任务内滚/右上=当前任务/右下=最近活动内滚）；每周指定星期 7 复选框；一次性「执行日期/今天/日历放大/过去时间错误」区；Office 只读说明卡；关于页隐私/帮助文案绑定 |
| `src/AutoShutdown.App/Presentation/MainWindowViewModel.cs` | 移除单任务创建拦截（多任务）；`WeekdaySaturday/Sunday`；一次性日期今天/过去校验；`CurrentTaskCountText` 摘要；`RowSetEnabledCommand` 每任务启停；`GoToLogsCommand`；版本文本 v2；`PrivacyBoundaryText/HelpFeedbackText` 改实例属性（`{Binding}` 无法绑定 const） |
| `src/AutoShutdown.Core/Scheduling/SchedulerEngine.cs` | `SetTaskEnabledCommand` 同步运行态实例启用标志并持久化；`GetActiveInstances` 启停双闸门（定义唯一事实源 + 孤儿 fail-open） |
| `src/AutoShutdown.Core/State/RuntimeState.cs` | `TaskInstance.IsEnabled`（默认 true，旧 runtime.json 兼容） |
| `src/AutoShutdown.Core/Tasks/TaskService.cs` | 创建实例时贯通 `IsEnabled` |
| `tests/.../S11UiSourceContractTests.cs` | 契约更新：存在活动任务时创建仍可执行（多任务） |
| `tests/.../S12_4_2TaskActionTests.cs` | 契约更新：`ScheduledTask_CreateRemainsEnabled` |
| `tests/.../S14_Checkpoint4_CreateRuleTests.cs` | 周天零勾选错误文案断言更新（「工作日」→「星期」） |
| `tests/.../S_UI2_MultiTaskHomeTests.cs` | 新增 41 个聚焦测试（多任务/周天/一次性日期/版本单源/Office 与关于/首页摘要） |
| `tools/test/Invoke-ASUI2Smoke.ps1` | 新增 S-UI2 UIA 冒烟（P1~P10，仿 S-UI1 沙箱纪律） |

未改动：`ShutdownWorkflow` 唯一 `IPowerService` 出口、双闸门与冻结状态机、`OfficeSave → RunCommands → CloseApps`
固定顺序、`SchedulerEngine` 唯一仲裁路径、`TaskCollection` 唯一事实源、Task Scheduler 单向同步、
远程控制只读本地配置、未知状态 fail-closed、`RunCommands` 白名单默认空（均经全量测试回归验证）。

## 4. 测试数量汇总（全部实际执行，真实退出码）

| 测试 | 结果 |
| --- | --- |
| Release 构建 | 0 错误 0 警告（TreatWarningsAsErrors） |
| .NET Release 全量测试 | 1578 / 0 / 0 |
| S-UI2 聚焦测试（S_UI2_MultiTaskHomeTests） | 41 / 0 / 0 |
| 每周 7 天测试（Weekday*，含跨周/保存/重载/旧数据兼容） | 32 / 0 / 0 |
| 多任务创建/恢复/冲突测试（Create*/Restart*/TwoTasks*/DisableOneTask*/TaskItems*/CurrentTaskCountText*） | 11 / 0 / 0 |
| 版本单一源测试（Version*/AssemblyVersion/InformationalVersion/DirectoryBuildProps*） | 43 / 0 / 0 |
| Office 与关于 UI 测试（OfficeCard*/PrivacyBoundary/HelpFeedback） | 4 / 0 / 0 |
| UIA 冒烟第 1 轮（独立运行） | 75 / 0 / 2（2 SKIP = DPI 100%/125% 人工待验） |
| UIA 冒烟第 2 轮（独立运行） | 75 / 0 / 2（同上） |
| `git diff --check` | 0 错误 |

日志与截图证据：`S-UI2-截图证据/`（全量测试 / 聚焦测试 / 冒烟两轮日志 + 6 张截图）。

## 5. 阶段验收条件逐项对照

| 执行书验收项 | 结果 | 证据 |
| --- | --- | --- |
| Release 构建 | ✓ | dotnet test -c Release 全量构建成功，0 错 0 警 |
| .NET Release 全量测试 | ✓ | 1578/0 |
| S-UI2 聚焦测试 | ✓ | 41/0 |
| 每周 7 天测试 | ✓ | 32/0 |
| 多任务创建/恢复/冲突测试 | ✓ | 11/0 |
| 版本单一源测试 | ✓ | 43/0（含真实提交哈希） |
| Office 与关于 UI 测试 | ✓ | 4/0 |
| UIA 冒烟连续两轮 | ✓ | 75/0/2 ×2（仅显式 SKIP） |
| `git diff --check` | ✓ | 0 错误 |
| `git status` | ✓ | 仅 S-UI2 实现改动 + 历史未跟踪工作包（S13–S23/S-PKG/LibreOffice MSI，均未覆盖） |
| 冒烟：首页无最外层滚动条 | ✓ | P2（内部滚动可用、无整页滚动条、创建按钮内部滚动可达） |
| 冒烟：首页关键内容完整可见 | ✓ | P1/P2（三区域可见；紧凑 900×580 档不裁切关键控件） |
| 冒烟：周一~周日可见可选 | ✓ | P3（7 复选框；默认周一~五选中/周六日未选；周六可选中可取消） |
| 冒烟：日历可用 | ✓ | P4（执行日期/今天 快捷键/日历弹出含当天日期/过去时间被明确拒绝） |
| 冒烟：连续创建 ≥2 任务 | ✓ | P5（倒计时默认 30 分钟 + 每周指定星期第二个；首页「共 2 个任务」） |
| 冒烟：任务管理同时显示两个 | ✓ | P5（2 行、均启用、按钮按 ControlType.Button 计数） |
| 冒烟：Office 卡可见 | ✓ | P7（只读卡 + 查看日志与诊断 跳转可用） |
| 冒烟：隐私与帮助正文可见 | ✓ | P8（隐私/帮助/诚实反馈通道声明全部可见；const→实例属性修复） |
| 冒烟：各版本位置显示 V2.0.0 | ✓ | P6（顶部状态栏/左下导航/关于页三处同一候选版本文本 v2.0.0-S-UI2.b41fe3a） |

## 6. 构建警告/错误与遗留问题

- 构建：Release 0 警告 0 错误（`TreatWarningsAsErrors=true`，构建成功即无警告）。
- 人工待验（本环境不可执行，如实标注，未伪称覆盖）：
  - DPI 100% / 125% 完整电池：本机为单显示器 150%（AppliedDPI=144→150%），其余档位冒烟按 SKIP 记录。
  - 1600×900 / 1920×1080 在其它 DPI 机器上的精确显示（本机 150% 已 PASS 精确尺寸）。
  - 真机 WoL / RTC 唤醒 / 远程控制端到端。
  - Office 真实文档端到端（Word/Excel/PowerPoint 运行中保存）。
  - 日历键盘操作（方向键/Enter 选日期）真机体验。
- 无遗留代码问题；无新增第三方依赖（日历沿用 WPF 原生 DatePicker，仅定制样式放大）。

## 7. 最终工作树状态

- S-UI2 实现提交 `30b658e` 已完成（结果记录提交随后单独进行）。
- 本阶段提交后，工作树仅余历史未跟踪工作包与输入物（`S13-work包/`…`S23-work包/`、`S-PKG-work包/` 下历史记录、
  `LibreOffice_26.2.5_Win_x86-64.msi` 等），均未覆盖、未改动。
- 未进行 `git push`；未进入 S-UI3 或其他阶段。
