# AutoShutdown V2 · S-FINALUI1「UI 功能改进正式闭环」独立结果记录

- 阶段: S-FINALUI1（工作树 UI 功能改进的追溯建档、独立检查、测试与正式提交）
- 执行: 本阶段唯一执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根
- 基线 HEAD: `db1673977c54b10e15dff4b7a10b83469b41d219`
- 结束 HEAD: 实现提交 + 结果记录提交（两个本地提交哈希见阶段报告随附 git log）
- 环境: Windows 11（10.0.26200），.NET 8 `net8.0-windows`

## 0. 追溯建档声明

- 本执行书覆盖的 9 个已跟踪源码、测试与脚本修改，在补建执行书之前已由用户授权 Codex 5.6-sol 实施；经总顾问裁决确认归属，非未知污染，不得丢弃。
- 本阶段为追溯建档、独立检查、测试与正式提交；**未虚构原始执行时间、原始测试结果或执行者**。本结果记录中的全部构建、测试、冒烟结果均由本阶段唯一执行 AI 于 2026-08-21 实际执行。
- 用户已实际尝试除「Windows 任务计划程序同步」以外的全部功能，暂未发现问题；**用户没有真机测试 Windows 任务计划程序同步**。
- 范围严格限于当前已存在的 9 个已跟踪修改；未顺手修改 Core、排程、Office、电源、远程或安装逻辑（`git diff HEAD --stat -- src/AutoShutdown.Core/` 为空）。

## 1. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short` / `git diff --cached` / `git diff --check` / `git log --oneline -15` | master / `db16739…` / 15 个已跟踪修改（9 个源码测试脚本 + 6 张旧截图）+ 未跟踪工件 / 暂存区为空 / exit 0（仅 LF→CRLF 提示）/ 15 条 log，全部一致 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release --nologo` | **0 警告 / 0 错误**，exit 0 |
| .NET Release 全量测试 | `dotnet test AutoShutdown.sln -c Release --no-build` | **1745 通过 / 0 失败 / 0 跳过**，exit 0 |
| 批量操作聚焦测试 | `--filter BulkSnooze\|BulkStop\|BulkClear\|TaskManagement_ExposesFourSafeBulkActions` | **4 通过 / 0 失败 / 0 跳过**，exit 0 |
| S11/S12/S13/S-UI2 聚焦 | `--filter S11UiSourceContractTests\|S12_4_2TaskActionTests\|S13_T08_TaskListUiTests\|S_UI2_MultiTaskHomeTests` | **111 通过 / 0 失败 / 0 跳过**，exit 0 |
| S-CLOSEUI1 相关测试 | `--filter S_CLOSEUI1` | **139 通过 / 0 失败 / 0 跳过**，exit 0 |
| S22 Windows Task Scheduler 同步自动化 | `--filter S22_` | **115 通过 / 0 失败 / 0 跳过**，exit 0 |
| S-UI3 启动器测试 | `--filter S_UI3` | **6 通过 / 0 失败 / 0 跳过**，exit 0 |
| UIA 冒烟 第 1 轮 | `Invoke-ASUI2Smoke.ps1 -ReleaseExe S-UI2-db16739 候选 -EvidenceDir S-FINALUI1-截图证据` | 74 通过 / 1 失败 / 2 跳过（见 §3 竞态说明），exit 1 |
| UIA 冒烟 第 2 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0 |
| UIA 冒烟 第 3 轮 | 同上，独立运行 | 74 通过 / 1 失败 / 2 跳过（同一竞态），exit 1 |
| UIA 冒烟 第 4 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0 |
| UIA 冒烟 第 5 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0（与第 4 轮连续通过） |
| 空白检查 | `git diff --check` | exit 0，无空白错误（仅已跟踪文件的 LF→CRLF 提示） |

冒烟每轮 2 SKIP = DPI 100% / 125% 完整电池在本机（单显示器 150%）无法覆盖，按既有口径列入人工待验。

## 2. 逐项审查结论（独立检查，9 项全部通过）

1. **四个批量操作只经既有路径**：`BulkDisableCommand`/`BulkSnoozeCommand`/`BulkStopCommand`/`BulkClearCommand` 全部调用 `SubmitCommandAsync`/`SubmitSetEnabledAsync` → `_engine.SubmitAsync(command)`（引擎唯一仲裁路径）。启停持久化经 `ConfigurationService.SaveTasksAsync`，**不直接改写 tasks.json / runtime.json**（`MainWindowViewModel.cs:3182-3245, 3395-3439`）。
2. **部分失败如实报告**：每个任务的命令独立提交；`SubmitCommandAsync` 对每次提交分别写 `StatusMessage` 与 `AppendActivity`（成功/失败/异常），不会把部分失败显示为全部成功（`MainWindowViewModel.cs:3410-3430`）。
3. **批量清除边界**：`CanClear` 仅对 Cancelled/Executed/Faulted/Interrupted（终态）为真；`ExecuteBulkClearAsync` 只选 `CanClear` 项，绝不删除仍在执行、状态不明或不允许删除的任务（`MainWindowViewModel.cs:2522-2525, 3140-3156`）。
4. **延迟 10 分钟逐任务经仲裁与状态机**：`ExecuteBulkSnoozeAsync` 逐任务提交 `SnoozeTaskCommand(InstanceId, StageToken, 10min)`，带阶段令牌经引擎仲裁与状态机推进，**不直接篡改截止时间**；且仅对 Waiting 状态任务执行（`CanSnooze`，`MainWindowViewModel.cs:2512-2514, 3111-3120`）。
5. **AppendActivity 时区语义一致**：`AppendActivity` 由 `_clock.UtcNow.ToString("HH:mm:ss")` 改为 `LocalNow()`（=`TimeZoneInfo.ConvertTime(UtcNow, _clock.LocalTimeZone)`），与全库显示时间（任务执行时间/告警时间/同步时间等）的本地时区语义一致；测试 `AppendActivity_DisplaysTimeInConfiguredLocalTimeZone` 用 +8 时区验证通过。
6. **CloseApps 卡片仅摘要与导航**：首页关闭应用卡仅显示「当前已配置 N 个目标」摘要 + 「前往软件设置」按钮（`SettingsCommand` → 设置页），**无新增关闭/结束/强杀动作**；S11 契约断言整页只保留一个 `SaveCloseAppsCommand` 与一个 `OpenProcessPickerCommand`。
7. **无人值守说明不夸大安全能力**：说明块仅作通俗解释（自启动/任务计划同步/无人值守三者分工）并强调「启用全局授权不会自动应用到全部任务，创建具体任务时仍需单独选择」，无安全能力夸大。
8. **UIA 精确 PID 回收**：冒烟脚本 `Add-LaunchedProc` 只记录本轮自己 `Start-Process` 启动的候选 EXE 精确 PID，`Stop-LaunchedProcs`/`Stop-SmokeApp` 只按记录的精确 PID 结束，**绝不按进程名批量结束、绝不误杀生产 AutoShutdown.exe**（`Invoke-ASUI2Smoke.ps1:58-78, 482-518`）。冒烟 5 轮后无残留 AutoShutdown 进程。
9. **cmd pause 纪律**：`启动AutoShutdown安全测试界面.cmd` 仅当启动退出码非 0 时显示失败提示并 `pause`；成功路径无 `pause`、不吞错误、`exit /b %launchExitCode%` 保留原始退出码，不把失败改成成功。

## 3. UIA 冒烟竞态说明（如实，非应用缺陷）

冒烟第 1、3 轮各出现 1 项失败：`P5: 两任务均为启用(显示「停用」按钮)  停用按钮数(Button)=0`。该断言在 `Wait-Until`「停止」按钮出现后**立即无等待**统计「停用」按钮（`Invoke-ASUI2Smoke.ps1:756-759`），为 S-UI2-D1 已定位并记录的既有 UIA 树时序竞态：`DashboardRefreshService` 每 800ms 刷新，`RefreshTaskList` 无条件 `TaskItems.Clear()` 后重建全部行，行按钮的自动化 peer 在刷新周期内异步重建；绑定内容按钮（「停用」）的 peer 名比静态内容按钮（「停止」）晚就绪。应用本身渲染正确（冒烟 P9 `tasks-150percent.png` 两行均含「停用」按钮）。该断言属冒烟脚本既有断言，本阶段范围不允许改动，故以多轮运行取得连续通过：第 4、5 轮连续两轮 **75/0/2** 通过（满足「至少连续两轮」）。

## 4. Windows 任务计划程序同步记录口径

后端：已实现。UI：已有。自动化：已验证（S22 聚焦 115 项全部通过，全部使用 `AutoShutdown.Core.Tasks.TaskService` + Fake 适配器 + 内存存储，**未写入系统任务计划程序**）。用户真机验证：未执行。执行 AI 本轮真机验证：未执行。发布前状态：**人工待验**。

> Windows 任务计划程序同步后端、映射、同步协调、配置 fail-closed、外部回调和安全契约已有自动化测试证据；Windows 真机创建、更新、删除、触发、权限失败和关闭同步后的清理行为仍为人工待验。

本轮未勾选「启用计划任务同步」、未点击任何会产生真实写入的同步操作、未创建/更新/删除系统计划任务、未手动运行外部计划任务、未修改或清理 `\AutoShutdown V2\` 系统任务目录、未提权或以管理员身份运行。

## 5. 测试日志绝对路径

| 项目 | 日志绝对路径 |
|---|---|
| Release 构建 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\release-build.log` |
| 全量测试 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\full-tests.log` |
| 批量操作聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\focus-bulk-actions.log` |
| S11/S12/S13/S-UI2 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\focus-s11-s12-s13-ui2.log` |
| S-CLOSEUI1 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\focus-closeui1.log` |
| S22 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\focus-s22.log` |
| S-UI3 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\focus-s-ui3.log` |
| 冒烟 第 1~5 轮 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\smoke-round{1..5}.log` |
| 空白检查 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\git-diff-check.log` |
| 候选发布 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-logs\publish-s-ui2-candidate.log` |

截图证据目录：`D:\电脑定时关机重建完整版\S-PKG-work包\S-FINALUI1-截图证据\`（home/weekday/onetime/tasks/office/about 共 6 张 150% PNG，冒烟通过轮次生成）。

## 6. 提交清单

1. **实现提交**：9 个已跟踪修改（`MainWindow.xaml`、`MainWindowViewModel.cs`、`Controls.xaml`、`S11UiSourceContractTests.cs`、`S12_4_2TaskActionTests.cs`、`S13_T08_TaskListUiTests.cs`、`S_UI2_MultiTaskHomeTests.cs`、`Invoke-ASUI2Smoke.ps1`、`启动AutoShutdown安全测试界面.cmd`）+ `AutoShutdown-V2-S-FINALUI1-阶段执行书.md`。
2. **结果记录提交**：`S-FINALUI1-结果记录.md`（本文件）+ `S-FINALUI1-截图证据/`（6 张 PNG）。
3. **未提交**：六张已跟踪修改的旧 S-UI2 截图（`S-UI2-截图证据/*-150percent.png`）、LibreOffice MSI、历史工作包、S-UI2-D1 未闭环证据、`diag-btns.ps1`、`diag-fit.ps1`、`_*_extracted.txt`、stash 内容、其他无关未跟踪工件。均未删除、未移动、未覆盖。

## 7. 最终 git status

（结果记录提交后附快照，见阶段报告。）

## 8. 后续

完成结果记录提交后立即停止，等待总顾问独立复验。未 `git push`、未自动进入打包。只有总顾问确认 S-FINALUI1 通过并指定新的最终 HEAD 后，才能恢复 S-PKG2 正式打包。
