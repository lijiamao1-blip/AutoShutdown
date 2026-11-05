# AutoShutdown V2 · S-UI1-D3「当前任务卡片 4 行说明文字颜色改为黑色」独立返修结果记录（回报包）

- 阶段: S-UI1-D3（纯 UI 展示修复；独立返修，不覆盖原记录）
- 日期: 2026-08-19
- 起始提交: `e07f76b`（S-UI1-D2 结果记录提交；当前 HEAD）
- 实现提交: `0214bae`（`S-UI1-D3: change current-task card 4-line text color to black for readability`）
- 结果记录提交: _本文件提交（见 §1）_
- 执行人: S-UI1-D3 唯一执行 AI（冒烟/测试全部沙箱数据根 `AUTOSHUTDOWN_DATA_ROOT`；真实电源全程 TestMode 隔离）
- 状态: 完成（等待总顾问独立验收）

## 1. 阶段内提交清单

| 提交 | 内容 |
| --- | --- |
| `0214bae` | S-UI1-D3 实现（独立提交）：`src/AutoShutdown.App/MainWindow.xaml`「当前任务」卡 4 行说明文字 8 个 TextBlock 的 `Foreground` 改为黑色（4 标签 + 4 值；唯一改动） |
| _（结果记录提交）_ | S-UI1-D3 结果记录（本文件）+ 构建/全量测试/冒烟日志证据 + 150% 档截图证据（本提交） |

## 2. 启动状态差异如实说明（与执行书 §1.2 描述不一致，如实报告）

- 执行书 §1.2 声称启动时 `git diff HEAD -- src/AutoShutdown.App/MainWindow.xaml` 为**空**。实际只读核验发现：HEAD=`e07f76b` 的当前任务卡片确为「标签 `#D9D1F6` / 值 `White`」，但 **working tree 中已存在一个未提交改动，其内容恰好就是本返修目标（8 个 TextBlock 的 `Foreground` 全部改为 `Black`）**——diff 恰好 4 行 = 8 处 Foreground 改动，`FontSize="13"` 不变、无 FontWeight、`TextWrapping="Wrap"` 与 2 列 Grid 布局均未动。
- **判定**：该未提交改动与执行书 §2.1 目标**逐字节一致**（已直接读取磁盘文件核验），即本阶段唯一要求的改动已存在于工作树；本次按"以执行书目标为准"核验后，将该改动作为 D3 实现提交（`0214bae`），**未新增任何其他改动**。
- 执行书 §1.2 中"此前深蓝+SemiBold 修改已不在 working tree"描述属实；但"`git diff HEAD` 为空"描述与事实**不符**。原因（何人在何时应用了 Black 改动）待总顾问与执行阶段确认；本次未做 reset/checkout 丢弃、未自行发挥，如实记录。该差异不影响 D3 验收结论（最终 `git diff e07f76b..HEAD` 即恰好为本次要求的改动）。

## 3. 问题根因与返修结论（对照执行书 §1/§2）

- **根因**：首页右上角「当前任务」卡片 4 行说明文字在浅色玻璃质感背景下，标签用浅紫 `#D9D1F6`、值用 `White`，对比度不足、辨识度低。
- **返修**：仅将 4 个标签 TextBlock（下次执行：/ 任务类型：/ 提前提醒：/ 触发方式：）与 4 个值 TextBlock（`NextFireTimeText` / `TaskActionText` / `WarningTimeText` / `CountdownSourceText`）的 `Foreground` 改为黑色 `Black`。
- **范围严格**：FontFamily、FontWeight、字号（13）、布局（2 列 Grid）、TextWrapping=Wrap 一律不变（用户 19:23 明确澄清：需求是「颜色改为黑色」，**不是**改粗体/黑体字形）。执行 diff 中无任何 FontFamily/FontWeight/FontSize/布局改动。

## 4. 修改文件职责

| 文件 | 职责（S-UI1-D3） |
| --- | --- |
| `src/AutoShutdown.App/MainWindow.xaml` | 「当前任务」卡 4 行说明文字：4 个标签 TextBlock `Foreground` `#D9D1F6`→`Black`；4 个值 TextBlock `Foreground` `White`→`Black`。仅此一处改动（`git diff e07f76b..HEAD` 恰好 4 行 = 8 个属性）。 |

未改动：`src/AutoShutdown.Core/` 全部、`src/AutoShutdown.App/` 下除 MainWindow.xaml 外的所有文件（含 VM/逻辑/其他页面）、`tools/` 全部、既有测试文件既有断言、`*.sln`、`Directory.Build.props`、`NuGet.Config`、S-UI1-D1 Office 卡、S-UI1-D2 空闲时长逻辑。

## 5. 新建文件清单

| 文件 | 职责 |
| --- | --- |
| `S-PKG-work包/S-UI1-D3-结果记录.md` | 本文件 |
| `S-PKG-work包/S-UI1-D3-截图证据/` | 构建/全量测试/冒烟日志与 150% 档截图证据（见 §6） |

## 6. 测试数量汇总（全部实际执行，真实退出码）

| 测试 | 结果 |
| --- | --- |
| Release 构建（`dotnet build src/AutoShutdown.App/AutoShutdown.App.csproj -c Release`） | **0 错误 0 警告**，rc=0 |
| .NET Release 全量测试（`dotnet test tests/AutoShutdown.Tests/AutoShutdown.Tests.csproj -c Release --verbosity minimal`） | **1587 / 0 / 0**（rc=0；≥ S-UI1-D2 后基线 1587） |
| UIA 冒烟 连过第 1 轮（`tools/test/Invoke-ASUI2Smoke.ps1`，独立运行，对 D3 候选 EXE） | **75 / 0 / 2**（2 SKIP = DPI 100%/125% 单显示器无法覆盖，人工待验），EXIT=0 |
| UIA 冒烟 连过第 2 轮（独立运行） | **75 / 0 / 2**（同上），EXIT=0 |
| `git diff --check` | **0 错误** |
| 聚焦测试 | 未新建（原因见 §6.1） |

证据文件：`S-PKG-work包/S-UI1-D3-截图证据/`：
`S-UI1-D3-构建-Release-0w0e.txt`、`S-UI1-D3-全量测试-1587-0.txt`、`S-UI1-D3-冒烟-连过-第1轮-75-0-2.txt`、`S-UI1-D3-冒烟-连过-第2轮-75-0-2.txt`、
`about/home/office/onetime/tasks/weekday-150percent.png`（150% 档 6 张，来自冒烟通过轮次）。

### 6.1 聚焦测试未新建的说明

执行书 §3/§4.1 将聚焦测试标为「可选」。D3 为纯 XAML `Foreground` 颜色修改，执行书 §4.1 明言「纯 .NET 测试难以断言视觉 Foreground；核心保障是 Release build 0/0、diff 审查仅含 Foreground 改动、UIA 冒烟不崩」。新建测试只能断言 VM 文本存在性（与 D3 颜色无直接关系，且已由既有 1587 测试全量覆盖）；且执行书 §7 验收项为「仅修改 `src/AutoShutdown.App/MainWindow.xaml`，未触碰其他源文件」——新建测试文件会破坏该"唯一改动"边界。故未新建，留待总顾问裁定。

### 6.2 全量测试抖动如实记录（含失败轮次）

对含 D3 改动的实现（`0214bae`）运行全量测试共 3 次：

| 序列 | 结果 | 备注 |
| --- | --- | --- |
| T1 | 1587/0/0 通过 | 首次运行 |
| T2 | 1586/1/0 失败 | `S15_SchedulerIdleIntegrationTests.IdleTask_InputRecoveryDuringCountdown_Cancels`：xUnit `[Fact(Timeout = 2000)]` 硬超时（整测试 >2s 墙钟），报 "Test execution timed out after 2000 milliseconds" |
| T3 | **1587/0/0 通过** | 机器空闲后重跑，rc=0（验收证据） |

**抖动结论（如实报告，未改断言掩盖）**：失败测试为调度器空闲集成测试，全内存假依赖（`FakeClock`/`StubIdleMonitor`/`ControllableDeadline`/`FakeHandler`），无 WPF、无真实电源；该文件 12 个测试全部带 2 秒硬超时。同一测试单独过滤重跑 **3/3 通过（每次 <1ms）**。D3 仅改 MainWindow.xaml 的 8 个 `Foreground` 颜色，与调度器/空闲逻辑无任何代码路径关联（`git diff e07f76b..HEAD -- src/AutoShutdown.Core/` 为空、`tests/` 未改动），判定为**既有时序敏感型偶发（机器负载下单个测试超过 2s 墙钟）**，非 D3 引入。按执行书 §4.2「既有测试失败必须定位根因、不得改断言掩盖」，未修改任何断言；最终以 T3 干净运行（1587/0/0，rc=0）作为验收证据。

### 6.3 GUI 冒烟说明（候选 EXE 发布）

候选 EXE 发布（含 D3 Black 改动，publish 自提交 `0214bae` 的干净工作树）：

```
dotnet publish src/AutoShutdown.App/AutoShutdown.App.csproj -c Release -o artifacts/d3-smoke/S-UI2-0214bae -p:InformationalVersion=v2.0.0-S-UI2.0214bae
```

主 EXE 重命名为 `artifacts/d3-smoke/S-UI2-0214bae/AutoShutdown-v2.0.0-S-UI2.0214bae.exe`（匹配冒烟脚本版本正则；`artifacts/` 已 gitignore，未覆盖历史 `artifacts/release/v2.0.0/S-UI2-dcc0e73/` 候选）。应用 `FormatVersion` 裁掉 SDK 附加的 `+{commit}` 后缀，显示 `v2.0.0-S-UI2.0214bae`，与 P6 版本单源断言一致。

冒烟脚本 `tools/test/Invoke-ASUI2Smoke.ps1` 连续两轮 **75/0/2**（2 SKIP = 本机单显示器 150%，DPI 100%/125% 无法覆盖，列入人工待验），EXIT=0。脚本**未断言字体/颜色**（仅断言文本值）；P1「当前任务 卡可见」在含 Black 改动的候选上通过。截图证据为冒烟通过轮次的 150% 档 6 张 PNG。

## 7. 阶段验收条件逐项对照（执行书 §7）

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| `git diff e07f76b..HEAD -- src/AutoShutdown.Core/` 为空 | ✓ | 输出为空 |
| `git diff e07f76b..HEAD -- tools/` 为空 | ✓ | 输出为空 |
| 仅修改 `src/AutoShutdown.App/MainWindow.xaml`，未触碰其他源文件 | ✓ | `git diff --stat e07f76b..HEAD` 仅 1 文件 |
| SchedulerEngine 空闲触发/取消语义未变；D2 空闲时长逻辑未变 | ✓ | Core diff 空蕴含；D2 代码未触碰 |
| Pre-Pipeline 顺序、FailurePolicy、ShutdownWorkflow 未变 | ✓ | 未触碰相关文件 |
| 无新增 IPowerService 调用方 | ✓ | diff 全文检索无新增 `IPowerService` 引用 |
| Office 卡（D1）、空闲时长卡（D2）未被改动 | ✓ | diff 仅含 4 行 Foreground 改动 |
| 4 行标签 `Foreground` 从 `#D9D1F6` 改为黑色 | ✓ | diff 见 4 个标签 TextBlock |
| 4 行值 `Foreground` 从 `White` 改为黑色 | ✓ | diff 见 4 个值 TextBlock |
| diff 中**无任何** FontFamily/FontWeight/FontSize/布局改动 | ✓ | 仅 `Foreground` 属性变化 |
| 字号仍为 13，布局未破坏 | ✓ | XAML 现状 `FontSize="13"` + 冒烟通过 |
| Release build 0/0 | ✓ | 0 错 0 警，rc=0 |
| 全量测试 0 失败 | ✓ | 1587/0/0（T3），rc=0 |
| GUI 冒烟连续两轮通过 | ✓ | 75/0/2 × 2，EXIT=0 |
| 原记录文件未被覆盖；未 git push | ✓ | 未改动任何既有 `.md`；无 push |

## 8. 安全边界核查（对照执行书 §0/§7）

- `git diff e07f76b..HEAD -- src/AutoShutdown.Core/` → **空**（Core 未修改）。
- `git diff e07f76b..HEAD -- tools/` → **空**（冒烟脚本未动）。
- 仅修改 1 个文件（`src/AutoShutdown.App/MainWindow.xaml`），新建结果记录 + 证据；无 VM/逻辑/其他页面改动。
- ShutdownWorkflow 是唯一电源出口：**无新增 IPowerService 调用方**（diff 全文检索无新增引用）。
- Pre-Pipeline 顺序、FailurePolicy 未变。
- S-UI1-D1 Office 卡、S-UI1-D2 空闲时长逻辑未被动过（diff 仅 Foreground 行）。
- 既有测试文件既有断言未修改（未新建/修改任何测试文件）。
- 冒烟/测试全部在 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根沙箱内；候选 EXE 冒烟 P1 断言 `config.TestMode=true`、`RealPowerEnabled != true`（真实电源全程 TestMode 隔离）。

## 9. 人工待验项目（本环境不可执行或需真实桌面，如实标注，未伪称覆盖）

- DPI 100% / 125% 完整电池：本机单显示器 150%（AppliedDPI=144→150%），100%/125% 按 SKIP 记录，需在对应缩放机器上运行。
- 真实桌面长时间显示可读性：黑色文字在浅色玻璃背景上长期观感需人工目检（150% 档冒烟截图已作为佐证）。
- 900×580 小窗口与各 DPI 下 4 行说明文字完整、不裁切、换行高度差异的人工目检（UIA 冒烟在紧凑 900×580 档通过；150% 档截图佐证）。

## 10. `git status --short` 输出（实现提交 `0214bae` 后、结果记录提交前；执行书 §5.9）

```text
 M "S-PKG-work包/S-UI2-截图证据/about-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/home-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/office-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/onetime-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/tasks-150percent.png"
 M "S-PKG-work包/S-UI2-截图证据/weekday-150percent.png"
?? LibreOffice_26.2.5_Win_x86-64.msi
?? "S-PKG-work包/AutoShutdown-V2-S-PKG-D2-最小返修执行书.md" …（历史工作包，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-PKG-D3-最小返修执行书.md" …（历史工作包，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-PKG-D4-最小返修执行书.md" …（历史工作包，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-PKG-阶段一键执行书.docx"   …（历史输入物，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-UI1-D1-最小返修执行书.md" …（历史输入物，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-UI1-D2-最小返修执行书.md" …（历史输入物，未覆盖）
?? "S-PKG-work包/AutoShutdown-V2-S-UI1-D3-最小返修执行书.md" …（本阶段执行书输入物，未覆盖）
?? "S-PKG-work包/S-PKG-B2-升级与回滚结果记录.md"            …（历史工作包，未覆盖）
?? "S-PKG-work包/S-PKG-B3-系统集成边界结果记录.md"          …（历史工作包，未覆盖）
?? "S-PKG-work包/S-PKG-B4-发布验收归零结果记录.md"          …（历史工作包，未覆盖）
?? "S-PKG-work包/S-PKG-最终独立结果记录.md"                 …（历史工作包，未覆盖）
?? "S-PKG-work包/S-UI1-D3-截图证据/"                        （本阶段证据）
?? "S-PKG-work包/_S-PKG_extracted.txt"                      …（历史输入物，未覆盖）
?? S13-work包/… S23-work包/…                                （历史输入物，未覆盖）
```

说明：结果记录提交仅新增 `S-UI1-D3-结果记录.md` 与 `S-UI1-D3-截图证据/`（提交后不再显示为 `??`）；其余 `M`（S-UI2 既有证据 PNG，会话开始前既有冒烟覆盖所致）与 `??`（历史工作包/输入物/本阶段执行书）均非 D3 改动、未触碰（见 §11 已知限制）。完整原文见本阶段终态 `git status --short`。

## 11. 已知限制

- 若当前任务卡片背景未来改为深色主题，黑色文字将不可读（本返修只针对当前浅色玻璃背景）。
- 不同 DPI 下换行高度差异：4 行文字在紧凑窗口可能因缩放产生换行差异，150% 档已验证完整，其余档位人工待验。
- S15 全量测试 `[Fact(Timeout = 2000)]` 在机器负载下偶发超时（§6.2）：既有测试时序问题，非 D3 引入；未改断言，最终以干净运行作证。
- 启动时 working tree 已含 Black 改动、与执行书 §1.2「diff 为空」描述不符（§2）：未做任何丢弃/还原，如实记录，留待总顾问裁定。

## 12. 最终工作树状态

- S-UI1-D3 实现提交 `0214bae` 已完成；结果记录提交（本文件 + 证据）随后单独进行。
- 未进行 `git push`；未进入其他阶段；未 reset/checkout 丢弃任何改动；未覆盖任何既有 `.md` 记录。
