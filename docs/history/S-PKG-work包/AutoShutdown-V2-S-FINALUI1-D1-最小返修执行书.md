# AutoShutdown V2 · S-FINALUI1-D1「图标资源链接与首页输入区域宽度正式闭环」独立执行书

- 阶段: S-FINALUI1-D1（S-FINALUI1 的最小闭环阶段）
- 基线: `c4c2484`（S-FINALUI1: record implementation and validation results；开始前 HEAD 一致）
- 分支: `master`；不 reset、不 checkout、不 git push、不自动进入下一阶段
- 工作包目录: `S-PKG-work包/`（历史工作包、六张旧 S-UI2 修改截图、LibreOffice MSI、S-UI2-D1 未闭环证据、`diag-btns.ps1`、`diag-fit.ps1`、`_*_extracted.txt`、stash 内容、旧候选包均保留，未修改、未清理、未覆盖）
- 执行人: S-FINALUI1-D1 唯一执行 AI
- 沙箱纪律: 冒烟/测试全部使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根 + `TestMode=true` 双闸门；冒烟脚本只按精确 PID 回收本轮自己启动的测试进程

## 0. 追溯建档声明（不得虚构）

1. 以下 **4 项已跟踪修改** 在本执行书建立之前已由用户本人授权实施，并经总顾问裁决确认归属，非未知污染，不得丢弃：
   - `src/AutoShutdown.App/AutoShutdown.App.csproj`：为共享 `assets/icon.ico` 增加 `<Link>assets\icon.ico</Link>` WPF 资源链接（配合既有 `LogicalName` 固定清单名）。
   - `src/AutoShutdown.App/MainWindow.xaml`：首页时间输入区域固定 `Width="480"`、`HorizontalAlignment="Left"`。
   - `tests/AutoShutdown.Tests/S_UI2_MultiTaskHomeTests.cs`：增加首页布局契约断言（480 固定宽布局契约）。
   - `tests/AutoShutdown.Tests/S_UI3_UiTestLauncherTests.cs`：增加图标资源链接契约测试。
2. 本阶段只做 **追溯建档、独立检查、测试、精确提交和候选复验**。**未虚构原始执行时间或原始执行者**；执行书中全部构建、测试、冒烟结果均由本阶段唯一执行 AI 于 2026-08-21 实际执行。
3. **不得扩展功能范围**。**不得修改 Core、排程、Office、电源、远程、任务计划同步或安装安全逻辑**（`git diff HEAD --stat -- src/AutoShutdown.Core/` 必须为空）。
4. Windows 任务计划程序同步真机行为仍标记「人工待验」；本阶段不得真实创建、更新、运行或删除系统计划任务。

## 1. 开始前核对（只读）

执行 `git branch --show-current`、`git rev-parse HEAD`、`git status --short`、`git diff --cached`、`git diff --check`、`git log --oneline -12`，必须确认：

- 分支为 `master`。
- HEAD 为 `c4c2484d92d4e5890991d079ca7b51ee0b08c3d3`。
- 暂存区为空。
- 当前新增源码范围只有上述 4 个文件；六张旧 S-UI2 截图继续保持修改状态，不得处理。
- 若状态不一致，立即停止并回报。

## 2. 代码审查要求（独立检查）

1. `assets/icon.ico` 只作为 WPF 资源链接，不产生重复资源、重复输出或错误路径：
   - 仅一条 `<Resource Include="..\..\assets\icon.ico">`（SDK 默认 glob 不到项目目录之外，无隐式重复）；
   - `<Link>assets\icon.ico</Link>` 归一化项目内路径，`<LogicalName>assets/icon.ico</LogicalName>` 固定清单资源名，二者一致；
   - `ApplicationIcon` 仅负责 EXE 级图标（RT_GROUP_ICON），与 WPF 资源不冲突。
2. 主窗口、提醒窗口和托盘图标仍使用同一图标来源：`Icon="/assets/icon.ico"`、`Icon="/assets/icon.ico"`、`pack://application:,,,/assets/icon.ico` 均解析到同一嵌入资源 `assets/icon.ico`。
3. 构建后的应用能正确解析 `pack://application:,,,/assets/icon.ico`：`.g.resources` 含 `assets/icon.ico`（既有 S12.4.3 契约测试 `BuiltAppDll_GResourcesContainIcon` / `BuiltAppExe_EmbedsGroupIconResource` 验证）。
4. 固定宽度 `480` 不会在常见窗口宽度和 150% DPI 下产生横向滚动条、裁切或覆盖：UIA 冒烟 P2 档（紧凑 900×580 DIP）+ 首页截图像素复核。
5. 首页继续满足：无最外层滚动条、创建区域无内部滚动条、按钮和中文文字不截断、未选中区域 Collapsed、当前任务与最近活动正常显示（S-UI2 首页布局契约测试 + 冒烟 P1/P2/P5）。
6. 新增测试不能只检查字符串存在：图标契约测试额外断言 `<Resource Include="..\..\assets\icon.ico">` 恰好 1 条（无重复资源/重复输出）；首页布局契约测试额外断言 `Width="480" HorizontalAlignment="Left">` 恰好 2 处（时间模式 WrapPanel 与时间输入区 Border，无重复/冲突/误配）。
7. 不得修改现有任务、状态机、批量操作或安全边界（仅 csproj 资源元数据 + 一处 XAML 宽度 + 两处测试断言）。

## 3. 精确提交候选验证（不得复用旧候选）

- S-FINALUI1 旧 UIA 候选由**脏工作树**构建却标记 `db16739`，不能作为精确提交候选证据。
- 本阶段必须：先完成实现提交 → 取得实现提交完整哈希 → 从该提交对应的**干净源码**构建全新候选 → 候选目录、EXE 文件名、界面版本、清单和日志必须包含该实现提交短哈希。
- 不得复用或覆盖 `S-UI2-db16739` 旧候选；构建失败不得启动旧 EXE。
- 记录候选 EXE 完整 SHA-256。

## 4. 测试要求

至少执行：Release build、.NET Release 全量测试、`S_UI2_MultiTaskHomeTests`、`S_UI3_UiTestLauncherTests`、图标资源加载测试（S12.4.3）、首页布局契约测试、S-FINALUI1 批量操作聚焦测试、S-CLOSEUI1 聚焦测试、S22 自动化测试、从精确实现提交生成的候选 UIA 连续两轮、`git diff --check`。逐项报告通过数/失败数/跳过数/退出码/日志绝对路径。

UIA 冒烟必须继续使用隔离数据根 + `TestMode=true`：不读取正式用户数据、不执行真实电源操作、不启用 Windows 任务计划程序同步、不启用无人值守、不启用远程监听、`RunCommands` 为空、`CloseApps` 强杀授权为空、只按本轮保存的精确 PID 回收测试进程。

## 5. 提交顺序

1. **实现提交**：上述 4 个已跟踪文件 + 本执行书。
2. **结果记录提交**：S-FINALUI1-D1 结果记录 + 新的精确提交候选测试证据 + 必要的新截图证据。
3. 不得把候选二进制、`.build-tmp`、旧截图或历史工件加入 Git（除非既有发布体系明确要求跟踪对应清单）。

完成后报告：实现提交完整哈希、结果记录提交完整哈希、最终 master HEAD、精确候选版本和绝对路径、候选 EXE SHA-256、全部测试真实数量与退出码、`git status --short`、`git diff --check`、尚存修改和未跟踪工件。结果记录提交后立即停止，不得恢复 S-PKG2、不得 git push。
