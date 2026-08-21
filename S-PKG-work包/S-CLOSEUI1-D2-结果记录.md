# S-CLOSEUI1-D2「关闭执行端路径规范化一致性」独立结果记录

- 阶段: S-CLOSEUI1-D2（S-CLOSEUI1 关闭执行端路径规范化一致性的最小返修阶段）
- 执行: 独立阶段唯一执行 AI；自动化测试全部使用替身（Fake），绝不枚举/操纵真实进程；无 UIA 冒烟（本阶段不修改 UI）
- 开始 HEAD: `f3de6efbe1592ec0728c93a90207c5a08e633c2d`（只读开头检查一致）
- 结束 HEAD: 本阶段实现提交 + 本结果提交（两个本地提交哈希见 §9）
- 环境: Windows 11 Home China（10.0.26200），.NET 8（SDK 8.0.130 / runtime 8.0.30，`net8.0-windows`）

## 1. 唯一修改目标

S-CLOSEUI1-D1 结果记录 §6「仅报告（未重构）」项：执行层 `CloseAppsService.Resolve`（`src/AutoShutdown.Core/CloseApps/CloseAppsService.cs:144`）以 `OrdinalIgnoreCase` 比较原始路径文本、未再 `GetFullPath` 归一化，与 D1 保存端已写入的规范化完整路径规则不一致。

本阶段将其改为与保存端一致：统一经既有 `ExecutablePathKey.EqualsNormalized`（内部 `Normalize` = `Path.GetFullPath(Trim)`，比较大小写不敏感）匹配。未触碰选择器 UI、进程选择器只读枚举边界、电源边界、状态机、双闸门或 fail-closed。

## 2. 实现改动（仅 1 个源码文件）

- `src/AutoShutdown.Core/CloseApps/CloseAppsService.cs`
  - `Resolve`：`.Where(process => ExecutablePathKey.EqualsNormalized(process.ExecutablePath, path))`，替换原 `string.Equals(process.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)`。
  - 类文档与行内注释如实说明：任一侧空/非法/含 NUL/无法规范化 fail-closed；绝不按进程名/文件名/前缀兜底、不回退 PID；规范化只委托既有 `Path.GetFullPath`（含其在 Windows 上对真实存在 8.3 短名段的展开），不做任何手工字符串替换或 8.3 展开。

## 3. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short --untracked-files=all` / `git log --oneline -8` / `git diff --check` | master / `f3de6ef` 一致 / 全量清单核对 / 最近 8 条确认 / exit 0 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release` | **0 警告、0 错误，exit 0** |
| .NET Release 全量测试 | `dotnet test AutoShutdown.sln -c Release` | **1698 通过 / 0 失败 / 0 跳过** |
| D2 聚焦测试 | `dotnet test … --filter FullyQualifiedName~S_CLOSEUI1_D2` | **26 通过 / 0 失败 / 0 跳过** |
| CloseApps 既有相关测试 | `--filter FullyQualifiedName~S18_CloseApps\|FullyQualifiedName~S_CLOSEUI1` | **160 通过 / 0 失败 / 0 跳过** |
| 安全源码契约测试 | `--filter FullyQualifiedName~SourceContractTests\|FullyQualifiedName~SafetyE2E` | **79 通过 / 0 失败 / 0 跳过** |
| 空白检查 | `git diff --check` | exit 0，无空白错误（仅历史 `Invoke-ASUI2Smoke.ps1` 的 LF→CRLF 提示，非本阶段文件） |

D2 聚焦测试明细（3 类 26 项）：
- `S_CLOSEUI1_D2_CloseAppsPathMatchTests`（16 项，服务级）：完全一致、大小写、`.`、`..`、正反分隔符、首尾空白、完全不同路径不匹配、同名异目录不匹配、前缀不匹配、空进程路径不匹配、NUL 路径不匹配、规范化失败不按进程名兜底、多实例只返回规范化精确相等者、PID+启动时间复核、强杀默认 false 与执行条件、已授权强杀执行条件。
- `S_CLOSEUI1_D2_ExecutablePathKeyNormalizationTests`（7 项，键级真实行为）：正斜杠→反斜杠、组合等价、重复分隔符折叠、末段尾点去除、空/空白任一侧、NUL 任一侧、真实 8.3 短名段展开。
- `S_CLOSEUI1_D2_SourceContractTests`（3 项）：`Resolve` 只经 `EqualsNormalized` 且旧直比移除、`ServiceRegistration` 固定顺序（注释 + `new OfficeSaveAction < new RunCommandsAction < new CloseAppsAction`）、CloseApps 无电源依赖且 `ShutdownWorkflow` 引用 `IPowerService`。

## 4. 测试覆盖对照（17 项必测点 + 向后兼容）

| # | 必测点 | 落实用例 |
|---|---|---|
| 1 | 目标与进程完全一致时匹配 | `ExactAbsolutePath_OldStyleConfig_StillMatches` |
| 2 | 仅大小写不同时匹配 | `CaseOnlyDifference_Matches` |
| 3 | 安全等价 `.` 段匹配 | `DotSegment_Matches` |
| 4 | 安全等价 `..` 段匹配 | `DotDotSegment_Matches` |
| 5 | 正反分隔符按 Windows 规则正确处理 | `ForwardSeparators_OnThisWindows_GetFullPathNormalizes_Matches`（真实 `GetFullPath` 归一） |
| 6 | 首尾空白经规范化正确处理 | `TargetWithSurroundingWhitespace_Matches` + 键级组合用例 |
| 7 | 完全不同完整路径绝不匹配 | `CompletelyDifferentAbsolutePath_NeverMatches` |
| 8 | 仅文件名相同、目录不同绝不匹配 | `SameFileName_DifferentDirectory_NeverMatches` |
| 9 | 路径前缀相同绝不匹配 | `PrefixOnly_NeverMatches` |
| 10 | 任一侧为空不匹配 | `NullProcessExecutablePath_NeverMatches` + 键级空/空白用例 |
| 11 | 任一侧非法、含 NUL 不匹配 | `UnnormalizableProcessPath_WithNul_NeverMatches` + 键级 NUL 用例 |
| 12 | 规范化失败不按进程名兜底 | `UnnormalizablePath_WithMatchingProcessName_NoNameFallback` |
| 13 | 多进程只返回规范化路径精确相等实例 | `MultipleProcesses_OnlyNormalizedExactPathInstances_AreReturned`（{100,200} 命中、300 不触碰） |
| 14 | 匹配后 PID+启动时间复核不变 | `MatchedCandidate_StartTimeRecheck_StillRefusesPidReuse`（StartTime 不符 → PidReuseDetected） |
| 15 | 强杀授权默认值与执行条件不变 | `ForceKillAuthorization_DefaultFalse_AndConditionUnchanged` + `…_True_StillForceKillsOnWindowTimeout` |
| 16 | `OfficeSave→RunCommands→CloseApps` 顺序不变 | `ServiceRegistration_Order_StillOfficeSaveRunCommandsCloseApps` |
| 17 | `ShutdownWorkflow` 唯一电源出口不变 | `CloseAppsSources_HaveNoPowerService_AndShutdownWorkflowIsUniqueExit` |
| 18 | 旧配置合法绝对路径向后兼容 | `ExactAbsolutePath_OldStyleConfig_StillMatches`（既有绝对路径幂等） |

## 5. `Path.GetFullPath` 在当前 Windows 环境的真实结果（探针实测，.NET 8.0.30）

隔离探针 `.build-tmp/path-probe`（gitignore，不入基线），输入 → 输出：

| 输入 | 输出 |
|---|---|
| `C:\Windows\System32\notepad.exe` | 原样 |
| `c:\windows\system32\notepad.EXE` | 大小写保留（比较大小写不敏感，等价） |
| `C:\Windows\System32\.\notepad.exe` | `C:\Windows\System32\notepad.exe`（`.` 折叠） |
| `C:\Windows\System32\..\System32\notepad.exe` | `C:\Windows\System32\notepad.exe`（`..` 折叠） |
| `C:/Windows/System32/notepad.exe` | `C:\Windows\System32\notepad.exe`（正斜杠→反斜杠） |
| `C:/Windows\System32/notepad.exe` | `C:\Windows\System32\notepad.exe`（混合分隔符归一） |
| `   C:\Windows\System32\notepad.exe   ` | 首尾空白裁剪 |
| `C:\Windows\System32\notepad.` | `C:\Windows\System32\notepad`（末段尾点去除） |
| `C:\Windows\System32\notepad.exe.` | `C:\Windows\System32\notepad.exe`（带扩展名尾点同样去除） |
| `C:\Windows\\System32\\notepad.exe` | `C:\Windows\System32\notepad.exe`（重复分隔符折叠） |
| `C:\bad\notepad\0x\exe`（含 NUL） | `ArgumentException` → `Normalize` 返回 null → fail-closed |
| `C:\Windows\bad\|notepad.exe`（含管道符） | `GetFullPath` 不拒绝（.NET 不做 Win32 设备名级校验；文本保留） |
| `notepad.exe`（单段相对） | 按进程 CWD 解析为绝对路径 |
| `C:\Temp\..\Windows\System32\notepad.exe` | `C:\Windows\System32\notepad.exe`（跨目录 `..` 折叠） |
| `C:\PROGRA~1\App\app.exe` | **`C:\Program Files\App\app.exe`（真实存在的 8.3 短名段被展开）** |

结论：正反目录分隔符差异由既有 `Path.GetFullPath` 自身按 Windows 规则归一，无需（也未做）任何手工字符串替换；含 NUL 路径 fail-closed；相对路径按进程 CWD 解析（本阶段仅承诺绝对路径向后兼容，相对路径依赖 CWD 属环境相关注意项）。

## 6. 8.3 短路径限制的实际核实（重要修正）

本阶段对既有 `ExecutablePathKey.Normalize` 的真实行为做了实证：在 Windows 11 / .NET 8.0.30 上，`Path.GetFullPath` 会调用 Win32 把**真实存在的 8.3 短名段**展开为长名（实测 `C:\PROGRA~1\App\app.exe` → `C:\Program Files\App\app.exe`，`EqualsNormalized` 返回 True）。因此：

- 保存端（D1）与执行端（D2）统一使用同一既有函数，8.3 与长路径的等价性两端自动一致；原 D1 假设「8.3 与长路径必然匹配不到」在当前环境**不成立**。
- 本阶段**未**新增任何手工 8.3 展开——此行为完全来自既有 `Path.GetFullPath`。
- 依赖提醒：此展开仅对**真实存在**的短名段生效（Win32 按目录表查询），且受卷的 8.3 生成开关影响；不存在/无法展开的短名段仍保持文本原样比较（fail-closed）。该行为随 .NET/Windows 版本变化，不做承诺。

## 7. 安全边界核对结果

- `ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口；CloseApps 全部源码（`CloseApps/` 目录 11 个文件）无 `IPowerService`/`PowerRequest`（契约测试确认）。
- `OfficeSave → RunCommands → CloseApps` 固定顺序不变（注释标记 + 动作链相对顺序双重断言）。
- 冻结状态机、双闸门（`TestMode=true` + 隔离数据根）、fail-closed 全部未改动。
- 关闭前仍按既有逻辑重新读取 PID + 启动时间防 PID 复用（用例 `MatchedCandidate_StartTimeRecheck_StillRefusesPidReuse`）。
- 正常关闭（WM_CLOSE/CloseMainWindow 优先）与强制结束逻辑不变；强杀仍逐目标显式授权，默认 false（用例覆盖）。
- 进程选择器只读枚举边界未改动；选择器 UI（筛选按钮、摘要窗、重选流程）未重做、未调整。
- 全程无 taskkill / Stop-Process / wmic terminate / 计划任务 / 提权 / 网络；无真实电源操作；测试不触碰真实进程。
- 本阶段不运行完整 UIA（不修改 UI）；未为 UIA 强杀任何现有进程。

## 8. 最终工作树状态（结果提交后）

由阶段报告随附提交后 `git status --short` 快照（见 §9 之后的最终状态）。仅新增本阶段 1 个源码文件改动 + 1 个新测试文件 + 2 份文书；前序未提交截图、`Invoke-ASUI2Smoke.ps1`、诊断脚本、历史工作包、LibreOffice MSI 全部原样保留。

## 9. 两个本地提交

1. **实现提交**：`CloseAppsService.cs`（Resolve 改 `EqualsNormalized`）+ 新测试 `S_CLOSEUI1_D2_CloseAppsPathMatchTests.cs` + 本阶段执行书。
2. **结果提交**：本结果记录。

（提交哈希见阶段报告随附 git log。）

## 10. 待总顾问独立验收项

- 执行端 `Resolve` 现已与保存端共用 `ExecutablePathKey.EqualsNormalized`，两端规则一致。
- §6 的 8.3 实证结论（修正原假设）请总顾问复核。
- §5 的相对路径按进程 CWD 解析为环境相关注意项（旧配置合法绝对路径不受影响）。
