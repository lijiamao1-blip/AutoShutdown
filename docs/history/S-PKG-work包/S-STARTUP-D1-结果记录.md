# S-STARTUP-D1 结果记录

> 阶段：S-STARTUP-D1 / S-STARTUP-D1-D1 / S-STARTUP-D1-D2 / S-STARTUP-D1-D3 / S-STARTUP-D1-D4
> 记录日期：2026-08-22
> 验收基线：app 候选源码提交 `d0c01f3` + 测试工具提交 `a795b53`（D3 边界加固提交 `5ea96aa`、D4 删除纪律修正提交 `537b147` 均在 1576391 之上独立追加）
>
> **本文件为补正后的最终结果记录**：在 `1576391`（`S-STARTUP-D1: record implementation and validation results`）所载初版记录的基础上，依总顾问 S-STARTUP-D1 最终复验要求对**最终轮数据根、提交身份、诊断脚本处置、launcher 数量、全量套件数量**等处作了如实更正（D3）；并依总顾问 D4 复验要求对**删除纪律**（junction 链接删除仅经 PowerShell，删除前核对绝对目标）作了如实补正（D4）。更正内容见 §4、§6、§7、§7.5。

## 1. 提交清单（完整哈希）

### 1.1 实现修正序列（S-STARTUP-D1 阶段，应用业务源码）

| 完整哈希 | 说明 |
|---|---|
| `c6e99d0697476bb437b31dedd5877d64b3c31990` | S-STARTUP-D1: fix half-started instance stuck in DI construction holding single-instance ownership |
| `e58d5d49626e6ea96c9f64ac7bd70660d34d6a1c` | S-STARTUP-D1: fix cross-process log-line corruption found during 20-round loop |
| `ce434b5a37e790fb087d5271034c32bdebd5a701` | S-STARTUP-D1-D1: implement five review gaps for startup lifecycle |
| `d0c01f327c8774191388c49d0b260474662b857c` | S-STARTUP-D1-D2: fix remote-start TOCTOU race with atomic publish boundary —— **app 候选源码提交** |

### 1.2 测试工具最小修正（D1-D2 收尾，总顾问 A–D 方案）

| 完整哈希 | 说明 |
|---|---|
| `a795b53d7c0f4cd3c354ca8eac9893ff8285e6a8` | S-STARTUP-D1-D2: UIA test-tool minimal fixes per advisor A-D plan —— **测试工具提交；验收仓库原 HEAD 即 `a795b53…`** |

### 1.3 D3 删除边界加固（S-STARTUP-D1-D3，总顾问最终复验）

| 完整哈希 | 说明 |
|---|---|
| `5ea96aa5c24fb4a3b77f5b0218c90351847d7f0e` | S-STARTUP-D1-D3: harden ASUI3 isolated-root deletion boundary + focused tests（在 `1576391` 之上新增独立提交，不 amend、不重写历史） |

### 1.4 D4 删除纪律修正（S-STARTUP-D1-D4，总顾问 D4 复验）

| 完整哈希 | 说明 |
|---|---|
| `537b1470f2d3fbfa57be374bd95dc451a603e858` | S-STARTUP-D1-D4: PowerShell-only junction-link deletion + guarded cleanup（在 `5ea96aa` 之上独立提交，不 amend、不重写历史） |

### 1.5 结果记录（本文件）

| 完整哈希 | 说明 |
|---|---|
| `1576391421b2ee2e27afaab28232d41b5a245732` | S-STARTUP-D1: record implementation and validation results —— 初版结果记录提交；**本补正文件即对其更正，最终数据根/提交身份/诊断脚本处置以本文件为准** |
| 见 `git log -1 --format=%H` | 补正后的最终结果记录（本文件所在提交；提交自身的哈希无法写入自身内容，故以 git 记录为准） |

## 2. 最终验收结果

### 2.1 构建

- Release 构建（`AutoShutdown.sln`，App + Tests，TreatWarningsAsErrors）：**0 错误 / 0 警告**，退出码 0。
- `git diff --check`：退出码 **0**（仅 LF→CRLF 归一化提示，无空白错误）。

### 2.2 自动化测试（真实数量，D3 复验后）

| 项 | 真实数量 | 退出/结果 |
|---|---|---|
| 全量套件 | 1767 通过 / 0 失败 / 0 跳过 | PASS |
| S_STARTUP_D1 聚焦（`S_STARTUP_D1_StartupLifecycleTests`） | 16 通过 / 0 失败 | PASS |
| S23 远程聚焦（`S23_CP5_RemoteSectionTests`） | 15 通过 / 0 失败 | PASS |
| S22 task-sync 自动化（Fake + 内存，无真实系统任务写入） | 115 通过 / 0 失败 | PASS |
| S-UI3 launcher 契约（`S_UI3_UiTestLauncherTests`，含 D2 新增 4 项冒烟契约 + D3 新增删除边界契约） | 12 通过 / 0 失败 | PASS |

> 更正说明：初版记录误载「全量 1766」「launcher 11」。D3 新增 `BoundaryModule_EnforcesStrictDeletionGuard` 契约测试后，launcher 契约为 **12** 项、全量套件为 **1767** 项（D3 复验日志见 §7）。

### 2.3 真机 UI

- **UIA 冒烟**（`tools/test/Invoke-ASUI3Smoke.ps1 -Rounds 2`）：**≥2 连续轮全通过**。
  - 最终干净轮 `ui3-smoke-20260822-201225.csv`：pass=50 fail=0 skip=0，rounds=2，**2/2 PASS**。
  - 每轮独立精确数据根、有界条件轮询、关闭到托盘（隐藏+存活）+ 按本轮精确 PID 真实托盘退出、0 残留、正式数据目录前后一致。
  - 不设 `AUTOSHUTDOWN_UI_TEST`（隔离根模式），不共享 UiTestSandbox，不启用共享沙箱 tasks.json。
- **20 轮真机循环**（`tools/test/Invoke-SStartupD1Loop.ps1 -Exe .build-tmp/S-UI3-d0c01f3/AutoShutdown.App.exe -Rounds 20`）：**20/20 PASS**。
  - 无窗口主实例 / `ActivationForwardFailed` / 托盘退出失败 / 残留进程 → 均未出现。
  - 成功清理全部为真实托盘菜单「退出程序」，**未使用强杀/按进程名清理**。
- UIA 冒烟与 20 轮循环均以真实托盘退出作为成功退出路径；临时文件仅经 PowerShell 在已验证精确路径删除（无 Bash/rm）。此「仅经 PowerShell」范围指冒烟/循环的临时数据根清理；D3 边界聚焦测试的 junction 链接清理曾用 `cmd /c rmdir` 与「全部删除仅经 PowerShell」整体表述不一致，已由 D4 修正提交 `537b147` 改为纯 PowerShell 删除（见 §6.4、§7.5）。

### 2.4 S23 flaky 如实记录

- 全量套件本次验收**一次通过**（1767/1767），本次运行未出现 flaky 失败。
- **本记录不声称「flaky 已消除」**：一次全量通过不等于消除 flaky。
- 未对 Core 调度逻辑做任何改动；既往 flaky 的失败日志与「单独复跑 vs 基线」记录如实保留在相应阶段记录中。

## 3. 证据路径（本记录引用的必要证据，均已纳入提交）

- **UIA 冒烟证据目录**：`S-PKG-work包/S-UI3-验收证据/`
  - 逐轮报告 CSV（最终干净轮）：`ui3-smoke-20260822-201225.csv`
  - 逐轮控制台：`ui3-round-1-console.log`、`ui3-round-2-console.log`
  - app 日志：`ui3-round-1-applogs/`、`ui3-round-2-applogs/`
  - 截图证据：`S-UI3-uia-two-tasks-round-1.png`、`S-UI3-uia-two-tasks-round-2.png`
  - 构建日志：`ui3-build-d0c01f3.log`
- **20 轮循环证据**：`S-PKG-work包/S-STARTUP-D1-循环证据/loop-20260822-201322.csv`
- **D3 复验证据目录**：`S-PKG-work包/S-UI3-D3复验证据/`（d3 系列文本日志 + 最终 2 轮 CSV/控制台/app 日志/截图 + 构建日志，清单见 §7）
- 未纳入提交：临时数据根、候选 EXE（`.build-tmp/`）、旧失败轮次产物及重复归档副本。

## 4. 逐轮 PID 与残留（UIA 冒烟，最终干净轮 201225）

> 更正说明：初版记录误载了旧轮（`ui3-smoke-20260822-201103.csv`）的 GUID。下表数据根已按最终干净轮 `ui3-smoke-20260822-201225.csv` 逐字核对修正。

| 轮 | 数据根（隔离） | PID | 停止按钮数 | trayExit 退出码 | residual | formalUnchanged | 结果 |
|---|---|---|---|---|---|---|---|
| 1 | `%TEMP%\as-ui3-round-1-0e5d576da0d14ebf9f3788bf4a216cd2` | 10776 | 2 | 0 | 0 | True | PASS |
| 2 | `%TEMP%\as-ui3-round-2-f2208952017e43c88560af983714e948` | 38312 | 2 | 0 | 0 | True | PASS |

> 注：托盘工具日志「残留进程数(不含目标PID)」为排除已退出目标 PID 进程表回收竞态后的计数；冒烟权威残留计数为 0（`Get-Process -Name AutoShutdown*` 排除目标 PID 后为 0，运行结束后系统实测 0 个 AutoShutdown 进程）。

## 5. 逐轮 PID 与残留（20 轮循环，loop-20260822-201322.csv）

| 轮 | primary_pid | t_window_ms | t_secondary_ms | secondary_exitcode | activated | t_tray_ms | primary_exited | residual | formalUnchanged | result |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 23472 | 1160 | 426 | 0 | TRUE | 3688 | TRUE | 0 | True | PASS |
| 2 | 7748 | 1175 | 426 | 0 | TRUE | 3468 | TRUE | 0 | True | PASS |
| 3 | 42964 | 1375 | 406 | 0 | TRUE | 3465 | TRUE | 0 | True | PASS |
| 4 | 36816 | 1174 | 417 | 0 | TRUE | 3468 | TRUE | 0 | True | PASS |
| 5 | 48072 | 1157 | 422 | 0 | TRUE | 3451 | TRUE | 0 | True | PASS |
| 6 | 38476 | 1142 | 427 | 0 | TRUE | 3471 | TRUE | 0 | True | PASS |
| 7 | 20464 | 1146 | 373 | 0 | TRUE | 3456 | TRUE | 0 | True | PASS |
| 8 | 42052 | 1127 | 384 | 0 | TRUE | 3444 | TRUE | 0 | True | PASS |
| 9 | 18588 | 1156 | 391 | 0 | TRUE | 3455 | TRUE | 0 | True | PASS |
| 10 | 11336 | 1141 | 386 | 0 | TRUE | 3449 | TRUE | 0 | True | PASS |
| 11 | 48296 | 1157 | 388 | 0 | TRUE | 3424 | TRUE | 0 | True | PASS |
| 12 | 26360 | 1145 | 382 | 0 | TRUE | 3445 | TRUE | 0 | True | PASS |
| 13 | 30204 | 1156 | 384 | 0 | TRUE | 3447 | TRUE | 0 | True | PASS |
| 14 | 36000 | 1172 | 401 | 0 | TRUE | 3446 | TRUE | 0 | True | PASS |
| 15 | 36068 | 1154 | 401 | 0 | TRUE | 3454 | TRUE | 0 | True | PASS |
| 16 | 6808 | 1125 | 395 | 0 | TRUE | 3441 | TRUE | 0 | True | PASS |
| 17 | 15148 | 1155 | 377 | 0 | TRUE | 3431 | TRUE | 0 | True | PASS |
| 18 | 21488 | 1125 | 382 | 0 | TRUE | 3434 | TRUE | 0 | True | PASS |
| 19 | 24080 | 1142 | 415 | 0 | TRUE | 3448 | TRUE | 0 | True | PASS |
| 20 | 16492 | 1139 | 376 | 0 | TRUE | 3451 | TRUE | 0 | True | PASS |

> 全部 20 轮 secondary_exitcode=0（副实例转发激活后正常退出）、activated=TRUE（前台激活成功）、primary_exited=TRUE（主实例经真实托盘退出）、residual=0、formalUnchanged=True。每轮日志路径见 CSV（`%TEMP%\as-d1-round-N-<guid>\logs\autoshutdown-*.log`）。

## 6. 测试工具修正范围说明（D1-D2 与 D3）

### 6.1 D1-D2（A–D 方案）

- 修改仅限：`tools/test/Invoke-ASUI3Smoke.ps1`、`tools/test/Invoke-SStartupD1TrayExit.ps1`、`tests/AutoShutdown.Tests/S_UI3_UiTestLauncherTests.cs`、`S-PKG-work包/AutoShutdown-V2-S-STARTUP-D1-最小返修执行书.md`（§9 验收补充）。
- **未修改任何应用业务源码**；启动器 `tools/Start-AutoShutdownUiTest.ps1` 保持只读。

### 6.2 D3（删除边界加固）

- 新增共享边界模块 `tools/test/ASUI3IsolatedRootCleanup.ps1`，`Invoke-ASUI3Smoke.ps1` 的隔离根清理改经该模块逐项校验（直接子目录 / 名称模式 / 非受保护根 / 无 reparse point / 删除前复检）；删除边界无法确认时保留目录并判本轮 FAIL。
- 新增聚焦测试 `tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1`（D3 起 30 项真实文件系统断言；D4 起 34 项）。
- 更新 `tests/AutoShutdown.Tests/S_UI3_UiTestLauncherTests.cs` 契约以匹配 D3 边界契约。
- **未修改任何应用业务源码**。

### 6.3 临时诊断脚本处置（如实更正）

> 更正说明：初版记录称「`tools/test/diag-btns.ps1`、`tools/test/diag-fit.ps1` 未进入任何提交，且已按 D 要求经 PowerShell 在已验证精确路径上删除」。此为**误述**。
> 实情：`diag-btns.ps1` / `diag-fit.ps1` 在阶段开始前即以**未跟踪文件**存在于工作区，属前置遗留诊断脚本；阶段过程中被**误删**，现工作区中已不存在，且因其从未被 Git 跟踪，**无法从版本历史恢复**。
> 因此如实记为：**阶段误删的前置未跟踪诊断脚本**；不得视为「原样保留 / 未改动」，其内容亦无法还原，故不声称任何内容。此更正不影响测试工具或应用源码的任何提交内容。

### 6.4 D4（删除纪律修正）

- D3 边界聚焦测试 `tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1` 的 junction 链接清理曾使用 `& cmd /c rmdir $junc`（非 PowerShell 删除），与「全部删除仅经 PowerShell」的整体表述不一致。D4 修正提交 `537b147` 将其替换为纯 PowerShell 的 `DirectoryInfo.Delete()`（非递归，只删链接不删目标），并在删除前逐项核对（绝对路径 / 本测试登记精确链接路径 / 目录名严格匹配 `^as-ui3-round-\d+-[0-9a-f]{32}$` / Get-Item 显示 ReparsePoint / 解析目标内哨兵存在）、删除后核对（链接不存在、目标目录与哨兵仍存在）。
- junction 创建仍允许 `cmd /c mklink /J`（创建操作，D4 不禁止）；测试自身其余递归清理一律在删除前核对绝对目标处于本轮精确测试 sandbox 内或已登记的临时测试路径后才递归删除（`Remove-ASUI3BoundaryCleanupTarget`）。
- 修改仅限：`tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1`（新增 `Remove-ASUI3JunctionLink`、`Remove-ASUI3BoundaryCleanupTarget`，case 8 与最终清理改经 D4 路径）。
- **未修改任何应用业务源码**。

## 7. D3 复验结果（总顾问最终复验 三）

复验均在 `5ea96aa`（D3 加固提交）基础上进行；全部证据见 `S-PKG-work包/S-UI3-D3复验证据/`。

| 复验项 | 命令/范围 | 真实数量 | 结果 |
|---|---|---|---|
| 删除边界聚焦测试 | `Invoke-ASUI3RemoveBoundaryTests.ps1` | 30 通过 / 0 失败 | PASS（`d3-remove-boundary-tests.log`） |
| S-UI3 launcher 契约 | `FullyQualifiedName~S_UI3_UiTestLauncherTests` | 12 通过 / 0 失败 | PASS（`d3-launcher-tests.log`） |
| 全量套件 | `dotnet test`（Release） | 1767 通过 / 0 失败 | PASS（`d3-full-tests.log`） |
| S_STARTUP_D1 聚焦 | `FullyQualifiedName~S_STARTUP_D1_StartupLifecycleTests` | 16 通过 / 0 失败 | PASS（`d3-focus-startupd1.log`） |
| S23 远程聚焦 | `FullyQualifiedName~S23_CP5_RemoteSectionTests` | 15 通过 / 0 失败 | PASS（`d3-focus-s23.log`） |
| S22 task-sync | `FullyQualifiedName~S22_` | 115 通过 / 0 失败 | PASS（`d3-focus-s22-tasksync.log`） |
| git diff --check | 工作区 | 退出码 0 | PASS（`d3-git-diff-check.log`） |
| UIA 冒烟 ≥2 连续轮 | `Invoke-ASUI3Smoke.ps1 -Rounds 2`（D3 边界清理路径） | pass=52 fail=0 skip=0，rounds=2，**2/2 PASS** | PASS（`d3-smoke-run.log` + `ui3-smoke-20260822-204438.csv`） |

- D3 冒烟最终两轮数据根（`ui3-smoke-20260822-204438.csv`）：round 1 `%TEMP%\as-ui3-round-1-29f12c1ee7f846d5a65830e738dc3f9e`、round 2 `%TEMP%\as-ui3-round-2-8b96322ba99b4fcca3c7a10bbdfdbd57`；round 2 PID=27060，trayExit=0，residual=0，formalUnchanged=True。两轮隔离根均在边界确认后清理（`D: 隔离根删除边界确认并清理` PASS，status=Deleted）。
- 正式数据目录 `%LOCALAPPDATA%\AutoShutdown` 前后快照一致（formalUnchanged=True），实测 0 个 AutoShutdown 进程残留。
- D3 复验证据文件：`d3-smoke-run.log`、`ui3-smoke-20260822-204438.csv`、`ui3-round-{1,2}-console.log`、`ui3-round-{1,2}-applogs/`、`S-UI3-uia-two-tasks-round-{1,2}.png`、`ui3-build-1576391.log`、`d3-remove-boundary-tests.log`、`d3-launcher-tests.log`、`d3-full-tests.log`、`d3-focus-startupd1.log`、`d3-focus-s23.log`、`d3-focus-s22-tasksync.log`、`d3-git-diff-check.log`。

### 7.5 D4 复验结果（总顾问 D4 复验）

复验均在 `537b147`（D4 修正提交）基础上进行；全部证据见 `S-PKG-work包/S-UI3-D4复验证据/`。

| 复验项 | 命令/范围 | 真实数量 | 结果 |
|---|---|---|---|
| 删除边界聚焦测试 | `Invoke-ASUI3RemoveBoundaryTests.ps1` | 34 通过 / 0 失败 | PASS（`d4-remove-boundary-tests.log`；运行后系统临时目录实测无 `as-ui3-*` 残留） |
| S-UI3 launcher 契约 | `FullyQualifiedName~S_UI3_UiTestLauncherTests` | 12 通过 / 0 失败 | PASS（`d4-launcher-tests.log`） |
| git diff --check | 工作区 | 退出码 0 | PASS（`d4-git-diff-check.log`，仅 LF→CRLF 归一化提示，无空白错误） |

- D4 删除纪律要点：junction 创建仍用 `cmd /c mklink /J`（创建操作，允许）；junction 链接删除仅经 PowerShell `DirectoryInfo.Delete()`（删除前核对绝对路径 / 登记精确链接路径 / 名称严格匹配 / ReparsePoint / 解析目标哨兵存在，删除后核对链接消失、目标目录与哨兵仍在）。修正后脚本内已无任何 `cmd /c rmdir` 调用。
- D4 复验证据文件：`d4-remove-boundary-tests.log`、`d4-launcher-tests.log`、`d4-git-diff-check.log`。

## 8. 收尾承诺

- 本阶段完成后：不 git push、不打包、不恢复 S-PKG2、不进入其它阶段。
