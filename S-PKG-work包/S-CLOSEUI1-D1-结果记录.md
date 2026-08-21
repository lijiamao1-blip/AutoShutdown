# S-CLOSEUI1-D1「进程选择器最小返修」独立结果记录

- 阶段: S-CLOSEUI1-D1（S-CLOSEUI1 进程选择器最小返修：重选原子替换 / 祖先链 reparse / 规范化保存 / 快速筛选 / 显式原因 / 确认前摘要 / 身份复核 / 安全边界 / 测试 / 冒烟 / 提交）
- 执行: 独立阶段唯一执行 AI；自动化测试 + UIA 冒烟全部经 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根 + `TestMode=true` 双闸门
- 开始 HEAD: `3171962`（S-CLOSEUI1: record implementation and validation results；只读开头检查一致）
- 结束 HEAD: 本阶段实现提交 + 本结果提交（两个本地提交哈希见阶段报告随附 git log）
- 环境: Windows 11 Home China（10.0.26200），.NET 8 `net8.0-windows`

## 1. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| 开始只读检查 | `git branch --show-current` / `git rev-parse HEAD` / `git status --short` / `git log --oneline -8` / `git diff --check` | master / `3171962` / 全量清单 / 8 条 log / exit 0 |
| Release 构建 | `dotnet build AutoShutdown.sln -c Release`（输出 `.build-tmp/S-CLOSEUI1-D1-3171962/`） | 0 警告、0 错误，exit 0 |
| 全量测试 | `dotnet test AutoShutdown.sln -c Release` | **1672 通过 / 0 失败 / 0 跳过** |
| D1 聚焦测试 | `S_CLOSEUI1_D1_ProcessPickerTests` | **36 项全部通过** |
| S-CLOSEUI1 聚焦（选择器+契约+D1） | 3 个测试类合计 | **73 项全部通过** |
| UIA 冒烟 | `tools/test/Invoke-ASCloseUI1Smoke.ps1`（Release 版，隔离数据根，精确 PID 回收） | **148 通过 / 0 失败 / 0 跳过** |
| 空白检查 | `git diff --check` | exit 0，无空白错误 |

冒烟输出（`.build-tmp/d1-smoke-full2.log`，磁盘留存、不入基线）末行：`S-CLOSEUI1 UI SMOKE: pass=148 fail=0 skip=0`。

## 2. 截图证据（`S-PKG-work包/S-CLOSEUI1-D1-截图证据/`，独立新目录，7 张）

| 文件 | 内容 |
|---|---|
| `picker.png` | 进程选择窗口全貌（搜索 + 仅显示有窗口/可选项 + 刷新 + 已选择 0 项 + 全选当前可用项/取消当前选择/取消全部选择 + 8 列 + 底部只读声明） |
| `search.png` | 搜索「winver」过滤结果（仅剩 winver 行） |
| `picker-already-added.png` | 已添加路径行显示「已添加」且复选框禁用（A9 关闭「仅显示可选项」后可见） |
| `invalid-hint.png` | 升级后失效路径显示「原程序路径已失效」+「重新选择」按钮 |
| `summary-add.png` | 确认添加摘要（程序名 / PID 仅展示 / 完整路径 / 窗口标题 / 去重后新增数量 + 只按完整路径匹配提示 + 确认添加/返回修改/取消） |
| `reselect-summary.png` | 重新选择确认摘要（原路径 / 新路径 / 确认替换 + 只按完整路径匹配提示） |
| `settings-targets.png` | 关闭应用卡目标列表（winver 正常目标 + mstsc 替换失效行后） |

## 3. 人工验证项（需独立复核）

- 重新选择空确认/取消/全部被排除 → 原目标保持不变；只确认恰好 1 个新目标后原子替换旧行。
- 搜索 +「仅显示有窗口」+「仅显示可选项」三条件组合过滤；刷新仅保留 PID+StartTime+规范化路径三者一致的选择，否则清除并提示。
- 「全选当前可用项」只选当前可见且可选行（不含已添加/不可选/被筛选隐藏），重新选择模式禁用；「取消当前选择」只清可见行；「取消全部选择」清全部。
- 不可选行置灰、复选框禁用、状态列显示 ≥11 类具体原因（非笼统「不可用」）。
- 确认前摘要只展示身份与完整路径，明确提示不按进程名兜底、不自动授予强制结束权限；返回/取消不改动目标集合。
- 摘要窗独立模态展示正常（自带 BoolToVisibility 资源，无 StaticResource 崩溃）。
- junction/祖先 reparse/祖先不可读路径 fail-closed；已保存路径失效 → 显示失效提示 + 重新选择，绝不自动更新。

## 4. 安全边界检查（冒烟 C1/C2 + 契约测试）

- 测试程序 winver / charmap / mstsc 全程存活，**未被关闭、未被终止**（C1 PASS×3）。
- 应用全程存活无崩溃（C2）；仅测试模式下运行，无真实电源。
- config：`TestMode=true`、`RealPowerEnabled=false`（双闸门隔离，C2 PASS×2）。
- 持久化目标记录不含 PID、不含强杀授权；PID/StartTime/窗口标题等身份字段仅展示/复核，绝不参与执行匹配。
- 冒烟全程使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根，未触碰正式用户数据根；脚本只按精确 PID 回收本轮启动的测试进程，无残留实例。
- 契约测试确认：无 taskkill / Stop-Process / wmic / 计划任务 / 提权 / 网络 / 上传调用；OfficeSave→RunCommands→CloseApps 顺序不变；`ShutdownWorkflow` 仍是唯一电源退出路径。

## 5. 根因修复记录

1. **摘要窗 StaticResource 崩溃**：独立摘要窗引用 `BoolToVisibility`，但该键只在 MainWindow/选择窗实例资源中定义 → 打开摘要必抛 `ResourceReferenceKeyNotFoundException`。修复：摘要窗自身 `<Window.Resources>` 声明转换器。未修复则 A6「确认添加」即崩溃。
2. **冒烟 B2 挂起**：InvokePattern 点「确定」会阻塞至 Click 返回，而确认流程现在弹模态摘要窗 → 死锁。修复：真实鼠标点击 + 补摘要交互断言。
3. **冒烟 A9 误报**：默认「仅显示可选项」隐藏不可选的「已添加」行。修复：A9 先关闭该筛选再断言。
4. **xunit 基础设施**：2.8.1→2.9.2、runner 2.8.1→2.8.2、新增 `Xunit.SkippableFact`，为 junction 测试提供真实运行时跳过（junction 创建不可用环境 SKIP，不假通过）；`S23_CP5_RemoteSectionTests` 做 2 行兼容改写。

## 6. 阶段边界与仅报告项

- 本阶段只返修 S-CLOSEUI1 进程选择器；关机执行边界、冻结状态机、双重闸门、fail-closed、关闭流程顺序（OfficeSave→RunCommands→CloseApps）均未改动。
- **仅报告（未重构）**：执行层 `CloseAppsService.Resolve`（`src/AutoShutdown.Core/CloseApps/CloseAppsService.cs:144`）以 `OrdinalIgnoreCase` 比较原始路径文本，未再 `GetFullPath` 归一化。D1 保存端已写入规范化完整路径，常规场景一致；但若进程枚举返回路径与保存值存在归一化差异（`.`/`..`/尾点/8.3 短名等）时，执行匹配窗口可能不一致。按本阶段约束不修改执行层，留待总顾问独立评估是否纳入后续阶段。
- 环境受限与本阶段无关项（DPAPI、真机 WoL/RTC、双机远程控制、125%/150% DPI 复核）不涉及、不承诺。

## 7. 最终 git status（结果提交后）

（由阶段报告随附提交后 `git status` 快照，见阶段报告。）
