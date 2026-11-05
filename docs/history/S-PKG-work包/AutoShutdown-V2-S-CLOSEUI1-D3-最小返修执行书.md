# AutoShutdown V2 · S-CLOSEUI1-D3「执行端拒绝相对路径 + 8.3 测试环境隔离」独立执行书

- 阶段: S-CLOSEUI1-D3（S-CLOSEUI1 关闭执行端拒绝相对路径 + 8.3 测试环境隔离的最小返修阶段）
- 基线: `8b5dfa4`（S-CLOSEUI1-D2: record implementation and validation results；开始前 HEAD 一致）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入下一阶段
- 工作包目录: `S-PKG-work包/`（既有历史工作包、前序未提交截图、`Invoke-ASUI2Smoke.ps1`、诊断脚本、LibreOffice MSI 全部保留，未修改、未清理、未覆盖）
- 执行人: S-CLOSEUI1-D3 唯一执行 AI
- 唯一修改目标:
  1. 执行端相对路径可能被 `Path.GetFullPath` 按当前工作目录解析，扩大关闭匹配范围（`ExecutablePathKey.Normalize` 可接受相对路径并转绝对路径）。
  2. D2 的 8.3 短路径测试无条件依赖本机卷配置（硬编码 `C:\PROGRA~1\App\app.exe`），可能在其他正常 Windows 环境误失败。
- 沙箱纪律: 本阶段不启动/结束任何正式或测试实例；不运行真实电源；测试全部使用替身（Fake），绝不枚举或操纵真实进程；不创建系统目录/管理员资源来启用 8.3

## 0. 硬性约束（不得违反）

1. 只读执行开头五步：`git branch --show-current`、`git rev-parse HEAD`、`git status --short --untracked-files=all`、`git log --oneline -8`、`git diff --check`。HEAD 一致（`8b5dfa4`）后才开工。
2. 保留全部前序未提交截图、`Invoke-ASUI2Smoke.ps1`、历史工作包、诊断脚本及 LibreOffice MSI；不得修改、清理、覆盖或夹带。禁止 `git add .`、`git add -A`、reset、checkout 和 git push。
3. 本阶段只修执行端绝对路径门槛 + 配置校验 + 8.3 测试隔离；不修改进程选择器 UI，不扩大 CloseApps 功能，不进入其他阶段。
4. 不得改变既有 `ExecutablePathKey.EqualsNormalized` 的公共语义（不完整审计全部调用点）；本阶段只收紧执行端，优先采用范围最小的实现：新增语义明确的 `ExecutablePathKey.EqualsNormalizedAbsolute`，供 `CloseAppsService.Resolve` 路径匹配唯一使用。
5. 只能显式暂存本阶段文件；提交前逐项核对 `git diff --cached --name-status`，确保没有夹带前序遗留改动。
6. 提交顺序：(1) 实现、测试和 D3 执行书作为独立实现提交；(2) D3 结果记录作为独立结果提交。
7. 不 git push，不自动进入下一阶段。完成两个本地提交后立即停止，等待 Codex 总顾问独立验收。

## 1. 本阶段要求与本阶段落实

### 一、执行端必须拒绝相对路径
- `CloseAppsService.Resolve` 路径目标匹配前必须确认目标路径是完整绝对路径；每个进程快照的 `ExecutablePath` 也必须是完整绝对路径。
- 使用 `Path.IsPathFullyQualified` 检查原始去空白文本；任一侧为 null、空、只有空白、相对、drive-relative（`C:app.exe`）、当前目录相对（`.\app.exe`）、父目录相对（`..\app.exe`）、根相对但缺卷标（`\app.exe`）、含 NUL、无法规范化时直接不匹配。
- 只有两侧都是完整绝对路径后，才允许调用既有 `ExecutablePathKey.EqualsNormalized`（内部 `Normalize` = `Path.GetFullPath(Trim)`）。
- 不得按当前工作目录把相对目标转换成可执行关闭目标；不得按进程名/文件名/窗口标题/产品名/公司名兜底；不得回退 PID；不得模糊/前缀/目录匹配。
- 实现：新增 `ExecutablePathKey.EqualsNormalizedAbsolute`（先 `Path.IsPathFullyQualified` 门槛，再委托既有 `EqualsNormalized`），`Resolve` 唯一调用它。未改动 `EqualsNormalized` 的公共语义。

### 二、配置校验（fail-closed 前置）
- 新保存的 CloseApps 路径目标必须是完整绝对路径；相对路径配置在配置校验阶段被拒绝，不等执行时才处理。
- `CloseAppsTargetList.Validate`：路径目标先 `Path.IsPathFullyQualified`，再 `ExecutablePathKey.Normalize` 非空（拒含 NUL/无法规范化）。
- 配置损坏 fail-closed（`CloseAllAsync` 对非法清单返回失败，不静默回退）；合法旧版完整绝对路径继续兼容；PID-only 历史目标既有校验语义不变；路径与 PID 并存仍被拒绝。
- 不把非法路径静默删除后继续执行其他危险目标；保持既有配置损坏处理策略（`ConfigurationValidator` 委托 `CloseAppsTargetList.Validate`，`ConfigurationService.SaveAsync` 对校验错误拒绝保存）。

### 三、8.3 短路径测试环境感知
- D2 的 `EqualsNormalized_ExistingShort8Dot3Segment_ExpandedByGetFullPath_OnThisWindows` 硬编码假定 `PROGRA~1` 存在 → 改为 `[SkippableFact]`：
  1. 不硬编码假定 `PROGRA~1` 必然存在。
  2. 只读 `GetShortPathName` 确认目标目录真实存在且系统能获得对应短路径别名。
  3. 只有真实获得一对「短路径/长路径」时才断言现有规范化函数的实际行为。
  4. 无 8.3 能力时明确 SKIP 并写明原因（Xunit.SkippableFact）。
  5. 不创建/修改注册表或卷配置启用 8.3；不为测试创建系统目录或管理员资源。
  6. 不新增手工 8.3 展开到生产代码；8.3 匹配不作为跨机器保证。
  7. 无法展开时继续安全地匹配不到，不按文件名/进程名兜底。
- 结果记录把 8.3 行为写成：「环境相关能力；能真实解析时按现有 Windows/.NET 行为处理，不能解析时安全不匹配，不作为跨环境承诺。」

### 四、自动化测试（新增 D3 聚焦测试 40 项 + 修订 D2 8.3 测试与源码契约）
- 配置校验（11 项）：完整绝对路径通过；`app.exe`/`.\app.exe`/`..\app.exe`/`folder\app.exe`/`C:app.exe`/`\app.exe` 被拒绝；空/空白路径保持既有拒绝语义；PID-only 合法历史目标兼容；路径与 PID 并存仍被拒绝。
- 执行端（12 项服务级）：绝对目标+绝对进程路径匹配；相对目标即使可按 CWD 解析到同一文件也绝不匹配（配置 fail-closed）；相对进程路径即使可按 CWD 解析到目标也绝不匹配；drive-relative 目标/进程路径绝不匹配；任一侧规范化失败（含 NUL）不匹配；不按进程名兜底；不回退 PID；大小写与安全绝对路径文本差异仍按 D2 规则匹配；PID+启动时间复核不变；强杀默认关闭与显式授权条件不变。
- 键级（11 项）：`EqualsNormalizedAbsolute` 门槛（相对/drive-relative/根相对任一侧不匹配；旧 `EqualsNormalized` 对 CWD 可解析相对路径会匹配、新门槛拒绝的直接对照；NUL 不匹配；空/空白不匹配；大小写与安全文本差异仍匹配）。
- 源码契约（6 项）：`CloseAppsService` 执行路径匹配前存在完整绝对路径门槛；`CloseAppsTargetList.Validate` 拒绝相对路径目标；生产 CloseApps 代码无手工 8.3 展开、无产品名/公司名/窗口标题兜底、无 taskkill/Stop-Process/wmic；无新增网络/shell/提权/计划任务调用；`OfficeSave→RunCommands→CloseApps` 顺序不变；`ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口。
- D2 回归（26 项）：修订 8.3 测试为环境感知 + 修订源码契约断言为 `EqualsNormalizedAbsolute`，其余 24 项保持不变。

### 五、验证
- Release 构建 → .NET Release 全量测试 → S-CLOSEUI1-D3 聚焦测试 → S-CLOSEUI1-D2 回归测试 → CloseApps 全部相关测试 → 配置校验相关测试 → 安全源码契约测试 → `git diff --check` → 最终 `git status --short`。
- 本阶段不修改 UI，不要求完整 UIA；不为了测试结束任何现有进程。

## 2. 真实环境核实（探针，`.build-tmp/path-probe`，gitignore）

- 8.3 能力：`GetShortPathName` 只读探测 `C:\Program Files` → `C:\PROGRA~1`、`C:\ProgramData` → `C:\PROGRA~3`（真实别名存在）；`C:\Windows\System32` 无短名。`fsutil 8dot3name query C:` 因权限拒绝，未尝试提权。
- `Path.GetFullPath(@"C:\PROGRA~1\app.exe")` → `C:\Program Files\app.exe`（真实短名段被展开）；`EqualsNormalized` 对真实短/长对返回 True。
- 相对路径风险：`Path.GetFullPath("notepad.exe")` 按当前 CWD 解析为 `D:\...\.build-tmp\path-probe\notepad.exe`；旧 `EqualsNormalized("notepad.exe", CWD解析结果)` = True（漏洞场景），新 `EqualsNormalizedAbsolute(...)` = False（门槛关闭）。

## 3. 提交计划

1. **实现提交（单独）**：`ExecutablePathKey.cs`、`CloseAppsService.cs`、`CloseAppsTargetList.cs`（3 个源码文件）+ 新测试 `S_CLOSEUI1_D3_CloseAppsAbsolutePathGateTests.cs` + 修订 `S_CLOSEUI1_D2_CloseAppsPathMatchTests.cs` + 本执行书。
2. **结果提交（单独）**：`S-PKG-work包/S-CLOSEUI1-D3-结果记录.md`。
3. 每个提交前核对 `git diff --cached --name-status`，排除 S-UI2 截图/`Invoke-ASUI2Smoke.ps1`/诊断脚本/LibreOffice MSI/历史工作包等前序遗留。
4. 提交后停止，等待独立验收；不 git push，不自动进入下一阶段。
