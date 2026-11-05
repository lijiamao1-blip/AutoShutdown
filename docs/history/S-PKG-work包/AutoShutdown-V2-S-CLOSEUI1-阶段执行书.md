# AutoShutdown V2 · S-CLOSEUI1「从运行中的进程选择关闭目标」阶段执行书

- 阶段: S-CLOSEUI1（软件设置 → 关闭应用（关机前）卡的目标添加体验：从运行中的进程只读选择）
- 基线: `565ffec`（S-UI3 提交；当前 HEAD）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入下一阶段
- 工作包目录: `S-PKG-work包/`（既有历史工作包与 LibreOffice MSI 全部保留、未覆盖）
- 执行人: S-CLOSEUI1 唯一执行 AI
- 沙箱纪律: 冒烟/测试全部使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根；真实电源全程 TestMode 双闸门隔离

## 0. 硬性约束（不得违反）

1. **只读枚举（安全硬约束）**：绝不启动/终止/关闭任何进程，绝不发送窗口关闭消息，绝不请求管理员权限；
   不使用 taskkill/Stop-Process/wmic/计划任务，不进行网络/上传。单个进程读取失败仅将该行置灰并给出原因，
   绝不能导致整个列表失败。
2. **保存模型**：「精确路径执行 + 智能提示重新绑定」。唯一执行匹配依据是规范化后的完整 EXE 绝对路径；
   不持久化 PID；禁止自动进程名回退；保存/显示只读身份信息（进程名/产品/公司/窗口标题/添加时间）绝不参与匹配。
3. **选择规则**：只有通过安全检查的进程才可选择（完整绝对路径可读、非自身进程、非核心系统进程、同会话等）；
   reparse/junction/symlink fail-closed；同一 EXE 路径多进程按路径去重；已有列表路径显示「已添加」且不可重新添加。
4. **对话框**：独立模态进程选择器，8 列表头，搜索/刷新/全部取消/确定/取消，底部只读声明文本。
5. **升级后**：已保存路径不存在 → 显示「原程序路径已失效，请重新选择运行中的程序」；候选仅用于显示/搜索，
   必须明确重新选择，绝不自动更新。
6. **关闭执行边界不变**：OfficeSave → RunCommands → CloseApps 顺序不变；ShutdownWorkflow 仍是唯一电源退出路径；
   不修改冻结状态机/双重闸门/fail-closed；从选择器添加绝不授予强制终止。
7. **交付**：本执行书 + `S-CLOSEUI1-结果记录.md`；两个提交按顺序：(1) 实现+测试+执行书，(2) 自动化结果+截图+结果记录。
   提交只显式暂存本阶段文件，**绝不 `git add -A`**；预先存在的未提交更改保持原样。
8. 原记录文件（`S-PKG-最终独立结果记录.md`、`S-PKG-B2/B3/B4-*.md` 等）不得覆盖。

## 1. 阶段目标

### A. 只读进程信息探测
- `IProcessInfoProvider`（只读枚举 + 按 PID 复核）：System.Diagnostics.Process + FileVersionInfo，逐字段防御性容错；
  单条读取失败置空/哨兵，绝不抛中断整表。
- 不复用也不触碰执行边界 `IProcessManager/IAppWindowManager`；探测层与执行层完全分离。

### B. 安全资格评估（fail-closed）
- `ProcessSelectionGuard`：纯函数判定（自身进程、路径不可读/相对路径/环境变量占位、reparse/junction/symlink、
  指向目录/非普通文件、会话未知/非当前会话、自身路径、辅助进程、Windows 核心系统进程、已添加）→ 不可选并给出原因；
  全部通过 → 可选。任何无法确认的状态一律不可选。

### C. 进程选择窗口（模态、只读）
- 8 列表头：选择 / 进程名 / PID / 可执行文件完整路径 / 窗口标题 / 产品名称 / 公司名称 / 状态（不可选原因）。
- 搜索（进程名/路径/窗口标题/产品/公司）、刷新、全部取消、确定、取消；底部只读声明文本。
- 不可选行标灰、复选框禁用并显示原因（含「已添加」）；行虚拟化保持默认开启（数百进程仅实例化视口内行）。
- **确定复核**：再次复核勾选进程仍存在、路径仍一致、安全资格仍通过（fail-closed）；被排除者提示，绝不静默添加。

### D. 设置页接入与升级重绑定
- 打开选择器、合并去重（按规范化路径）、失效行「重新选择」替换；取消不改动目标集合。
- 已添加目标按文件名标识显示；升级后路径失效显示「原程序路径已失效」提示与「重新选择」按钮，绝不自动更新路径。

### E. 勾选写回根因修复（本阶段实测发现）
- 只读 DataGrid 下 `DataGridCheckBoxColumn` 的 TwoWay 绑定不会写回源（视觉可切换但 IsSelected 保持 false），
  导致确定时误判为未勾选。改为模板 CheckBox（OneWay 显示跟随模型）+ Click 处理器显式写回 IsSelected，
  与鼠标/键盘/TogglePattern 交互一致，仅写内存标志，绝不触碰进程。

## 2. 技术方案要点

1. `Core/CloseApps/ExecutablePathKey.cs`：规范化键（大小写/分隔符/末尾点号等），`EqualsNormalized`/`DisplayNormalized`。
2. `Core/Configuration/CloseAppsConfig.cs`：`CloseAppsTargetConfig` 扩展只读识别字段（ProcessName/ProductName/
   CompanyName/WindowTitleAtAdd/AddedAtUtc），校验器与执行匹配均不引用这些字段。
3. `App/Infrastructure/ProcessSelection/`：`IProcessInfoProvider` + `DiagnosticProcessInfoProvider`（只读探测）+
   `ProcessSelectionGuard`（资格评估）+ `ProcessSelectionContext/Decision`（上下文与判定记录）。
4. `App/Presentation/`：`ProcessPickerViewModel`（RefreshAsync/搜索/勾选/ConfirmSelection 复核）、`RunningProcessRow`
   （显示列 + Matches 搜索 + IsSelected）、`ProcessPickerResult`、`InverseBoolConverter`。
5. `App/ProcessPickerWindow.xaml(.cs)`：模态窗口；DataGrid 模板复选框 + Click 写回（见 1.E）；代码后置仅编排确认/取消。
6. `App/Presentation/MainWindowViewModel.cs` + `MainWindow.xaml`：`OpenProcessPicker`/`ReSelectCloseAppsTarget`/
   `ApplyPickerResult`/`AddConfirmedProcesses`；关闭应用卡新增「从运行中的进程选择」与失效行「重新选择」按钮。
7. `App/AppHost/ServiceRegistration.cs`：注册 `IProcessInfoProvider`、注入 `processPickerLauncher`（弹窗 lambda），
   `IPrePipelineRunner` 顺序 OfficeSave→RunCommands→CloseApps 不变。
8. 测试：`S_CLOSEUI1_ProcessPickerTests.cs`（资格/复核/合并/去重/取消/失效重选/持久化身份 30 项）+
   `S_CLOSEUI1_SourceContractTests.cs`（源码契约：无进程控制/无电源耦合/无网络上传/顺序不变 7 项），共 37 项。
9. UIA 冒烟：`tools/test/Invoke-ASCloseUI1Smoke.ps1`（A1-A9 + B1/B2 + C1/C2，107 项断言），真实鼠标点击模拟用户勾选。

## 3. 验证与验收

- Release 构建 0 错误；全量 Release 测试、聚焦测试、UIA 冒烟、`git diff --check` 实际执行并报告真实数量/退出码。
- 全量测试：`dotnet test AutoShutdown.sln -c Release` 1636 通过 / 0 失败 / 0 跳过。
- UIA 冒烟：`Invoke-ASCloseUI1Smoke.ps1` 107 通过 / 0 失败 / 0 跳过（截图证据落盘到 `S-PKG-work包/S-CLOSEUI1-截图证据/`）。
- 安全边界：config.TestMode=true、RealPowerEnabled≠true；测试程序 winver/charmap/mstsc 全程存活（未被关闭）；
  持久化目标无 PID、无强杀授权；选择器只读声明可见。
- DPAPI、真机 WoL/RTC、双机远程控制、125%/150% DPI 等与本阶段无关的环境受限项不涉及。

## 4. 提交计划

1. 实现提交（单独）：源码/XAML/测试/冒烟脚本/本执行书。
2. 结果记录提交（单独）：`S-CLOSEUI1-结果记录.md` + `S-CLOSEUI1-截图证据/`。
3. 提交后停止，等待独立验收；不 git push，不自动进入下一阶段。
