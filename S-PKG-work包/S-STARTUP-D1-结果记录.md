# S-STARTUP-D1 结果记录

> 阶段：S-STARTUP-D1 / S-STARTUP-D1-D1 / S-STARTUP-D1-D2
> 记录日期：2026-08-22
> 验收基线：HEAD `d0c01f3`（含测试工具最小修正提交后单独记录）

## 1. 提交清单（完整哈希）

### 1.1 实现修正序列（S-STARTUP-D1 阶段）

| 完整哈希 | 说明 |
|---|---|
| `c6e99d0697476bb437b31dedd5877d64b3c31990` | S-STARTUP-D1: fix half-started instance stuck in DI construction holding single-instance ownership |
| `e58d5d49626e6ea96c9f64ac7bd70660d34d6a1c` | S-STARTUP-D1: fix cross-process log-line corruption found during 20-round loop |
| `ce434b5a37e790fb087d5271034c32bdebd5a701` | S-STARTUP-D1-D1: implement five review gaps for startup lifecycle |
| `d0c01f327c8774191388c49d0b260474662b857c` | S-STARTUP-D1-D2: fix remote-start TOCTOU race with atomic publish boundary |

### 1.2 测试工具最小修正（D1-D2 收尾，总顾问 A–D 方案）

| 完整哈希 | 说明 |
|---|---|
| `a795b53d7c0f4cd3c354ca8eac9893ff8285e6a8` | S-STARTUP-D1-D2: UIA test-tool minimal fixes per advisor A-D plan |

### 1.3 结果记录（本文件）

| 完整哈希 | 说明 |
|---|---|
| 见 `git log -1 --format=%H` | S-STARTUP-D1-结果记录（本文件所在提交；提交主题「S-STARTUP-D1: record implementation and validation results」。提交自身的哈希无法写入自身内容，故以 git 记录为准） |

## 2. 最终验收结果（HEAD d0c01f3，2026-08-22）

### 2.1 构建

- Release 构建（`AutoShutdown.sln`，App + Tests，TreatWarningsAsErrors）：**0 错误 / 0 警告**，退出码 0。
- `git diff --check`：退出码 **0**（仅 LF→CRLF 归一化提示，无空白错误）。

### 2.2 自动化测试（真实数量）

| 项 | 真实数量 | 退出/结果 |
|---|---|---|
| 全量套件 | 1766 通过 / 0 失败 / 0 跳过 | PASS |
| S_STARTUP_D1 聚焦（`S_STARTUP_D1_StartupLifecycleTests`） | 16 通过 / 0 失败 | PASS |
| S23 远程聚焦（`S23_CP5_RemoteSectionTests`） | 15 通过 / 0 失败 | PASS |
| S22 task-sync 自动化（Fake + 内存，无真实系统任务写入） | 115 通过 / 0 失败 | PASS |
| S-UI3 launcher 契约（`S_UI3_UiTestLauncherTests`，含新增 4 项冒烟契约） | 11 通过 / 0 失败 | PASS |

### 2.3 真机 UI

- **UIA 冒烟**（`tools/test/Invoke-ASUI3Smoke.ps1 -Rounds 2`）：**≥2 连续轮全通过**。
  - 最终干净轮 `ui3-smoke-20260822-201225.csv`：pass=50 fail=0 skip=0，rounds=2，**2/2 PASS**。
  - 此前另一轮（`ui3-smoke-20260822-201103.csv`）同样 2/2 PASS。
  - 每轮独立精确数据根、有界条件轮询、关闭到托盘（隐藏+存活）+ 按本轮精确 PID 真实托盘退出、0 残留、正式数据目录前后一致。
  - 不设 `AUTOSHUTDOWN_UI_TEST`（隔离根模式），不共享 UiTestSandbox，不启用共享沙箱 tasks.json。
- **20 轮真机循环**（`tools/test/Invoke-SStartupD1Loop.ps1 -Exe .build-tmp/S-UI3-d0c01f3/AutoShutdown.App.exe -Rounds 20`）：**20/20 PASS**。
  - 无窗口主实例 / `ActivationForwardFailed` / 托盘退出失败 / 残留进程 → 均未出现。
  - 成功清理全部为真实托盘菜单「退出程序」，**未使用强杀/按进程名清理**。
- UIA 冒烟与 20 轮循环均以真实托盘退出作为成功退出路径；临时文件仅经 PowerShell 在已验证精确路径删除（无 Bash/rm）。

### 2.4 S23 flaky 如实记录

- 全量套件本次验收**一次通过**（1766/1766），本次运行未出现 flaky 失败。
- **本记录不声称「flaky 已消除」**：一次全量通过不等于消除 flaky。
- 未对 Core 调度逻辑做任何改动；既往 flaky 的失败日志与「单独复跑 vs 基线」记录如实保留在相应阶段记录中。

## 3. 证据路径

- **UIA 冒烟证据目录**：`S-PKG-work包/S-UI3-验收证据/`
  - 逐轮报告 CSV（最终干净轮）：`ui3-smoke-20260822-201225.csv`
  - 逐轮控制台：`ui3-round-1-console.log`、`ui3-round-2-console.log`
  - app 日志：`ui3-round-1-applogs/`、`ui3-round-2-applogs/`
  - 截图证据：`S-UI3-uia-two-tasks-round-1.png`、`S-UI3-uia-two-tasks-round-2.png`
  - 构建日志：`ui3-build-d0c01f3.log`
- **20 轮循环证据**：`S-PKG-work包/S-STARTUP-D1-循环证据/loop-20260822-201322.csv`
- **候选 EXE**：`.build-tmp/S-UI3-d0c01f3/AutoShutdown.App.exe`（当前源码 HEAD `d0c01f3` 的 Release 构建）

## 4. 逐轮 PID 与残留（UIA 冒烟，最终干净轮 201225）

| 轮 | 数据根（隔离） | PID | 停止按钮数 | trayExit 退出码 | residual | formalUnchanged | 结果 |
|---|---|---|---|---|---|---|---|
| 1 | `%TEMP%\as-ui3-round-1-84279727962b42b683a531b534ad4078` | 10776 | 2 | 0 | 0 | True | PASS |
| 2 | `%TEMP%\as-ui3-round-2-05efee7c14264bdbb517427b3c10a6be` | 38312 | 2 | 0 | 0 | True | PASS |

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

## 6. 测试工具最小修正范围说明（D1-D2）

- 修改仅限：`tools/test/Invoke-ASUI3Smoke.ps1`、`tools/test/Invoke-SStartupD1TrayExit.ps1`、`tests/AutoShutdown.Tests/S_UI3_UiTestLauncherTests.cs`、`S-PKG-work包/AutoShutdown-V2-S-STARTUP-D1-最小返修执行书.md`（§9 验收补充）。
- **未修改任何应用业务源码**；启动器 `tools/Start-AutoShutdownUiTest.ps1` 保持只读。
- 临时诊断脚本（`tools/test/diag-btns.ps1`、`tools/test/diag-fit.ps1`）未进入任何提交，且已按 D 要求经 PowerShell 在已验证精确路径上删除。
- 本阶段完成后：不 git push、不打包、不恢复 S-PKG2、不进入其它阶段。
