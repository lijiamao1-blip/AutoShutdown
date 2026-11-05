# S-CLOSEUI1「从运行中的进程选择关闭目标」独立结果记录

- 阶段: S-CLOSEUI1（软件设置 → 关闭应用（关机前）卡目标添加：只读进程选择器）
- 执行: 独立阶段唯一执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根
- 开始 HEAD: `565ffec`（S-UI3: record implementation and validation results）
- 结束 HEAD: `9db7aae`（本阶段实现提交）+ 本结果提交（两个本地提交哈希见阶段报告随附 git log）
- 环境: Windows 11（10.0.26200），.NET 8 `net8.0-windows`

## 1. 验证矩阵（真实执行，真实数量）

| 项目 | 命令 / 方式 | 结果 |
|---|---|---|
| Release 构建 | `dotnet build AutoShutdown.sln -c Release`（Release 输出 `.build-tmp/S-CLOSEUI1-565ffec/`） | 0 错误，exit 0 |
| 全量测试 | `dotnet test AutoShutdown.sln -c Release` | **1636 通过 / 0 失败 / 0 跳过** |
| 聚焦测试 | `S_CLOSEUI1_ProcessPickerTests`（30 项）+ `S_CLOSEUI1_SourceContractTests`（7 项） | 37 项全部通过 |
| UIA 冒烟 | `tools/test/Invoke-ASCloseUI1Smoke.ps1`（Release 版，隔离数据根） | **107 通过 / 0 失败 / 0 跳过** |
| 空白检查 | `git diff --check` | exit 0，无空白错误 |

冒烟输出（`.build-tmp/smoke-out.txt`，UTF-16 转录）末行：`S-CLOSEUI1 UI SMOKE: pass=107 fail=0 skip=0`。

## 2. 截图证据（`S-PKG-work包/S-CLOSEUI1-截图证据/`，5 张）

| 文件 | 内容 |
|---|---|
| `picker.png` | 进程选择窗口全貌（8 列 + 搜索 + 刷新 + 全部取消/确定/取消 + 底部只读声明） |
| `search.png` | 搜索「winver」过滤结果 |
| `picker-already-added.png` | 已添加路径行显示「已添加」且复选框禁用 |
| `invalid-hint.png` | 升级后失效路径显示「原程序路径已失效，请重新选择运行中的程序」+「重新选择」按钮 |
| `settings-targets.png` | 关闭应用卡目标列表（winver 正常目标 + mstsc 替换后） |

## 3. 人工验证项（需独立复核）

- 进程选择窗口为模态独立窗口，标题与列头正确；底部只读声明文本可见。
- 不可选行置灰、复选框禁用并显示原因（自身进程/核心系统进程/路径不可读/reparse/跨会话/已添加）。
- 真实鼠标勾选后点「确定」→ 目标入列并按规范化路径去重；「取消」不改动任何目标。
- 搜索按进程名/完整路径/窗口标题/产品名/公司名过滤；「刷新」重新枚举。
- 已保存路径失效 → 显示「原程序路径已失效」+「重新选择」，重新选择后替换旧行，绝不自动更新。
- 关闭应用卡目标执行顺序与既有 CloseApps 流程一致（OfficeSave→RunCommands→CloseApps 不变）。
- 全部 8 列表头：选择 / 进程名 / PID / 可执行文件完整路径 / 窗口标题 / 产品名称 / 公司名称 / 状态。

## 4. 安全边界检查（冒烟 C1/C2 + 契约测试）

- 测试程序 winver / charmap / mstsc 全程存活，**未被关闭、未被终止**（C1 PASS×3）。
- 应用全程存活无崩溃（C2）；仅测试模式下运行，无真实电源。
- config：`TestMode=true`、`RealPowerEnabled=false`（双闸门隔离，C2 PASS×2）。
- 持久化目标记录不含 PID、不含强杀授权；保存/显示只读身份字段绝不参与执行匹配。
- 冒烟全程使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根，未触碰正式用户数据根。
- 冒烟脚本自己启动的测试程序按精确 PID 回收，无残留。

## 5. 源码契约测试（7 项，防回归）

- 无 taskkill / Stop-Process / wmic / 计划任务 / 提权调用；无网络/上传调用。
- 探测层（ProcessSelection）与执行层（IProcessManager / IAppWindowManager / ShutdownWorkflow）无耦合。
- OfficeSave→RunCommands→CloseApps 注册顺序不变；ShutdownWorkflow 仍是唯一电源退出路径。
- `CloseAppsTargetConfig` 只读身份字段不被执行匹配引用。

## 6. 根因修复记录（勾选写回）

只读 DataGrid 下 `DataGridCheckBoxColumn` 的 TwoWay 绑定不写回源（视觉可切换但 IsSelected 保持 false），
冒烟 A6 曾显示「勾选成功」却 0 目标。修复：模板 CheckBox（OneWay 显示跟随模型）+ `Click` 处理器
显式写回 `row.IsSelected`（仅内存标志，绝不触碰进程）；冒烟对复选框改用真实鼠标点击（TogglePattern
不触发 Click）。修复后 A6/B2 复检通过，端到端「点击→写回→复核→去重→持久化」链路完整。

## 7. 阶段边界与后续

- 本阶段只改进「从运行中的进程选择关闭目标」添加体验；关机执行边界、冻结状态机、
  双重闸门、fail-closed、关闭流程顺序均未改动。
- DPAPI、真机 WoL/RTC、双机远程控制、125%/150% DPI 等与本阶段无关项不涉及、不承诺。
- 两个提交已按顺序完成；已停待独立验收。不 push、不自动进入下一阶段。

## 8. 最终 git status（结果提交后）

（由阶段报告随附提交后 `git status` 快照，见阶段报告。）
