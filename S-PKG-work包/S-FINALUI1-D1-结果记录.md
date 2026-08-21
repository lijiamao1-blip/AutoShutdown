# AutoShutdown V2 · S-FINALUI1-D1「图标资源链接与首页输入区域宽度正式闭环」独立结果记录

- 阶段: S-FINALUI1-D1（工作树 4 项已授权修改的追溯建档、独立检查、测试、精确提交与候选复验）
- 执行: 本阶段唯一执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根
- 基线 HEAD: `c4c2484d92d4e5890991d079ca7b51ee0b08c3d3`
- 结束 HEAD: `457b4aa`（实现提交）+ 结果记录提交（见阶段报告随附 git log）
- 环境: Windows 11（10.0.26200），.NET 8 `net8.0-windows`

## 0. 追溯建档声明

- 4 项已跟踪修改（csproj 图标资源链接 / MainWindow.xaml 时间输入区 480 固定宽 / S_UI2 首页布局契约断言 / S_UI3 图标资源链接契约测试）在补建执行书之前已由用户本人授权实施；经总顾问裁决确认归属，非未知污染，不得丢弃。
- 本阶段为追溯建档、独立检查、测试与正式提交；**未虚构原始执行时间、原始测试结果或执行者**。本结果记录中的全部构建、测试、冒烟结果均由本阶段唯一执行 AI 于 2026-08-21 实际执行。
- 范围严格限于当前已存在的 4 项已跟踪修改；未顺手修改 Core、排程、Office、电源、远程或安装逻辑（`git diff HEAD --stat -- src/AutoShutdown.Core/` 为空）。
- 新增测试经本阶段独立检查后小幅加强（不改变功能范围）：S_UI3 图标契约断言 Resource Include 恰好 1 条；S_UI2 布局契约断言 `Width="480" HorizontalAlignment="Left">` 恰好 2 处——以满足「新增测试不能只检查字符串存在，应至少验证资源路径和首页布局契约没有重复或冲突」的要求。

## 1. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short` / `git diff --cached` / `git diff --check` / `git log --oneline -12` | master / `c4c2484…` / 4 个已跟踪修改 + 6 张旧截图 + 未跟踪工件 / 暂存区为空 / exit 0（仅 LF→CRLF 提示）/ 12 条 log，全部一致 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release --nologo` | **0 警告 / 0 错误**，exit 0 |
| .NET Release 全量测试 | `dotnet test AutoShutdown.sln -c Release --no-build` | **1746 通过 / 0 失败 / 0 跳过**，exit 0 |
| S_UI2_MultiTaskHomeTests 聚焦 | `--filter FullyQualifiedName~S_UI2_MultiTaskHomeTests` | **48 通过 / 0 失败 / 0 跳过**，exit 0 |
| S_UI3_UiTestLauncherTests 聚焦 | `--filter FullyQualifiedName~S_UI3_UiTestLauncherTests` | **7 通过 / 0 失败 / 0 跳过**，exit 0 |
| 图标资源加载测试（S12.4.3） | `--filter FullyQualifiedName~S12_4_3IconContractTests` | **20 通过 / 0 失败 / 0 跳过**，exit 0 |
| 首页布局契约测试 | `--filter FullyQualifiedName~Homepage` | **10 通过 / 0 失败 / 0 跳过**，exit 0 |
| S-FINALUI1 批量操作聚焦测试 | `--filter BulkSnooze\|BulkStop\|BulkClear\|TaskManagement_ExposesFourSafeBulkActions` | **4 通过 / 0 失败 / 0 跳过**，exit 0 |
| S-CLOSEUI1 聚焦测试 | `--filter S_CLOSEUI1` | **139 通过 / 0 失败 / 0 跳过**，exit 0 |
| S22 自动化测试 | `--filter S22_` | **115 通过 / 0 失败 / 0 跳过**，exit 0 |
| UIA 冒烟 第 1 轮 | `Invoke-ASUI2Smoke.ps1 -ReleaseExe S-UI2-457b4aa 候选 -EvidenceDir S-FINALUI1-D1-截图证据` | **75 通过 / 0 失败 / 2 跳过**，exit 0 |
| UIA 冒烟 第 2 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0（与第 1 轮连续通过） |
| 空白检查 | `git diff --check` | exit 0，无空白错误（仅已跟踪文件的 LF→CRLF 提示） |

冒烟每轮 2 SKIP = DPI 100% / 125% 完整电池在本机（单显示器 150%）无法覆盖，按既有口径列入人工待验。

## 2. 逐项审查结论（独立检查，7 项全部通过）

1. **图标资源链接无重复资源/重复输出/错误路径**：csproj 仅一条 `<Resource Include="..\..\assets\icon.ico">`（加强测试断言恰好 1 条），`<Link>assets\icon.ico</Link>` 归一化项目内路径、`<LogicalName>assets/icon.ico</LogicalName>` 固定清单名，二者一致；Release 构建 0 警告（无重复资源/重复输出/路径警告）；S12.4.3 `BuiltAppDll_GResourcesContainIcon`（`.g.resources` 含 `assets/icon.ico`）与 `BuiltAppExe_EmbedsGroupIconResource`（EXE 含 7 帧 RT_GROUP_ICON）全部通过。
2. **主窗口/提醒窗口/托盘同一图标来源**：`MainWindow.xaml` `Icon="/assets/icon.ico"`、`ReminderWindow.xaml` `Icon="/assets/icon.ico"`、`TrayIconService.cs` `pack://application:,,,/assets/icon.ico` 均指向同一嵌入资源；S12.4.3 `MainAndReminderWindows_UseSameIconResource` / `TrayIconService_UsesEmbeddedIcon_NotSystemPlaceholder` 通过。
3. **pack URI 可解析**：候选 EXE 实际启动并加载主窗口（两轮 UIA 均 `window found`），`Icon="/assets/icon.ico"` 在窗口加载时解析成功，无资源解析异常；`.g.resources` 契约测试通过。
4. **固定宽度 480 在常见窗口宽度与 150% DPI 下无横向滚动条/裁切/覆盖**：UIA 冒烟 P2 紧凑 900×580 DIP 档「首页无可滚动面板」/「首页无整页滚动条」/「首页创建任务按钮直接可见」全部通过；150% 首页截图像素复核（1350×870，最小窗口档）：左卡渲染右缘在 x≈859px（预期 861px），间隔窗口渐变在 x=860px 清晰可见，右卡自 x=880px 起无任何被覆盖迹象 → 480 DIP 固定宽时间输入区被约束在左卡内，未溢出到间隔/右列。
5. **首页契约全部满足**：无最外层滚动条、创建区域无内部滚动条、按钮和中文文字不截断、未选中区域 Collapsed、当前任务与最近活动正常显示（S_UI2 首页布局契约 10 项 + 冒烟 P1/P2/P3/P4/P5/P9 通过）。
6. **新增测试不只检查字符串**：S_UI3 图标契约测试额外断言 `<Resource Include="..\..\assets\icon.ico">` 恰好 1 条（无重复资源/重复输出），并断言 Link/LogicalName 路径一致；S_UI2 布局契约测试额外断言 `Width="480" HorizontalAlignment="Left">` 恰好 2 处（时间模式 WrapPanel + 时间输入区 Border，无重复/冲突/误配）。
7. **未修改现有任务、状态机、批量操作或安全边界**：`git diff HEAD --stat -- src/AutoShutdown.Core/` 为空；改动仅限 csproj 资源元数据、一处 XAML 宽度、两处测试断言；Core、排程、Office、电源、远程、任务计划同步、安装安全逻辑均未触碰。

## 3. 精确提交候选验证（全新候选，未复用旧候选）

- 实现提交：`457b4aa6517afc68454ce27bec05eee41be4e580`（短哈希 `457b4aa`）。
- 候选目录：`D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-UI2-457b4aa\`
- 候选 EXE：`AutoShutdown-v2.0.0-S-UI2.457b4aa.exe`（InformationalVersion `v2.0.0-S-UI2.457b4aa`）
- 候选 EXE SHA-256：`BD7E50D4F625FB068684EA058C7593F19399EB5442F2338C71EF4EDAB6ACD995`
- manifest：`artifacts\release\v2.0.0\manifests\S-UI2-457b4aa-candidate.txt`，`source-commit = 457b4aa6517afc68454ce27bec05eee41be4e580`。
- **从干净实现提交构建**：提交实现后源码树与 `457b4aa` 完全一致（`git diff HEAD --stat -- src/ tests/ *.csproj *.xaml` 为空）才发布候选；**未复用、未覆盖 `S-UI2-db16739` 旧候选**；候选目录/EXE 文件名/界面版本/清单/日志全部包含实现提交短哈希 `457b4aa`。
- UIA 冒烟从该精确候选连续两轮 **75/0/2** 通过。

## 4. Windows 任务计划程序同步记录口径

Windows 任务计划程序同步后端/UI/自动化（S22 聚焦 115 项全部通过，全部使用 `AutoShutdown.Core.Tasks.TaskService` + Fake 适配器 + 内存存储，**未写入系统任务计划程序**）；真机创建、更新、删除、触发、权限失败和关闭同步后的清理仍为**人工待验**。本阶段未真实创建/更新/运行/删除系统计划任务。

## 5. 测试日志绝对路径

| 项目 | 日志绝对路径 |
|---|---|
| Release 构建 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\release-build.log` |
| 全量测试 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\full-tests.log` |
| S_UI2 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-s_ui2.log` |
| S_UI3 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-s_ui3.log` |
| 图标资源聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-icon.log` |
| 首页布局聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-homepage-layout.log` |
| 批量操作聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-bulk-actions.log` |
| S-CLOSEUI1 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-closeui1.log` |
| S22 聚焦 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\focus-s22.log` |
| 冒烟 第 1 轮 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\smoke-round1.log` |
| 冒烟 第 2 轮 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\smoke-round2.log` |
| 候选发布 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\publish-candidate.log` |
| 空白检查 | `D:\电脑定时关机重建完整版\.build-tmp\S-FINALUI1-D1-logs\git-diff-check.log` |

截图证据目录：`D:\电脑定时关机重建完整版\S-PKG-work包\S-FINALUI1-D1-截图证据\`（home/weekday/onetime/tasks/office/about 共 6 张 150% PNG，冒烟通过轮次生成）。

## 6. 提交清单

1. **实现提交 `457b4aa`**：4 个已跟踪修改（`AutoShutdown.App.csproj`、`MainWindow.xaml`、`S_UI2_MultiTaskHomeTests.cs`、`S_UI3_UiTestLauncherTests.cs`）+ `AutoShutdown-V2-S-FINALUI1-D1-最小返修执行书.md`。
2. **结果记录提交**：`S-FINALUI1-D1-结果记录.md`（本文件）+ `S-PKG-work包/S-FINALUI1-D1-截图证据/`（6 张 PNG）。
3. **未提交**：六张已跟踪修改的旧 S-UI2 截图（`S-UI2-截图证据/*-150percent.png`）、LibreOffice MSI、历史工作包、S-UI2-D1 未闭环证据、`diag-btns.ps1`、`diag-fit.ps1`、`_*_extracted.txt`、stash 内容、候选二进制（`artifacts/` 未跟踪）、其他无关未跟踪工件。均未删除、未移动、未覆盖。

## 7. 最终 git status

（结果记录提交后附快照，见阶段报告。）

## 8. 后续

完成结果记录提交后立即停止，等待总顾问独立复验。未 `git push`、未自动进入打包。只有总顾问确认 S-FINALUI1-D1 通过并指定最终打包 HEAD 后，才能开始正式打包。
