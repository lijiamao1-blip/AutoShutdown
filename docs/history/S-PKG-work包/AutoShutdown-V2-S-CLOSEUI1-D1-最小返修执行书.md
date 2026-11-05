# AutoShutdown V2 · S-CLOSEUI1-D1「进程选择器最小返修」独立执行书

- 阶段: S-CLOSEUI1-D1（S-CLOSEUI1 进程选择器的最小返修阶段，共 11 节要求）
- 基线: `3171962`（S-CLOSEUI1: record implementation and validation results；开始前 HEAD 一致）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入下一阶段
- 工作包目录: `S-PKG-work包/`（既有历史工作包、前序未提交截图、`Invoke-ASUI2Smoke.ps1`、诊断脚本、LibreOffice MSI 全部保留，未修改、未清理、未覆盖）
- 执行人: S-CLOSEUI1-D1 唯一执行 AI
- 沙箱纪律: 冒烟/测试全部使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根 + `TestMode=true` 双闸门；冒烟脚本只按精确 PID 回收本轮自己启动的测试进程

## 0. 硬性约束（不得违反）

1. 只读执行开头四步：`git branch --show-current`、`git rev-parse HEAD`、`git status --short`、`git log --oneline -8`、`git diff --check`。HEAD 一致（`3171962`）后才开工。
2. 保留全部前序未提交截图、`Invoke-ASUI2Smoke.ps1`、历史工作包、诊断脚本及 LibreOffice MSI；不得修改、清理、覆盖或夹带。禁止 `git add .`、`git add -A`、reset、checkout 和 git push。
3. 本阶段只返修 S-CLOSEUI1 进程选择器，不修改 CloseApps 执行语义、排程、首页、Office、远程控制、S-UI3 或电源安全边界。
4. 冒烟脚本只能按精确 PID 清理本轮脚本自己启动的测试进程，不得按名称批量结束任何实例。
5. 只能显式暂存本阶段文件；提交前逐项核对 `git diff --cached --name-status`，确保没有夹带前序遗留改动。
6. 提交顺序：(1) 实现、测试和 D1 执行书作为独立实现提交；(2) 测试日志、截图证据和 D1 结果记录作为独立结果提交。测试日志保留在磁盘（`.build-tmp/`，gitignore），结果记录中登记路径。
7. 不得 git push，不得自动进入下一阶段。完成两个本地提交后立即停止，等待 Codex 总顾问独立验收。

## 1. 十一节要求与本阶段落实

### 一、重新选择不误删原目标
- `ProcessPickerViewModel.BuildPreview()`：重新选择模式下仅允许确认恰好 1 个新程序；空选择/无选择/全部被排除/取消 → 返回 `null`，选择窗保持打开并提示「未选择任何程序，原目标保持不变」或「重新选择一次只能选择一个程序」，绝不触碰原目标。
- `MainWindowViewModel.AddConfirmedProcesses`：`if (replaceRow is not null && confirmed.Count == 0) return;` 前置守卫 —— 只有至少一个新目标通过确认复核后，才先删旧目标再原子加入新目标。
- 回归冒烟 B2a：重新选择空确认 → 原失效目标仍保留（`PASS`）。

### 二、完整祖先链重解析检查
- `ProcessSelectionGuard.CheckOrdinaryFile` 改为沿 EXE 路径逐级父目录上行（深度 <64 防护），任一祖先为 reparse/junction/symlink → 不可选（原因「路径祖先目录为符号链接/联接，安全起见不可选择」）；任一祖先目录读取异常/非目录 → fail-closed（原因「路径祖先目录状态无法确认，安全起见不可选择」）。绝不跟随链接解析真实目标。
- 保留最终 EXE 必须存在/普通文件/非 reparse 检查。
- 测试 `junction\app.exe` 用例为真实 junction 创建测试，junctions 创建不可用时 `[SkippableFact]` 运行时跳过（`Skip.IfNot`），绝不假通过。

### 三、规范化路径保存一致性
- 保存与去重全部经 `ExecutablePathKey.Normalize`（`Path.GetFullPath(trimmed)`；失败返回 null → 该行不添加）。
- 归一化失败不添加、不进程名兜底、不持久化 PID；已添加/确认复核/持久化/重载全部使用一致规范化；`EqualsNormalized` 大小写不敏感。
- 测试覆盖大小写/`.`/`..`/重复路径。
- **仅报告（不重构）**：执行层 `CloseAppsService.Resolve`（`src/AutoShutdown.Core/CloseApps/CloseAppsService.cs:144`）仍以 `OrdinalIgnoreCase` 比较原始路径文本、未再 `GetFullPath` 归一化。因 D1 保存端已写入规范化完整路径，常规场景一致；但当进程枚举返回的 `ExecutablePath` 与保存值存在归一化差异（`.`/`..`/尾点/8.3 短名等）时，执行匹配窗口可能不一致。按本阶段约束不修改执行层，仅记录待总顾问评估。

### 四、选择器快速筛选与按钮
- 顶栏：搜索框（按进程名/路径/窗口标题/产品名/公司名）+「仅显示有窗口」（默认开）+「仅显示可选项」（默认开）+ 刷新 + 「已选择 N 项」实时计数。
- 工具行：「全选当前可用项」（只选当前筛选结果中可见且可选的行；重新选择模式禁用）、「取消当前选择」（只清可见行）、「取消全部选择」（清全部）。
- 刷新保留规则：仅当 PID+StartTime+规范化路径三者一致才保留原勾选；否则清除该选择并提示「刷新后已清除 N 项无法确认的原选择」。

### 五、显式不可用原因（≥11 类，不笼统写「不可用」）
- `ProcessSelectionGuard.Evaluate` 共返回 18 种不可选原因（自身进程 / Windows 核心系统进程 / 会话未知 / 非当前会话 / 路径不可读 / 相对路径 / 环境变量占位 / 指向目录 / 非普通文件 / 最终文件为 reparse / 祖先目录为 reparse / 祖先目录状态无法确认 / 无主窗口 / 进程已退出（复核期）/ 已添加等）。
- 行置灰、复选框禁用、状态列显示原因、全选不包含、确定复核 fail-closed。

### 六、确认前路径摘要
- 新增独立模态摘要窗 `ProcessPickerSummaryWindow`（720×520，自带 `BoolToVisibility` 资源，杜绝 StaticResource 解析崩溃）。
- 摘要含：程序名 / PID（仅本次展示）/ 完整 EXE 路径 / 窗口标题（仅展示）/ 去重后最终新增数量；固定提示「任务执行时只按以下完整路径匹配。不会按进程名自动兜底，也不会自动授予强制结束权限。」。
- 重新选择摘要显示 原路径 / 新路径 / 「确认替换」；按钮：确认添加（确认替换）/ 返回修改 / 取消。
- 摘要不启动、不关闭、不终止任何程序；确认前重新读取 PID + StartTime + 路径 + 安全资格。

### 七、进程身份复核
- 确定确认时逐进程按 PID 重新读取：PID 已知且 StartTimeUtc 已知且与初始枚举相等，路径 `EqualsNormalized` 一致，重新 `Evaluate` 安全资格仍通过；任一不满足 → fail-closed 排除并提示。
- 受保护进程且 StartTime 不可读 → 不可选/复核失败，绝不降级。

### 八、安全边界
- 全程只读；不启动/关闭/终止任何进程、不发窗口消息、无 taskkill/Stop-Process/wmic/计划任务、无提权/网络/上传、无进程名兜底、无自动更新失效路径、不持久化 PID、不自动授予强制结束权限。
- OfficeSave→RunCommands→CloseApps 顺序不变；`ShutdownWorkflow` 仍是唯一 `IPowerService` 生产出口；双重闸门、冻结状态机、fail-closed 全部不变。

### 九、自动化测试
- 新增 `tests/AutoShutdown.Tests/S_CLOSEUI1_D1_ProcessPickerTests.cs`：**36 项**覆盖全部九节场景（空确认保留原目标 / 取消保留 / 全失败保留 / 多选拒绝 / 原子替换 / junction 拒绝 / 祖先目录不可读拒绝 / 正常链可选 / 规范化路径保存 / 搜索+两筛选组合 / 有窗口筛选 / 可选筛选 / 全选仅可见可选 / 全选不含已添加与不可选 / 取消当前仅可见 / 取消全部 / 跨筛选计数 / 刷新清理与保留 / 各类原因 / 摘要复核去重 / 返回与取消不改集合 / PID 复用 StartTime 不匹配拒绝 / 不强杀 / 不持久化 PID / 无名称兜底）。
- 既有 `S_CLOSEUI1_ProcessPickerTests.cs` 适配 BuildPreview/Commit 流程与 `[SkippableFact]` junction 用例；`S_CLOSEUI1_SourceContractTests.cs` 将 `ProcessPickerPreview.cs`、`ProcessPickerSummaryWindow.xaml(.cs)` 纳入只读源码清单。
- 测试基础设施：xunit 2.8.1→2.9.2、runner 2.8.1→2.8.2、新增 `Xunit.SkippableFact 1.4.13`；`S23_CP5_RemoteSectionTests.cs` 2 行 xunit 2.9.2 兼容改写（`Assert.Single` 谓词重载）。

### 十、UIA 冒烟
- `tools/test/Invoke-ASCloseUI1Smoke.ps1` 扩展 A1-A9/B1/B2/B2a/C1/C2 全链路；隔离 `AUTOSHUTDOWN_DATA_ROOT` + TestMode 双闸门；断言 >148 项，含：搜索、两筛选开关、刷新、全选仅当前筛选、取消当前、计数、不可用原因可见、确认前摘要出现、返回保留勾选、取消不改设置目标、确认添加加入规范化路径、**重新选择空确认不删旧目标**、测试进程存活、无崩溃、无真实电源、正式数据根不变。
- 截图证据写入独立新目录 `S-PKG-work包/S-CLOSEUI1-D1-截图证据/`（不覆盖原 S-CLOSEUI1 截图）。

### 十一、验证与提交
- Release 构建 → 全量 Release 测试 → 聚焦 D1 测试 → 聚焦 S-CLOSEUI1 契约 → UIA 冒烟 → `git diff --check` → `git status --short`；随后两个本地提交并停止。

## 2. 本阶段发现并修复的真实缺陷

1. **摘要窗 StaticResource 崩溃（必现致命）**：`ProcessPickerSummaryWindow.xaml` 引用 `{StaticResource BoolToVisibility}`，但该键只在 MainWindow/选择窗实例资源中定义，独立窗口解析必抛 `ResourceReferenceKeyNotFoundException`。修复：在摘要窗自身 `<Window.Resources>` 声明转换器。未修复则 A6「确认添加」冒烟即崩溃。
2. **冒烟 B2 挂起**：B2 用 `Invoke-Click`（InvokePattern）点「确定」，但确认处理现在弹模态摘要窗 → 死锁。修复：改真实鼠标点击 `Invoke-RealClickOn`，并补摘要窗交互断言（原路径/新路径/确认替换/截图/点击）。
3. **冒烟 A9 误报**：默认「仅显示可选项」筛选隐藏不可选的「已添加」行，导致 A9 断言失败。修复：A9 先关闭该筛选再定位「已添加」行（与 A5 同模式）。

## 3. 验证计划与真实结果

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short` / `git log --oneline -8` / `git diff --check` | master / `3171962` 一致 / 全量清单核对 / 最近 8 条确认 / exit 0 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release`（输出 `.build-tmp/S-CLOSEUI1-D1-3171962/`） | 0 警告、0 错误，exit 0 |
| 全量测试 | `dotnet test AutoShutdown.sln -c Release` | **1672 通过 / 0 失败 / 0 跳过** |
| D1 聚焦测试 | `S_CLOSEUI1_D1_ProcessPickerTests` | **36 项全部通过** |
| S-CLOSEUI1 聚焦（选择器+契约） | `S_CLOSEUI1_ProcessPickerTests` + `S_CLOSEUI1_SourceContractTests` + `S_CLOSEUI1_D1_ProcessPickerTests` | **73 项全部通过** |
| UIA 冒烟 | `Invoke-ASCloseUI1Smoke.ps1`（Release 版，隔离数据根，精确 PID 回收） | **148 通过 / 0 失败 / 0 跳过**（日志 `.build-tmp/d1-smoke-full2.log`） |
| 空白检查 | `git diff --check` | exit 0，无空白错误 |

## 4. 提交计划

1. **实现提交（单独）**：S-CLOSEUI1 源文件（8 修改）、D1 新源文件（`ProcessPickerPreview.cs`、`ProcessPickerSummaryWindow.xaml(.cs)`）、测试文件（新 `S_CLOSEUI1_D1_ProcessPickerTests.cs` + 3 个既有适配/兼容/清单）、csproj、冒烟脚本、本执行书。
2. **结果提交（单独）**：`S-CLOSEUI1-D1-结果记录.md` + `S-PKG-work包/S-CLOSEUI1-D1-截图证据/`（7 张）。测试日志保留在磁盘 `.build-tmp/d1-smoke-full2.log`（gitignore，登记于结果记录）。
3. 每个提交前核对 `git diff --cached --name-status`，排除 S-UI2 截图/`Invoke-ASUI2Smoke.ps1`/诊断脚本/LibreOffice MSI/历史工作包等前序遗留。
4. 提交后停止，等待独立验收；不 git push，不自动进入下一阶段。
