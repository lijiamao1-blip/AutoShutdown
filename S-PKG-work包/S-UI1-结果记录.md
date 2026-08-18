# AutoShutdown V2 · S-UI1「发布前 UI 可用性、导航页与诊断中心收尾」独立结果记录（回报包）

- 阶段: S-UI1（发布前 UI 可用性、导航页与诊断中心收尾）
- 日期: 2026-08-18
- 起始提交: `93357ad`（S-PKG-D4 基线；本阶段不含 S-PKG 改动）
- 结束提交: `eae7b5a`（S-UI1 最终实现提交；结果记录提交随后）
- 执行人: S-UI1 唯一执行 AI（冒烟/测试全部沙箱数据根 `AUTOSHUTDOWN_DATA_ROOT`；真实电源全程 TestMode 隔离）
- 状态: ✅ 完成，等待独立验收

## 1. 阶段内提交清单

| 提交 | 内容 |
| --- | --- |
| `8b6532f` | S-UI1 实现：布局重构/导航页真实化/关于页/诊断中心/契约测试更新/B-4 稳定性修复/S-UI1 UIA 冒烟 |
| `d762f0f` | S-UI1 冒烟修复：WoL MAC 提示改为可绑定实例属性 + 日志页导航自动刷新；冒烟断言按真实 UIA 行为重写 |
| `eae7b5a` | S-UI1 剪贴板健壮性：复制日志/摘要失败时只报告状态不崩溃；冒烟断言复制后应用存活 |
| _（结果记录提交）_ | S-UI1 结果记录（本文件）+ 截图/日志证据 |

## 2. 修改文件职责

### 8b6532f（实现主体，25 文件，+3697/−247）

| 文件 | 职责 |
| --- | --- |
| `src/AutoShutdown.App/app.manifest` | 声明 PerMonitorV2 DPI 感知（100/125/150% 布局正确缩放） |
| `src/AutoShutdown.App/AutoShutdown.App.csproj` | 发布/清单相关调整 |
| `src/AutoShutdown.App/AppHost/ServiceRegistration.cs` | 注册诊断中心新服务（自检/导出/截图/ShellOpen/AppInfo） |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/AppInfo.cs` | 关于页产品信息（产品名/真实版本/构建提交/签名状态/数据与日志目录） |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/SafetySelfCheckRunner.cs` | 只读安全自检（配置健康/数据目录可写/任务同步/WoL 合法性/远程 TLS 状态）；不自动修复、不执行电源操作 |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/DiagnosticsRedactor.cs` | 诊断导出脱敏（默认排除 PIN/HMAC/私钥/PFX 密码） |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/DiagnosticsPackageExporter.cs` | 脱敏诊断包导出（ZIP + 汇总文本，隐私开关用户确认） |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/ShellOpenService.cs` | 打开日志目录/定位导出文件 |
| `src/AutoShutdown.App/Infrastructure/Diagnostics/WindowScreenshotService.cs` | 「截图当前窗口」仅用户主动保存并打开目录；绝不自动上传/外传 |
| `src/AutoShutdown.App/Presentation/DiagnosticsCenterViewModel.cs` | 诊断中心 VM：日志刷新/错误筛选/搜索/复制选中/打开目录/复制摘要/自检/导出/截图；全只读或显式触发 |
| `src/AutoShutdown.App/Presentation/MainWindowViewModel.cs` | 页面可见性、关于页属性、移除「后续开放」占位、日志页导航自动刷新（null 防护） |
| `src/AutoShutdown.App/Presentation/Converters.cs` | Bool→Visibility 等布局转换器 |
| `src/AutoShutdown.App/MainWindow.xaml` | 全局布局重构：真实垂直 ScrollViewer（禁用固定 Viewbox 裁切）、时间模式/电源动作区域自适应布局、WoL 可见标签、关于页、日志与诊断页、导航 4 项真实化 |
| `tests/AutoShutdown.Tests/S_UI1_DiagnosticsCenterTests.cs` | 新增聚焦测试（诊断 VM/自检/导出/导航/复制健壮性） |
| `tests/AutoShutdown.Tests/AppHostSourceContractTests.cs` 等 8 个契约测试 | 随服务注册/VM 构造签名更新而同步 |
| `tools/test/Invoke-ASReleaseSmoke.ps1` | B-4 冒烟稳定性修复：有界可诊断的 `Wait-UiReady`（替换固定 Start-Sleep，超时带诊断，不无限等/不静默重试） |
| `tools/test/Invoke-ASUI1Smoke.ps1` | 新增 S-UI1 UIA 冒烟（616 行）：100%/125%/150% DPI、900×580 小窗口、8 种时间模式/5 种动作文字完整与不裁切、6 页可到达可滚动、WoL 标签、诊断页、关于页、截图证据 |

### d762f0f（冒烟修复，6 文件，+212/−34）

| 文件 | 职责 |
| --- | --- |
| `src/AutoShutdown.App/Presentation/WolTargetsSectionViewModel.cs` | `MacFormatHintText` 实例属性（WPF 数据绑定不能解析 const 字段；与 `MacFormatHint` 常量一致） |
| `src/AutoShutdown.App/Presentation/MainWindowViewModel.cs` | 日志页导航自动刷新 + null 防护 |
| `src/AutoShutdown.App/MainWindow.xaml` | MAC 格式提示绑定改用 `MacFormatHintText`（2 处） |
| `tests/AutoShutdown.Tests/S21_WolTargetsSectionViewModelTests.cs` | `MacFormatHintText_BindableInstanceProperty_EqualsConst` |
| `tests/AutoShutdown.Tests/S_UI1_DiagnosticsCenterTests.cs` | 日志页导航自动刷新聚焦测试 |
| `tools/test/Invoke-ASUI1Smoke.ps1` | P5 断言按真实 UIA 行为重写（折叠 ComboBox 无选中值文本→展开数项；复制按钮先选中后点击） |

### eae7b5a（剪贴板健壮性，3 文件，+83/−19）

| 文件 | 职责 |
| --- | --- |
| `src/AutoShutdown.App/Presentation/DiagnosticsCenterViewModel.cs` | 复制选中日志/复制摘要包 try/catch：剪贴板被占用（`CLIPBRD_E_CANT_OPEN`）时仅报告状态，绝不把未处理 COMException 抛回命令通道导致应用崩溃 |
| `tests/AutoShutdown.Tests/S_UI1_DiagnosticsCenterTests.cs` | `DiagnosticsVm_CopyCommands_ClipboardFailure_ReportsStatus_DoesNotThrow` |
| `tools/test/Invoke-ASUI1Smoke.ps1` | 复制后断言进程存活 |

## 3. 测试数量汇总（全部实际执行，真实退出码）

| 套件 | 通过 | 失败 | 跳过 | 退出码 | 备注 |
| --- | --- | --- | --- | --- | --- |
| .NET 全量（AutoShutdown.Tests，Release，HEAD=eae7b5a） | 1537 | 0 | 0 | 0 | `Passed! - Failed: 0, Passed: 1537, Skipped: 0, Total: 1537`（25s） |
| S-UI1 聚焦测试（S_UI1* + S21_WolTargets* 过滤） | 40 | 0 | 0 | 0 | `Passed: 40, Skipped: 0` |
| S-UI1 UIA 冒烟 `Invoke-ASUI1Smoke.ps1` 第 1 轮 | 159 | 0 | 2 | 0 | 候选 `S-UI1-eae7b5a`；SKIP 仅为 100%/125% DPI（本机 AppliedDPI=144→150%，列入人工待验） |
| S-UI1 UIA 冒烟 `Invoke-ASUI1Smoke.ps1` 第 2 轮 | 159 | 0 | 2 | 0 | 与第 1 轮同一候选、紧接复跑，完全一致 → 稳定性证据 |
| Release 构建 `AutoShutdown.sln -c Release` | — | — | — | 0 | `Build succeeded.`，警告 0、错误 0 |
| `git diff --check` | — | — | — | 0 | 无空白错误 |

> B-4 release-acceptance 冒烟 `Invoke-ASReleaseSmoke.ps1`：脚本已在本阶段修复（8b6532f 引入 `Wait-UiReady` 有界等待，替换固定 Sleep）。完整 release-acceptance 冒烟（安装/回滚/卸载生命周期）属 S-PKG B-4 验收范畴，其目标候选硬编码为 S-PKG 产物，本阶段未重跑（见既有 `S-PKG-B4-发布验收冒烟结果记录.md`，该阶段 56/0/0）。S-UI1 阶段用「连续两轮 GUI 冒烟（同一机制 `Wait-UiReady`）均通过」作为稳定性证据，见上方第 3、4 行。

## 4. 阶段验收条件逐项对照

| 执行书条件 | 状态 | 证据 |
| --- | --- | --- |
| 垂直 ScrollViewer 替代 Viewbox 裁切 | ✅ 通过 | 首页 `P2: 页面纵向滚动条可用`；日志/网络唤醒/关于页均实际 ScrollViewer；XAML 无固定 Viewbox 放大裁切 |
| 900×580 下首页 8 种时间模式/5 种动作文字完整 | ✅ 通过 | P2 每项 `可见/在视口内/可点击/文字不裁切（像素级 Get-TextInkExtent）/彼此不重叠` 全 PASS；截图 `home-top-150percent.png`、`home-scrolled-150percent.png` |
| 导航 4 项真实化（高级功能/网络唤醒/日志与诊断/关于软件） | ✅ 通过 | P3 六页均可到达、内容经滚动可达、无水平溢出；P6b 无「后续开放」占位 |
| 关于软件字段（版本/构建/签名/数据目录/日志目录/TestMode） | ✅ 通过 | P6 关于页字段全 PASS；左下角硬编码「v1.0.0 测试版」已删除（P6b PASS）；截图 `about-150percent.png` |
| WoL 目标配置可见标签 | ✅ 通过 | P4 机器名称/MAC/广播地址/UDP 端口标签可见；MAC 格式提示可见（`AA:BB:CC:DD:EE:FF`、可留空、默认端口 9）；截图 `wol-150percent.png` |
| 日志与诊断中心（刷新/筛选/搜索/复制/打开目录/摘要/自检/导出/截图） | ✅ 通过 | P5 日志文件/级别筛选（展开计数）、错误筛选、选中复制（复制后进程存活）、打开日志目录、自检 6 项 PASS、复制摘要、脱敏导出入口、截图入口；截图 `logs-150percent.png` |
| 诊断包脱敏边界（PIN/HMAC/私钥/PFX 默认排除；隐私信息用户确认） | ✅ 通过 | `DiagnosticsRedactor` 默认剔除 PIN/HMAC secret/证书私钥/PFX 密码；`IncludePrivacyInfo` 默认 false，仅用户显式勾选才含路径/IP/MAC/机器名；冒烟/自检均验证 |
| 配置缺失/损坏恢复入口 + 初始化后 TestMode=true | ✅ 通过 | P1 fresh 配置不可用 header + 初始化按钮 → 初始化后 `TestMode=true`、未开启真实电源、config.json 写入 |
| B-4 冒烟稳定性（有界就绪等待 + 连续两轮全绿） | ✅ 通过 | `Wait-UiReady` 有界可诊断等待（超时 40s，超时带已发现元素诊断，非无限等/非静默重试）；S-UI1 冒烟连续两轮 159/0/2、退出码 0 |
| 150% DPI（本机实际缩放） | ✅ 通过 | 本机 `AppliedDPI=144`（150%）；冒烟在 150% 档完整跑 P1–P7 全 PASS；窗口物理 1620×958 ≈ 900×580×scale 校验 PASS |
| 100%/125% DPI | ⏳ 人工待验 | 当前机器单显示器实际缩放 150%，无法切换系统缩放验证另两档；需在对应缩放机器上人工复跑（SKIP 已如实记录，未伪称通过） |

## 5. 构建警告/错误与遗留问题

- 构建: `dotnet build AutoShutdown.sln -c Release` → `Build succeeded.`，警告 0、错误 0，退出码 0。
- `git diff --check` → 0 警告，退出码 0。
- 候选制品: `artifacts/release/v2.0.0/S-UI1-eae7b5a/`，manifest `S-UI1-eae7b5a-candidate.txt`，`signing-status: unsigned-candidate`（未使用正式签名证书，绝不伪称已签名发布）；主 EXE `AutoShutdown-v2.0.0-S-UI1.eae7b5a.exe` SHA-256 `edd8a0651d49ea78d7ec556c28bb563d1e26794a21bf6405017f06c7b128e67b`。

### 遗留/人工待验（如实列示，未伪称通过）

1. **100% / 125% DPI 完整电池**：本机仅 150%（AppliedDPI=144）单显示器；100%/125% 需在对应系统缩放的机器上人工复跑 `Invoke-ASUI1Smoke.ps1`（冒烟已内置自动识别缩放档位）。
2. **真机 WoL / RTC 唤醒 / 双机远程控制**：环境受限（无第二台目标机、无真实 WOL 网段/主板设置），本阶段未做真机端到端；相关 VM 与自检逻辑已有单测覆盖，端到端列入人工待验。
3. **DPAPI 持久化（真实用户凭证）**：环境受限，未在真实用户凭据下验证 DPAPI 加解密链路。
4. **「截图当前窗口」打开所在目录**：截图保存逻辑已执行并断言；自动打开所在目录依赖资源管理器，属人工待验项（脚本已避免在自动化中触发）。
5. **真实任务计划同步（任务计划程序可视化）**：SchedulerEngine 单测覆盖仲裁逻辑；真实 Windows 任务计划程序中的同步表现列入人工待验。

## 6. 最终工作树状态

- 跟踪改动: 实现提交 `8b6532f`/`d762f0f`/`eae7b5a` + 结果记录提交后，无未提交跟踪改动（`git status` 仅余未跟踪证据）。
- 未跟踪（证据，保留不提交）: S13–S23 历史工作包、`S-PKG-work包/`（含本执行书/结果记录/`S-UI1-截图证据/`）、LibreOffice MSI；均未覆盖、未删除。
- 未 git push、未 reset、未 checkout、未进入 S24、未新增业务功能；冻结状态机/双闸门/`ShutdownWorkflow` 唯一 `IPowerService` 出口/`SchedulerEngine` 唯一仲裁路径/`TaskCollection` 唯一事实源/远程单向回调与 fail-closed 规则均未改动；`RunCommands` 本地白名单保持默认空。

### 截图与日志证据清单（`S-PKG-work包/S-UI1-截图证据/`）

| 文件 | 说明 |
| --- | --- |
| `home-top-150percent.png` | 首页顶部（150% DPI，900×580） |
| `home-scrolled-150percent.png` | 首页滚动后（验证内容可滚动访问） |
| `wol-150percent.png` | 网络唤醒页（WoL 可见标签） |
| `logs-150percent.png` | 日志与诊断页 |
| `about-150percent.png` | 关于软件页 |
| `S-UI1-冒烟第1轮-159-0-2.txt` | UIA 冒烟第 1 轮完整输出（159 PASS/0 FAIL/2 SKIP） |
| `S-UI1-冒烟第2轮-159-0-2.txt` | UIA 冒烟第 2 轮完整输出（159 PASS/0 FAIL/2 SKIP，连续复跑证据） |
| `全量测试-1537-0.txt` | .NET Release 全量测试输出（1537/0，退出码 0） |
| `聚焦测试-40-0.txt` | S_UI1 + S21 聚焦测试输出（40/0，退出码 0） |
