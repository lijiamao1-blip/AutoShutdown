# S-CLOSEUI1-D3「执行端拒绝相对路径 + 8.3 测试环境隔离」独立结果记录

- 阶段: S-CLOSEUI1-D3（S-CLOSEUI1 关闭执行端拒绝相对路径 + 8.3 测试环境隔离的最小返修阶段）
- 执行: 独立阶段唯一执行 AI；自动化测试全部使用替身（Fake），绝不枚举/操纵真实进程；无 UIA 冒烟（本阶段不修改 UI）
- 开始 HEAD: `8b5dfa45a2fc5c0c261778d6e30dfe58039b60d6`（只读开头检查一致）
- 结束 HEAD: 本阶段实现提交 + 本结果提交（两个本地提交哈希见 §9 与阶段报告）
- 环境: Windows 11 Home China（10.0.26200），.NET 8（SDK 8.0.130 / runtime 8.0.30，`net8.0-windows`）

## 1. 唯一修改目标

1. **执行端相对路径风险**：`ExecutablePathKey.Normalize` 可接受相对路径并通过 `Path.GetFullPath` 按当前工作目录转绝对路径；`CloseAppsTargetList.Validate` 只检查非空、不保证完整绝对路径。D2 的 `CloseAppsService.Resolve` 改用 `EqualsNormalized` 后，旧配置/损坏配置中的 `notepad.exe`、`.\app.exe`、`folder\app.exe` 可能按 AutoShutdown 当前工作目录解析并与真实进程路径匹配，违反执行端 fail-closed 原则。
2. **D2 8.3 短路径测试环境依赖**：`EqualsNormalized_ExistingShort8Dot3Segment_ExpandedByGetFullPath_OnThisWindows` 无条件断言 `C:\PROGRA~1\App\app.exe` 与 `C:\Program Files\App\app.exe` 必然规范化相等；该行为依赖当前卷是否生成 8.3 短名、目标短名别名是否真实存在、Windows/.NET 运行环境，可能在其他正常 Windows 环境误失败。

## 2. 实现改动（3 个源码文件 + 2 个测试文件）

- `src/AutoShutdown.Core/CloseApps/ExecutablePathKey.cs`
  - 新增 `EqualsNormalizedAbsolute(string?, string?)`：先用 `Path.IsPathFullyQualified` 检查两侧原始去空白文本（拒绝相对/drive-relative/根相对路径），两侧均为完整绝对路径后才委托既有 `EqualsNormalized` 做规范化大小写不敏感比较；任一侧 null/空白/相对/无法规范化（含 NUL）都 fail-closed 返回 false。
  - **未改变既有 `EqualsNormalized` / `Normalize` 的公共语义**（D1 搜索、去重、已添加判断、重选、保存行为不受影响；新增方法仅供执行端路径匹配使用）。
- `src/AutoShutdown.Core/CloseApps/CloseAppsService.cs`
  - `Resolve` 路径匹配由 `EqualsNormalized(process.ExecutablePath, path)` 改为 `EqualsNormalizedAbsolute(process.ExecutablePath, path)`：目标路径与进程枚举路径都必须先过完整绝对路径门槛。
- `src/AutoShutdown.Core/CloseApps/CloseAppsTargetList.cs`
  - `Validate`：路径目标先 `Path.IsPathFullyQualified`（相对/drive-relative/根相对 → 报 `must be a fully qualified absolute path`），再 `ExecutablePathKey.Normalize` 非空（含 NUL/非法字符 → 报 `must be a valid absolute path`）。完整绝对路径旧格式继续兼容；PID-only 语义不变；路径+PID 并存仍被拒。
- `tests/AutoShutdown.Tests/S_CLOSEUI1_D2_CloseAppsPathMatchTests.cs`
  - 8.3 测试改为环境感知 `[SkippableFact]`：只读 `GetShortPathName` 探测真实「短/长路径」对，只有真实拿到对才断言现有规范化函数与 `GetFullPath` 真实结果一致；拿不到则明确 SKIP 并写明原因。绝不为测试创建目录/系统目录/管理员资源，不修改注册表或卷配置。
  - 源码契约断言由 `EqualsNormalized(process.ExecutablePath, path)` 更新为 `EqualsNormalizedAbsolute(process.ExecutablePath, path)`，并断言旧直比已移除、`// S-CLOSEUI1-D3` 标记存在。
- `tests/AutoShutdown.Tests/S_CLOSEUI1_D3_CloseAppsAbsolutePathGateTests.cs`（新增，40 项）
  - 配置校验 11 项、执行端 12 项、键级 11 项、源码契约 6 项。

## 3. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short --untracked-files=all` / `git log --oneline -8` / `git diff --check` | master / `8b5dfa4` 一致 / 全量清单核对 / 最近 8 条确认 / exit 0 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release` | **0 警告、0 错误，exit 0** |
| .NET Release 全量测试 | `dotnet test AutoShutdown.sln -c Release` | **1738 通过 / 0 失败 / 0 跳过** |
| D3 聚焦测试 | `--filter FullyQualifiedName~S_CLOSEUI1_D3` | **40 通过 / 0 失败 / 0 跳过** |
| D2 相关回归测试 | `--filter FullyQualifiedName~S_CLOSEUI1_D2` | **26 通过 / 0 失败 / 0 跳过**（含修订 8.3 测试，本机 PASS 非 SKIP） |
| CloseApps 全部相关测试 | `--filter FullyQualifiedName~S18_CloseApps\|FullyQualifiedName~S_CLOSEUI1` | **200 通过 / 0 失败 / 0 跳过** |
| 配置校验相关测试 | `--filter FullyQualifiedName~S18_CloseAppsConfig\|FullyQualifiedName~S_CLOSEUI1_D3_CloseAppsConfigAbsolutePathTests` | **26 通过 / 0 失败 / 0 跳过** |
| 安全源码契约测试 | `--filter FullyQualifiedName~SourceContractTests\|FullyQualifiedName~SafetyE2E` | **85 通过 / 0 失败 / 0 跳过** |
| 空白检查 | `git diff --check` | exit 0，无空白错误（仅历史 `Invoke-ASUI2Smoke.ps1` 及本阶段文件的 LF→CRLF 提示，非错误） |

全量 1738 = D2 基线 1698 + D3 新增 40。D3 聚焦 40 项明细：配置校验 11、执行端 12、键级 11、源码契约 6。

## 4. 相对路径拒绝证据（探针实测 + 用例）

隔离探针 `.build-tmp/path-probe`（gitignore，不入基线），输入 → 输出：

| 输入 | 输出 |
|---|---|
| `Path.GetFullPath("notepad.exe")` | `D:\电脑定时关机重建完整版\.build-tmp\path-probe\notepad.exe`（按当前 CWD 解析） |
| `Path.IsPathFullyQualified("notepad.exe")` | False |
| `Path.IsPathFullyQualified("C:app.exe")` | False（drive-relative） |
| `Path.IsPathFullyQualified(@"\app.exe")` | False（根相对缺卷标） |
| `Path.IsPathFullyQualified(@".\app.exe")` | False（当前目录相对） |
| `Path.IsPathFullyQualified(@"..\app.exe")` | False（父目录相对） |
| `Path.IsPathFullyQualified(@"folder\app.exe")` | False |
| 旧 `EqualsNormalized("notepad.exe", CWD解析结果)` | **True**（漏洞场景：相对目标会匹配） |
| 新 `EqualsNormalizedAbsolute("notepad.exe", CWD解析结果)` | **False**（门槛拒绝） |
| 新 `EqualsNormalizedAbsolute(绝对路径, 大小写不同绝对路径)` | True（大小写与安全文本差异仍匹配） |

用例覆盖：`RelativeTarget_ConfigRejected_FailsClosed_EvenIfCwdResolvesToRunningPath`（配置校验阶段 fail-closed，绝不把相对路径转成可关闭目标）、`RelativeProcessPath_EvenIfCwdResolvesToTarget_NeverMatches`、键级 `RelativeTarget_EvenIfCwdResolvesToSameAbsolutePath_NeverMatches`（直接对照旧 True / 新 False）、`DriveRelativeTarget_ConfigRejected_FailsClosed`、`DriveRelativeProcessPath_NeverMatches`、`UnnormalizableTarget_ConfigRejected_FailsClosed`、`UnnormalizableProcessPath_WithNul_EvenIfFullyQualified_NeverMatches`、`RelativeProcessPath_WithMatchingProcessName_NoNameFallback`、`PathTarget_NeverFallsBackToPid`。

## 5. 8.3 短路径测试结果（本机 PASS；逻辑为环境感知）

- 只读 `GetShortPathName` 探测本机：`C:\Program Files` → `C:\PROGRA~1`、`C:\ProgramData` → `C:\PROGRA~3`（真实短名别名存在）；`C:\Windows\System32`、`C:\Windows`、`C:\Users` 无短名。`fsutil 8dot3name query C:` 因权限拒绝（未提权，也未尝试创建/修改注册表或卷配置）。
- `Path.GetFullPath(@"C:\PROGRA~1\app.exe")` → `C:\Program Files\app.exe`；`EqualsNormalized` 对真实短/长对返回 True。测试 `EqualsNormalized_RealShortAndLongAlias_ObservesGetFullPathBehavior_OnThisEnvironment` 拿到真实「短/长」对并断言现有规范化函数与 `GetFullPath` 真实结果一致 → **PASS**（未 SKIP）。
- 在不启用 8.3 短名的环境，该测试会拿不到真实别名对 → 明确 SKIP 并写明原因（Xunit.SkippableFact），绝不假通过。
- 行为书面表述：**「环境相关能力；能真实解析时按现有 Windows/.NET 行为处理，不能解析时安全不匹配，不作为跨环境承诺。」**
- 无论 PASS/SKIP，源码契约测试 `ProductionCloseAppsSources_NoManual8Dot3Expansion_NoNameOrPidFallback` 已验证生产 CloseApps 代码无 `GetShortPathName`/`GetLongPathName`/`fsutil`/`DllImport`、无产品名/公司名/窗口标题兜底、无 taskkill/Stop-Process/wmic——生产代码没有任何手工 8.3 展开或名称兜底。

## 6. 测试覆盖对照（本阶段要求）

| # | 必测点 | 落实用例 |
|---|---|---|
| 配置 | 完整绝对路径通过 | `FullAbsolutePath_IsValid` |
| 配置 | `app.exe` 被拒绝 | `RelativeOrPartiallyQualifiedPath_IsRejected`（Theory） |
| 配置 | `.\app.exe` 被拒绝 | 同上 |
| 配置 | `..\app.exe` 被拒绝 | 同上 |
| 配置 | `folder\app.exe` 被拒绝 | 同上 |
| 配置 | `C:app.exe` 被拒绝 | 同上 |
| 配置 | 空/空白路径保持既有拒绝语义 | `EmptyPath_KeepsExistingRejectionSemantics` + `WhitespaceOnlyPath_KeepsExistingRejectionSemantics` |
| 配置 | PID-only 合法历史目标兼容 | `PidOnlyLegacyTarget_RemainsValid` |
| 配置 | 路径与 PID 并存仍被拒绝 | `BothPathAndPid_StillRejected` |
| 执行 | 绝对目标+绝对进程路径匹配 | `AbsoluteTargetAndAbsoluteProcessPath_Match` |
| 执行 | 相对目标按 CWD 可解析到同一文件也绝不匹配 | `RelativeTarget_ConfigRejected_FailsClosed_EvenIfCwdResolvesToRunningPath` + 键级 `RelativeTarget_EvenIfCwdResolvesToSameAbsolutePath_NeverMatches` |
| 执行 | 相对进程路径按 CWD 可解析到目标也绝不匹配 | `RelativeProcessPath_EvenIfCwdResolvesToTarget_NeverMatches` |
| 执行 | drive-relative 目标绝不匹配 | `DriveRelativeTarget_ConfigRejected_FailsClosed` + 键级 `AnySideNotFullyQualified_NeverMatches` |
| 执行 | drive-relative 进程路径绝不匹配 | `DriveRelativeProcessPath_NeverMatches` |
| 执行 | 任一侧规范化失败不匹配 | `UnnormalizableTarget_ConfigRejected_FailsClosed` + `UnnormalizableProcessPath_WithNul_EvenIfFullyQualified_NeverMatches` + 键级 NUL 用例 |
| 执行 | 不按进程名兜底 | `RelativeProcessPath_WithMatchingProcessName_NoNameFallback` + 源码契约无 ProductName/CompanyName/WindowTitle |
| 执行 | 不回退 PID | `PathTarget_NeverFallsBackToPid` + 源码契约路径分支只经 `EqualsNormalizedAbsolute` |
| 执行 | 大小写和安全绝对路径文本差异仍按 D2 规则匹配 | `CaseAndSafeTextDifferences_OnAbsolutePaths_StillMatch` + 键级 `AbsolutePaths_WithCaseAndSafeTextVariants_StillMatch` |
| 执行 | PID+启动时间复核不变 | `MatchedCandidate_PidStartTimeRecheck_StillRefusesPidReuse` |
| 执行 | 强杀默认关闭及显式授权条件不变 | `ForceKill_DefaultFalse_AndExplicitAuthorization_Unchanged` |
| 8.3 | 真实存在短路径别名时验证当前环境行为 | `EqualsNormalized_RealShortAndLongAlias_ObservesGetFullPathBehavior_OnThisEnvironment`（本机 PASS） |
| 8.3 | 不存在别名时明确 SKIP | 同一测试的 `Skip.If(true, …)` 分支 |
| 8.3 | 不得用不存在的硬编码假路径制造假证据 | 已移除硬编码 `C:\PROGRA~1\App\app.exe`，改为只读真实探测 |
| 8.3 | 无论 SKIP 与否验证生产代码无手工 8.3 展开/名称兜底 | `ProductionCloseAppsSources_NoManual8Dot3Expansion_NoNameOrPidFallback` |
| 契约 | `CloseAppsService` 执行匹配前存在完整绝对路径门槛 | `CloseAppsService_HasAbsolutePathGate_BeforeNormalizedMatch` |
| 契约 | `CloseAppsTargetList.Validate` 拒绝相对路径目标 | `CloseAppsTargetList_Validate_RejectsRelativePathTargets` |
| 契约 | `OfficeSave→RunCommands→CloseApps` 顺序不变 | `ServiceRegistration_Order_StillOfficeSaveRunCommandsCloseApps` |
| 契约 | `ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口 | `CloseAppsSources_HaveNoPowerService_AndShutdownWorkflowIsUniqueExit` |
| 契约 | CloseApps 不新增网络/shell/提权/计划任务调用 | `CloseAppsSources_HaveNoNewNetworkShellElevationOrTaskScheduler` |

## 7. 安全边界核对结果

- `ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口；CloseApps 全部源码（`CloseApps/` 目录 11 个文件）无 `IPowerService`/`PowerRequest`（契约测试确认）。
- `OfficeSave → RunCommands → CloseApps` 固定顺序不变（注释标记 + 动作链相对顺序双重断言）。
- 冻结状态机、双闸门（`TestMode=true` + 隔离数据根）、fail-closed 全部未改动。
- PID + 启动时间复核不变（`MatchedCandidate_PidStartTimeRecheck_StillRefusesPidReuse`）；强杀仍逐目标显式授权，默认 false（`ForceKill_DefaultFalse_AndExplicitAuthorization_Unchanged`）。
- 进程选择器只读枚举边界未改动；选择器 UI（筛选按钮、摘要窗、重选流程、进程选择器）未重做、未调整。
- `ExecutablePathKey.EqualsNormalized` 公共语义未改动（D1 的搜索、去重、已添加判断、重选、保存行为不受影响）；新增 `EqualsNormalizedAbsolute` 仅供执行端 `Resolve` 唯一使用。
- 全程无 taskkill / Stop-Process / wmic terminate / 计划任务 / 提权 / 网络；无真实电源操作；测试不触碰真实进程；不创建系统目录/管理员资源；不修改注册表或卷配置启用 8.3。
- 本阶段不运行完整 UIA（不修改 UI）；未为 UIA 强杀任何现有进程。

## 8. 最终工作树状态（结果提交后）

由阶段报告随附提交后 `git status --short` 快照（见 §9 之后的最终状态）。仅新增本阶段 3 个源码文件改动 + 1 个新测试文件 + 1 个 D2 测试文件修订 + 2 份文书；前序未提交截图、`Invoke-ASUI2Smoke.ps1`、诊断脚本、历史工作包、LibreOffice MSI 全部原样保留。

## 9. 两个本地提交

1. **实现提交**：`ExecutablePathKey.cs` + `CloseAppsService.cs` + `CloseAppsTargetList.cs`（执行端绝对路径门槛 + 配置校验）+ 新测试 `S_CLOSEUI1_D3_CloseAppsAbsolutePathGateTests.cs` + 修订 `S_CLOSEUI1_D2_CloseAppsPathMatchTests.cs`（8.3 环境感知 + 契约断言）+ 本阶段执行书。
2. **结果提交**：本结果记录。

（提交哈希见阶段报告随附 git log。）

## 10. 待总顾问独立验收项

- 执行端 `Resolve` 现经 `ExecutablePathKey.EqualsNormalizedAbsolute` 唯一匹配：两侧必须先过 `Path.IsPathFullyQualified` 完整绝对路径门槛，相对/drive-relative/根相对路径 fail-closed，绝不按 CWD 解析、绝不按名称兜底、不回退 PID。
- `CloseAppsTargetList.Validate` 在配置校验阶段拒绝相对路径目标（含 NUL/无法规范化），新保存与加载一律 fail-closed；合法旧版绝对路径兼容、PID-only 语义不变。
- §5 的 8.3 测试已环境感知：本机（有真实短名别名）PASS；无 8.3 能力环境明确 SKIP，不假通过；行为表述为「环境相关能力，不作为跨环境承诺」。
- §4 的相对路径拒绝证据（旧 `EqualsNormalized` 对 CWD 可解析相对路径返回 True / 新门槛返回 False）请总顾问复核。
