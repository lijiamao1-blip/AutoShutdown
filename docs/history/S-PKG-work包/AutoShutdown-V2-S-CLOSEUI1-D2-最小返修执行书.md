# AutoShutdown V2 · S-CLOSEUI1-D2「关闭执行端路径规范化一致性」独立执行书

- 阶段: S-CLOSEUI1-D2（S-CLOSEUI1 执行端路径规范化一致性的最小返修阶段）
- 基线: `f3de6ef`（S-CLOSEUI1-D1: record implementation and validation results；开始前 HEAD 一致）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入下一阶段
- 工作包目录: `S-PKG-work包/`（既有历史工作包、前序未提交截图、`Invoke-ASUI2Smoke.ps1`、诊断脚本、LibreOffice MSI 全部保留，未修改、未清理、未覆盖）
- 执行人: S-CLOSEUI1-D2 唯一执行 AI
- 唯一修改目标: S-CLOSEUI1-D1 结果记录 §6「执行层 `CloseAppsService.Resolve` 以 `OrdinalIgnoreCase` 比较原始路径文本、未再 `GetFullPath` 归一化」的不一致问题
- 沙箱纪律: 本阶段不启动/结束任何正式或测试实例；不运行真实电源；测试全部使用替身（Fake），绝不枚举或操纵真实进程

## 0. 硬性约束（不得违反）

1. 只读执行开头五步：`git branch --show-current`、`git rev-parse HEAD`、`git status --short --untracked-files=all`、`git log --oneline -8`、`git diff --check`。HEAD 一致（`f3de6ef`）后才开工。
2. 保留全部前序未提交截图、`Invoke-ASUI2Smoke.ps1`、历史工作包、诊断脚本及 LibreOffice MSI；不得修改、清理、覆盖或夹带。禁止 `git add .`、`git add -A`、reset、checkout 和 git push。
3. 本阶段只修 `src/AutoShutdown.Core/CloseApps/CloseAppsService.cs` 的 `Resolve` 路径匹配；不修改关闭目标选择器 UI、进程选择器只读枚举边界、电源安全边界、冻结状态机、双闸门或 fail-closed。
4. 只允许调用既有 `ExecutablePathKey.Normalize / EqualsNormalized`；删除「必须把正反目录分隔符视为等价」的硬性要求；不得新增手工字符串替换、8.3 路径展开、文件名匹配或进程名兜底；不得因规范化失败回退 PID。
5. 只能显式暂存本阶段文件；提交前逐项核对 `git diff --cached --name-status`，确保没有夹带前序遗留改动。
6. 提交顺序：(1) 实现、测试和 D2 执行书作为独立实现提交；(2) D2 结果记录作为独立结果提交。
7. 不 git push，不自动进入下一阶段。完成两个本地提交后立即停止，等待 Codex 总顾问独立验收。

## 1. 本阶段要求与本阶段落实

### 一、实现要求（执行端路径规范化一致性）
- `Resolve` 改为对目标路径与进程枚举路径统一调用 `ExecutablePathKey.EqualsNormalized`：两侧都经既有 `Normalize`（`Path.GetFullPath(Trim)`）规范化，仅当两侧均成功规范化且大小写不敏感相等时才匹配。
- 任一侧为空、含 NUL、非法或无法规范化 → fail-closed，不匹配；绝不按进程名/文件名/产品名/公司名/前缀/文件名/目录兜底；规范化失败不回退 PID。
- 不改变「同一路径可能匹配多个运行实例」的既有语义（逐实例过滤天然保留）。
- 不自动授予强杀权限；强杀仍逐目标显式授权。
- 不扩大为文件系统身份、签名、哈希、8.3 展开或句柄级真实路径解析：规范化只委托既有 `Path.GetFullPath`（含其在 Windows 上对真实存在 8.3 短名段的展开行为），不做任何手工处理。
- 旧配置中的合法绝对路径向后兼容（`EqualsNormalized` 对既有绝对路径幂等）。

### 二、安全边界（全部保持不变，本阶段不触碰）
- `ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口；CloseApps 类型不引入任何电源依赖。
- `OfficeSave → RunCommands → CloseApps` 顺序不变（源码契约验证）。
- 冻结状态机、双闸门、fail-closed 不变。
- 关闭前仍按既有逻辑重新读取 PID 与启动时间防 PID 复用；正常关闭/强制结束逻辑不变。
- 强杀仍需逐目标显式授权；进程选择器只读枚举边界不变。
- 不使用 taskkill / Stop-Process / wmic terminate / 计划任务 / 提权 / 网络；不执行真实电源操作。

### 三、测试要求（新增独立聚焦测试 26 项，覆盖 17 项必测点 + 向后兼容）
- 服务级（16 项）：完全一致匹配、大小写差异、`.` 段、`..` 段、正反分隔符（如实记录 `Path.GetFullPath` 真实结果）、首尾空白、完全不同路径不匹配、同名异目录不匹配、前缀不匹配、任一侧为空不匹配、含 NUL 不匹配、规范化失败不按进程名兜底、多实例只返回规范化精确相等者、PID+启动时间复核不变、强杀授权默认值与执行条件不变（默认 false + 已授权时超时仍强杀）。
- 键级（7 项）：正斜杠→反斜杠（真实环境）、大小写+空白+`.`+`..`+分隔符组合等价、重复分隔符折叠、末段尾点去除（Win32 语义）、任一侧空/空白不匹配、任一侧含 NUL 不匹配、真实存在 8.3 短名段被 `GetFullPath` 展开（如实记录，修正原「8.3 必然匹配不到」假设）。
- 源码契约（3 项）：`Resolve` 只经 `EqualsNormalized`、旧 `OrdinalIgnoreCase` 直比已移除、无名称兜底；`ServiceRegistration` 固定顺序注释与动作链 `new OfficeSaveAction < new RunCommandsAction < new CloseAppsAction`；CloseApps 全部源码无 `IPowerService`/`PowerRequest` 且 `ShutdownWorkflow` 引用 `IPowerService`。
- 旧配置合法绝对路径向后兼容由服务级「完全一致匹配」用例显式覆盖。

### 四、验证
- Release 构建 → .NET Release 全量测试 → D2 聚焦测试 → CloseApps 既有相关测试 → 安全源码契约测试 → `git diff --check` → 最终 `git status --short`。
- 本阶段不运行完整 UIA（不修改 UI）；与 CloseApps 执行路径直接相关的自动化测试全部运行。不为了 UIA 强杀任何现有进程。

## 2. 真实环境核实（探针，`.build-tmp/path-probe`，gitignore）

在当前 Windows 11 / .NET 8.0.30 环境实测 `Path.GetFullPath` 真实结果（详见结果记录 §5）：
- 正斜杠 → 反斜杠（`C:/Windows/...` → `C:\Windows\...`）；混合分隔符同样归一。
- `.`、`..` 段折叠；重复分隔符折叠；首尾空白裁剪；末段尾点去除（Win32 语义）。
- 含 NUL → `ArgumentException` → fail-closed。
- 单段相对路径按进程 CWD 解析（旧配置合法绝对路径不受影响）。
- **真实存在的 8.3 短名段被展开**（`C:\PROGRA~1\...` → `C:\Program Files\...`）：这是既有 `ExecutablePathKey.Normalize` 在 Windows 上的既有行为，保存端与执行端因此自动一致；原「8.3 与长路径必然匹配不到」的假设在当前环境不成立，如实记录，不做任何手工 8.3 展开。

## 3. 提交计划

1. **实现提交（单独）**：`src/AutoShutdown.Core/CloseApps/CloseAppsService.cs`（Resolve 改 `EqualsNormalized`）+ 新测试 `tests/AutoShutdown.Tests/S_CLOSEUI1_D2_CloseAppsPathMatchTests.cs` + 本执行书。
2. **结果提交（单独）**：`S-PKG-work包/S-CLOSEUI1-D2-结果记录.md`。
3. 每个提交前核对 `git diff --cached --name-status`，排除 S-UI2 截图/`Invoke-ASUI2Smoke.ps1`/诊断脚本/LibreOffice MSI/历史工作包等前序遗留。
4. 提交后停止，等待独立验收；不 git push，不自动进入下一阶段。
