# AutoShutdown V2 · S-FINALUI1「UI 功能改进正式闭环」阶段执行书

- 阶段: S-FINALUI1（当前工作树中由用户授权 Codex 5.6-sol 实施的 UI 功能改进的追溯建档、独立检查、测试与正式提交）
- 基线: `db1673977c54b10e15dff4b7a10b83469b41d219`（提交 `db16739` S-CLOSEUI1-D3）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入打包
- 工作包目录: `S-PKG-work包/`（既有历史工作包与 LibreOffice MSI 全部保留、未覆盖）
- 执行: 本阶段唯一执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根

## 0. 追溯建档声明（如实，不虚构）

1. **修改归属**：本执行书覆盖的 9 个已跟踪源码、测试与脚本修改，在补建本执行书之前，已由用户另行授权 Codex 5.6-sol 实施。经总顾问裁决确认，这些修改不属于未知污染，不得丢弃。
2. **本阶段性质**：本阶段属于**追溯建档、独立检查、测试与正式提交**。不得虚构原始执行时间、原始测试结果或执行者。
3. **用户实测情况（如实）**：用户已实际尝试除「Windows 任务计划程序同步」以外的下述新增/调整功能，暂未发现问题：
   - 任务批量停用
   - 批量延迟 10 分钟
   - 批量停止
   - 批量清除
   - 首页 CloseApps 摘要及前往软件设置
   - 无人值守通俗说明
   - RadioButton 间距调整
   - AppendActivity 使用 LocalNow()
   - 安全测试启动入口及相关 UIA 调整
4. **用户真机待验（如实）**：用户**没有**真机测试 Windows 任务计划程序同步。本轮不得写成「用户真机使用验证通过」，也不得写成「执行 AI 独立真机复现通过」。当前可写的记录口径见第 5 节。
5. **范围硬约束**：范围严格限于当前已存在的 9 个已跟踪修改。不得顺手修改 Core、排程、Office、电源、远程或安装逻辑。

## 1. 硬性约束（不得违反）

1. **范围**：只处理当前工作树中已存在的 9 个已跟踪源码/测试/脚本修改（见 §2）；不得新增功能改动，不得修改 Core、排程、Office、电源、远程或安装逻辑。
2. **保留工件**：六张已跟踪修改的 S-UI2 旧截图（`S-UI2-截图证据/*-150percent.png`）保持原样：不 reset、不 checkout、不覆盖、不加入本阶段提交。
3. **不提交项**：LibreOffice MSI、历史工作包、S-UI2-D1 未闭环证据、`diag-btns.ps1`、`diag-fit.ps1`、`_*_extracted.txt`、stash 内容、其他无关未跟踪工件，一律不提交，也不得删除、移动或覆盖。
4. **暂存纪律**：提交只显式暂存本阶段文件，绝不 `git add -A` / `git add .`。
5. **不触碰系统任务计划程序**：本轮只执行不写入系统任务计划程序的 S22 自动化测试；不得勾选「启用计划任务同步」、不得点击产生真实写入的同步操作、不得创建/更新/删除系统计划任务、不得手动运行外部计划任务、不得修改或清理 `\AutoShutdown V2\` 系统任务目录、不得提权或以管理员身份重新运行。
6. **批量操作边界**：四个批量操作只通过既有 TaskService、SchedulerEngine、命令和状态机路径执行；不得直接改写 tasks.json 或 runtime.json。
7. **进程回收纪律**：UIA 脚本只能结束本轮脚本自己启动并保存的精确 PID，不得按进程名批量结束；不得为测试结束正式 AutoShutdown 实例。检测到正式实例时停止并提示用户从托盘正常退出。
8. **cmd 纪律**：`启动AutoShutdown安全测试界面.cmd` 的 `pause` 只能用于失败提示，不得改变成功路径、吞掉错误或把失败退出码变成成功。

## 2. 修改范围（9 个已跟踪文件，追溯登记）

| 文件 | 功能归属 |
| --- | --- |
| `src/AutoShutdown.App/MainWindow.xaml` | 首页/任务管理 UI 布局调整；四个批量操作按钮；首页 CloseApps 摘要卡片（仅摘要+导航）；无人值守通俗说明块 |
| `src/AutoShutdown.App/Presentation/MainWindowViewModel.cs` | 四个批量操作命令（全部经既有 `SubmitCommandAsync`/`SubmitSetEnabledAsync` → 引擎唯一仲裁路径）；`AppendActivity` 改用 `LocalNow()` |
| `src/AutoShutdown.App/Themes/Controls.xaml` | RadioButton 间距调整（`Margin` `0,0,8,0` → `0,0,8,5`） |
| `tests/AutoShutdown.Tests/S11UiSourceContractTests.cs` | 契约测试：CloseApps 首页摘要/无人值守说明/批量操作存在性 |
| `tests/AutoShutdown.Tests/S12_4_2TaskActionTests.cs` | `AppendActivity_DisplaysTimeInConfiguredLocalTimeZone` |
| `tests/AutoShutdown.Tests/S13_T08_TaskListUiTests.cs` | 批量延迟/批量停止/批量清除三个聚焦测试 |
| `tests/AutoShutdown.Tests/S_UI2_MultiTaskHomeTests.cs` | 首页布局契约测试（时间模式行间距/卡片顶端对齐） |
| `tools/test/Invoke-ASUI2Smoke.ps1` | UIA 冒烟：精确 PID 回收；P2 首页无滚动条断言；P5 创建任务计数路径调整 |
| `启动AutoShutdown安全测试界面.cmd` | 失败时 pause 提示，保留原始退出码 |

## 3. 逐项审查要点（独立检查）

1. 四个批量操作是否只通过既有 `TaskService`、`SchedulerEngine`、命令和状态机路径执行；不得直接改写 tasks.json 或 runtime.json。
2. 批量停用、停止、清除遇到部分失败时必须如实报告，不能显示全部成功。
3. 批量清除不得删除仍在执行、状态不明或不允许删除的任务。
4. 延迟 10 分钟必须逐任务经过既有仲裁和状态机，不得直接篡改截止时间绕过规则。
5. `AppendActivity` 改用 `LocalNow()` 后，显示时间与既有日志和时区语义一致。
6. CloseApps 卡片只能显示摘要和导航，不得新增关闭、结束或强杀动作。
7. 无人值守说明不得夸大安全能力。
8. UIA 脚本只能结束本轮脚本自己启动并保存的精确 PID，不得按进程名批量结束。
9. cmd 的 `pause` 只能用于失败提示，不得改变成功路径、吞掉错误或把失败退出码变成成功。

## 4. 测试要求（全部实际执行并如实记录）

- Release build
- .NET Release 全量测试
- 四个批量操作聚焦测试
- S11、S12、S13、S-UI2 相关聚焦测试
- S-CLOSEUI1 相关测试
- S22 Windows Task Scheduler 同步自动化测试（只运行不写系统任务计划程序的自动化测试）
- S-UI3 启动器测试
- UIA 冒烟至少连续两轮
- `git diff --check`

每组测试必须报告：通过数、失败数、跳过数、退出码、日志绝对路径。

UIA 使用隔离数据根：`AUTOSHUTDOWN_DATA_ROOT=%LOCALAPPDATA%\AutoShutdown\UiTestSandbox`（安全测试启动入口使用）；保持 `TestMode=true`、`RealPowerEnabled=false`、Windows Task Scheduler 同步关闭、无人值守关闭、远程监听关闭、RunCommands 白名单和命令列表为空、CloseApps 目标和强杀授权为空；不读取或覆盖正式用户数据。

## 5. Windows 任务计划程序同步的记录口径

后端：已实现。UI：已有。自动化：已验证。用户真机验证：未执行。执行 AI 本轮真机验证：未执行。发布前状态：**人工待验**。

允许的记录表述：
> Windows 任务计划程序同步后端、映射、同步协调、配置 fail-closed、外部回调和安全契约已有自动化测试证据；Windows 真机创建、更新、删除、触发、权限失败和关闭同步后的清理行为仍为人工待验。

## 6. 提交计划

测试全部通过后：

1. **实现提交**（单独）：9 个已跟踪实现/测试/脚本文件 + 本执行书。
2. **结果记录提交**（单独）：`S-FINALUI1-结果记录.md` + 本轮新生成、放在独立证据目录中的必要证据。
3. 不得提交六张旧 S-UI2 截图；不得提交无关未跟踪工件；不 `git push`。
4. 完成结果记录提交后立即停止，等待总顾问独立复验。只有总顾问确认 S-FINALUI1 通过并指定新的最终 HEAD 后，才能恢复 S-PKG2 正式打包。不得自动进入打包，不得 git push。
