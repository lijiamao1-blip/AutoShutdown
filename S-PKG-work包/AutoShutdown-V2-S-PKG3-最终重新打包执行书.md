# AutoShutdown V2 · S-PKG3「启动缺陷返修后的最终重新打包」执行书

- 阶段：S-PKG3（启动缺陷返修 S-STARTUP-D1 后的最终重新发布候选：全新构建、测试、打包、证据记录与本地提交）
- 执行：本阶段唯一打包执行 AI；自动化测试 + UIA 冒烟 + 30 轮启动专项全部经隔离数据根
- 分支：`master`
- 唯一打包基线 HEAD：`6e3e1243219b663d7a211728589603a3029c01f8`（短哈希 `6e3e124`）
- 基线提交主题：`S-STARTUP-D1-D4: record junction-link deletion correction + re-verification`
- 基线提交日期：`2026-08-22T21:26:00+08:00`
- 日期：2026-08-22

## 0. 基线声明

- **唯一打包基线为 `6e3e1243219b663d7a211728589603a3029c01f8`。** 正式候选必须从该提交对应源码**全新构建**；不得从旧候选复制 EXE/DLL/ZIP/manifest，不得复用旧 publish 输出，不得在构建失败后回退旧候选。
- 本阶段为重新发布候选阶段，只允许基于已经通过总顾问独立验收的 S-STARTUP-D1 最终 HEAD 全新构建、测试、打包和记录；**不得继续使用或覆盖旧候选 S-PKG2-e50afcb**。
- **打包时序（如实声明）**：为确保 EXE 的 `InformationalVersion`/`ProductVersion` 在 SDK `IncludeSourceRevisionInInformationalVersion` 默认开启下追加的是**基线 6e3e124 的完整哈希**（而非其后追加的脚本提交），正式候选的 `restore/build/publish` 在 **HEAD==6e3e124** 时执行；执行书与打包脚本最小修改在打包完成后作为独立提交追加，两者均不触碰应用业务源码（`src/`、`tests/`、`Directory.Build.props`、`AutoShutdown.sln`）。
- 候选输出于全新目录 `artifacts\release\v2.0.0\S-PKG3-6e3e124\`，不覆盖 S-PKG2-e50afcb、S-UI2、S-FINALUI1 或 S-STARTUP-D1 任何旧候选。
- 版本与构建提交统一：**`v2.0.0-S-PKG3.6e3e124`**；产品版本中必须包含完整构建提交 `6e3e1243219b663d7a211728589603a3029c01f8`。不得继续显示 `d0c01f3`、`e50afcb` 或旧 S-PKG2 版本。
- 当前工作区保留的既有脏文件与历史工件**全部原样保留**（不 reset / checkout / 删除 / 覆盖 / 暂存 / 提交 / 入包），包括但不限于：6 张已跟踪但修改的旧 S-UI2 截图、LibreOffice MSI、历史工作包/执行书/结果记录/提取文件、旧候选及 artifacts 历史目录、`.build-tmp` 历史工件。完整清单见基线 `git status --short`（本文件同级 `S-PKG3-baseline-git-status.txt`）。
- 已如实记录：`tools/test/diag-btns.ps1`、`diag-fit.ps1` 是阶段前存在但在 S-STARTUP-D1 期间被误删的未跟踪诊断文件，因未被 Git 跟踪无法恢复。**本阶段不得虚构重建。**
- 本阶段**不得修改产品业务功能**；如需最小调整打包脚本，必须先证明当前脚本不能识别 S-PKG3 版本，只做最小必要修改，单独形成脚本实现提交。
- Windows 任务计划程序同步真机行为仍为**人工待验**；本轮不得真实创建/更新/运行/删除系统计划任务。
- 候选是**「重新发布候选」**，在总顾问独立复验和用户最终批准前不得称为正式发布版。
- 本阶段不得 `git push`，不得上传或分发，不得进入下一阶段；完成结果记录提交后立即停止。

## 1. 干净源码构建要求

- 构建前验证：`git rev-parse HEAD` = `6e3e1243219b663d7a211728589603a3029c01f8`、`git branch --show-current` = `master`、暂存区为空、`git diff --check` exit 0。
- 构建输入与 6e3e124 源码完全一致（打包在 HEAD==6e3e124 执行；工作区中未提交的仅为本阶段执行书与打包脚本最小修改，均不在构建输入清单 `src/ tests/ tools/ Directory.Build.props AutoShutdown.sln` 的应用业务范围内——tools/test 脚本修改不影响 `dotnet build/publish` 的应用产物）。
- 所有构建输出进入新的独立目录 `artifacts\release\v2.0.0\S-PKG3-6e3e124\`，不得覆盖任何旧候选。
- 不得：reset / checkout 主工作树、stash 当前用户工件、清理历史文件、下载新依赖、联网、提权、git push。

## 2. 打包体系

沿用已经通过 S-PKG、D1、D2、D3、D4、S-PKG2、S-STARTUP-D1 验收的既有安装、升级、回滚、卸载和路径安全体系，不得重新设计安装架构。

最终候选至少生成：

- win-x64 自包含单文件 EXE（安装候选）
- win-x64 framework-dependent ZIP
- AutoShutdown.OfficeSaveHelper.exe
- candidate.txt / candidate.json / SHA256SUMS / FILE-MANIFEST / build-report / version-git

所有产物目录、文件名、界面版本、程序集信息和清单必须包含或明确记录最终打包短哈希 `6e3e124` 与完整构建提交 `6e3e1243219b663d7a211728589603a3029c01f8`。采用明确最终候选标签：**S-PKG3-6e3e124**。不得为了改标签修改产品业务逻辑。

## 3. 打包脚本最小修改（如需）

### 3.1 已知需要修改的脚本

**`tools/test/Invoke-ASUI2Smoke.ps1`（UIA2 冒烟版本解析）：**
- 现状：第 85 行版本正则 `AutoShutdown-(v[\d.]+-S-(?:UI2|PKG2)\.[0-9a-f]+)\.exe$`。
- 证明无法识别 S-PKG3：S-PKG3 候选 EXE 名为 `AutoShutdown-v2.0.0-S-PKG3.6e3e124.exe`，其中 `S-PKG3` 不匹配 `S-(?:UI2|PKG2)`，版本解析将失败。
- 最小修改：`S-(?:UI2|PKG2)` → `S-(?:UI2|PKG2|PKG3)`（保留 S-UI2/S-PKG2 兼容供既有候选回归）。
- 仅此一行 + 说明注释；不改变任何断言、超时或回收逻辑；不放宽版本/HEAD/白名单/安全断言。

**`tools/test/Invoke-SStartupD1Loop.ps1`（启动专项循环断言补强）：**
- 现状：该循环覆盖「主窗口出现 / 二次启动激活转发 / 副实例退出 / 托盘真实退出 / 残留 0 / 正式数据目录不变」，但**缺少** S-PKG3 专项要求的：
  1. 「调用窗口关闭 → 窗口隐藏到托盘 + 主进程仍存活」步骤；
  2. 「托盘退出后日志顺序含 TrayExitRequested → ApplicationStopping → ApplicationStopped」断言。
- 最小修改：新增窗口关闭到托盘步骤（经 UIA `WindowPattern.Close()` + 有界轮询 MainWindowHandle==0 + 进程存活断言）与托盘退出日志序列断言（按索引序检查 TrayExitRequested 在 ApplicationStopping 之前、ApplicationStopping 在 ApplicationStopped 之前）；扩展 CSV 表头/行记录。
- 仅追加断言；不改应用业务、不改安全边界、不放松任何既有断言、不使用强杀。

### 3.2 不需要修改的脚本

- `tools/Publish-ReleaseCandidate.ps1`：`-Step`/`-Build` 已参数化，`-Step S-PKG3 -Build 6e3e124` 直接生成 S-PKG3 候选，无需修改。
- `tools/test/Invoke-ASUI3Smoke.ps1` 及 `ASUI3IsolatedRootCleanup.ps1`：接受 `-Exe` 直指候选、含 C1（关闭到托盘）与 C2（真实托盘退出 + 日志序列 TrayExitRequested→ApplicationStopping→ApplicationStopped）、无强杀；用于 UIA 冒烟 ≥2 连续轮，无需修改。
- 全部 S-PKG/S-PKG-D harness 与 S-UI3 冒烟、ASUI3 删除边界测试：不解析候选版本号，无需修改。

## 4. 包内容白名单 / 黑名单

**允许进入候选包：** AutoShutdown 主程序及必要 DLL、AutoShutdown.OfficeSaveHelper、必要运行时文件（WPF 原生库 `*_cor3.dll`、satellite 资源）、使用说明、安全说明、正式运行所需的图标和资源。

**禁止进入候选包：** 源码、tests、bin、obj、.build-tmp、Git 元数据、历史工作包、六张旧 S-UI2 截图、任何阶段截图、测试日志、用户任务和配置、runtime.json/tasks.json、正式用户日志和诊断包、证书/PFX/PIN/HMAC secret/配对信息、LibreOffice MSI、diag 脚本、`_*_extracted.txt`、stash 内容、旧候选包、临时探针、自动下载器或联网更新组件、任何真实用户数据。

## 5. 安全不变量

必须继续保持：ShutdownWorkflow 是唯一生产 IPowerService 调用出口；双闸门与冻结状态机不变；OfficeSave → RunCommands → CloseApps 固定顺序不变；SchedulerEngine 是唯一仲裁路径；TaskCollection 是任务唯一事实来源；Windows Task Scheduler 只能单向同步并回调本地应用、不得直接执行电源命令；远程控制不得双向修改本地任务或配置；配置、授权、证书和未知状态必须 fail-closed；RunCommands 白名单默认空；CloseApps 目标和强杀授权默认空；不新增第二电源出口；不处理 S19 D2B/D2C；安装/升级/回滚/卸载继续执行既有路径所有权、ancestor-chain、reparse point、junction 和 symlink 安全检查。

**文件锁处理：** 不结束用户或正式 AutoShutdown 进程，不使用 taskkill/wmic/tskill/Stop-Process/按进程名结束，不创建结束进程的计划任务，不绕过权限；使用新的隔离输出目录；若仍无法继续，停止并提示用户从托盘正常退出。

## 6. 构建与自动化验收

至少执行并如实报告（每项记录实际命令、通过/失败/跳过、退出码、日志绝对路径，不得只写"通过"）：

1. Release restore
2. Release build（TreatWarningsAsErrors，0 警告 0 错误）
3. .NET Release 全量测试
4. `S_STARTUP_D1_StartupLifecycleTests` 聚焦
5. S23 远程聚焦 `S23_CP5_RemoteSectionTests`
6. S22 task-sync 自动化（Fake + 内存，不写系统任务计划）
7. S-UI3 launcher 契约 `S_UI3_UiTestLauncherTests`
8. ASUI3 删除边界聚焦 `Invoke-ASUI3RemoveBoundaryTests.ps1`
9. S-PKG 打包聚焦 `FullyQualifiedName~S_PKG`
10. S-PKG D1–D4 xUnit 聚焦 `FullyQualifiedName~S_PKG_D` + 各 harness
11. 候选安装/升级/回滚/卸载沙箱测试（真实候选 EXE，隔离沙箱）
12. 包内容白名单/黑名单检查
13. 候选树 reparse point 检查
14. SHA-256 独立复算与版本/HEAD 一致性
15. UIA 冒烟至少连续两轮全通过
16. 候选专项启动循环 30/30
17. git diff --check

S23 已知排序型 flaky 必须如实记录：若出现失败，保留原始失败日志；与基线和单独复跑结果对照；不得声称已经消除；不得越界修改 Core 排程；最终发布候选仍要求一次完整全量套件干净通过。

## 7. 启动缺陷专项验收（30 轮）

必须从 **S-PKG3 最终候选 EXE 本身**执行，不得使用 bin、旧候选或 .build-tmp 中间 EXE 替代：

至少连续 30 轮：
1. 每轮独立 AUTOSHUTDOWN_DATA_ROOT（全新临时目录 + 安全 config.json）。
2. TestMode=true、RealPowerEnabled=false。
3. RunCommands 白名单和命令列表为空；CloseApps 目标和强杀授权为空。
4. 无人值守、远程监听和任务计划同步默认关闭（隔离根不含 `remote-settings.json`/`task-sync.json`/无人值守授权文件，应用默认即关闭）。
5. 启动候选，记录主 PID。
6. 有限时间确认主窗口出现且 MainWindowHandle 非零。
7. 再次启动同一候选：激活原主窗口；次实例有限时间退出；只剩原主 PID；不得出现 `ActivationForwardFailed`。
8. 调用窗口关闭：窗口隐藏到托盘；主进程仍存活。
9. 通过本轮精确 PID 的真实托盘菜单「退出程序」：主进程有限时间退出；残留 AutoShutdown 进程为 0；日志顺序含 `TrayExitRequested` → `ApplicationStopping` → `ApplicationStopped`。
10. 正式数据目录前后完全一致。

不得使用：taskkill、Stop-Process、Process.Kill、按进程名结束、重启电脑、多跑取成功、强杀后把该轮记录为通过。任意一轮无窗口、启动超时、ActivationForwardFailed、托盘退出失败或残留进程，30 轮整体不得通过。

## 8. UIA 安全要求

- UIA 必须使用本轮 S-PKG3 最终候选 EXE，禁止复用旧候选或中间 EXE。
- 必须使用隔离数据根：TestMode=true、RealPowerEnabled=false、Windows Task Scheduler 同步关闭、无人值守关闭、远程监听关闭、RunCommands 白名单和命令列表为空、CloseApps 目标和强杀授权为空、不导入证书/PIN/secret/配对数据、不读写或覆盖正式用户数据、不执行真实电源操作。
- 只按本轮保存的精确 PID 回收本轮测试进程；不得按进程名结束，不得结束正式实例。
- 至少连续两轮全通过。

## 9. 安装、升级、回滚和卸载

沿用既有 S-PKG 测试纪律。验证至少包括：全新安装、同版本处理、从既有版本升级、升级失败回滚、文件替换失败不留下半安装状态、安装所有权校验、候选源目录和目标目录边界、恶意相对子路径拒绝、ancestor-chain reparse point 拒绝、候选子目录 junction/symlink 拒绝、卸载只删除本产品拥有的文件、用户数据保留策略、Windows Task Scheduler 专属目录清理契约、其他应用的计划任务/文件/注册表/数据不受影响。

本阶段不得为了验证而真实修改用户当前正式安装；优先使用隔离目录、测试替身和既有安全 harness。需要真实管理员安装、真实任务计划程序写入或系统级验证的项目，标记"人工待验"，不得自行提权执行。

## 10. Windows 任务计划程序记录口径

继续写明：后端已实现；UI 已有；自动化已验证（Fake 适配器 + 内存存储，未写入系统任务计划程序）；用户真机验证未执行；本轮执行 AI 真机验证未执行；发布前状态为人工待验。本阶段不得勾选同步、不得真实创建/更新/运行/删除系统计划任务。

## 11. SHA-256 与可复现性

对所有最终候选产物计算 SHA-256（单文件 EXE、framework-dependent ZIP、OfficeSaveHelper、关键清单文件）。清单记录：完整 Git HEAD、短哈希、提交日期、提交主题、产品版本、InformationalVersion、.NET SDK 版本、运行时标识、自包含/框架依赖模式、签名状态、文件大小、SHA-256。独立复算必须与候选 manifest 及 SHA256SUMS 逐项一致；任何不一致都不得通过。

## 12. 签名与发布口径

无正式代码签名证书时：如实标记 `unsigned-candidate`；不伪造签名；不生成或导入临时生产证书；不宣称已解决 Windows SmartScreen 信誉问题。候选完成后仍不得上传、分发、发布或 git push。

## 13. 提交纪律

只允许显式暂存本阶段文件，禁止 `git add .` / `git add -A`。推荐顺序：

1. 新建并提交 S-PKG3 阶段执行书（本文件）+ 基线 git status 记录。
2. 若打包脚本确需修改：只提交最小脚本修改（`Invoke-ASUI2Smoke.ps1` 正则、`Invoke-SStartupD1Loop.ps1` 断言补强）、相应测试与执行书补充；不包含候选二进制或日志。
3. 从基线 6e3e124 全新打包（打包时序见 §0，先行于本阶段提交）。
4. 完成全部验收。
5. 单独提交：`S-PKG3-结果记录.md` + 必要且精确的文本日志、manifest 和验收证据。
6. 候选 EXE、ZIP、OfficeSaveHelper、publish 目录及 .build-tmp 不得加入 Git（既有仓库规则已 gitignore）。
7. 旧截图和历史工件不得进入任何提交。
8. 完成结果记录提交后立即停止。

## 14. 完成报告

完成后报告：打包基线完整 HEAD、是否修改打包脚本、脚本提交完整哈希（如有）、结果记录提交完整哈希、最终 master HEAD、S-PKG3 候选目录、EXE/ZIP/Helper 的绝对路径/大小/完整 SHA-256、版本与完整构建提交、全部测试真实数量/跳过数/退出码、30 轮逐轮 PID/窗口耗时/次实例退出/激活/托盘退出/残留数、UIA 连续通过轮次、正式数据目录未变证据、unsigned-candidate 状态、Windows 任务计划真机仍为人工待验、git status --short、git push 是否执行（必须为否）。完成后立即停止：不 git push、不上传、不分发、不安装到正式系统、不进入下一阶段，等待总顾问独立复验和用户最终批准。
