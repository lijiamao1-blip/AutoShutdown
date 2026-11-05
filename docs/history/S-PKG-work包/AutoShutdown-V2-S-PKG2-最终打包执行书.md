# AutoShutdown V2 · S-PKG2「最终打包」执行书

- 阶段：S-PKG2（最终发布候选的构建、打包、验证、证据记录与本地提交）
- 执行：本阶段唯一打包执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根
- 分支：`master`
- 唯一打包基线：`e50afcbcc1c0fe44497d6795f0100aae9049a021`（e50afcb）
- 基线提交主题：`S-FINALUI1-D1: record implementation and validation results`
- 日期：2026-08-21

## 0. 基线声明

- **唯一打包基线为 `e50afcbcc1c0fe44497d6795f0100aae9049a021`。**
- 发布候选必须从该提交对应的源码**全新构建**；不得从旧 artifacts 目录复制 EXE、不得在构建失败后回退旧候选。
- 当前工作区保留六张已跟踪修改的旧 S-UI2 截图及历史未跟踪工件，本阶段**全部明确排除**（不 reset / checkout / 删除 / 覆盖 / 暂存 / 提交 / 放入发布包）。
- 本阶段**不得修改产品业务功能**；如需最小调整打包脚本，必须先报告拟修改文件和理由，修改与测试单独提交。
- Windows 任务计划程序同步真机行为仍为**人工待验**。
- 候选是**「最终发布候选」**，在总顾问和用户最终批准前不得称为正式发布版。
- 本阶段不得 `git push`，不得上传或分发，不得进入下一阶段；完成结果记录提交后立即停止。

## 1. 干净源码构建要求

- 构建前验证：`git diff HEAD -- src tests tools Directory.Build.props AutoShutdown.sln` 必须为空。
- 使用主工作区构建，但必须采用严格白名单输入；构建输入与 e50afcb 源码完全一致。
- 所有构建输出进入新的独立目录，不得覆盖 `S-UI2-457b4aa`、`S-UI2-db16739` 或任何旧 S-PKG/S-PKG2 候选。
- 不得：reset / checkout 主工作树、stash 当前用户工件、清理历史文件、下载新依赖、联网、提权、git push。

## 2. 打包体系

沿用已经通过 S-PKG、D1、D2、D3、D4 验收的既有安装、升级、回滚、卸载和路径安全体系，不得重新设计安装架构。

最终候选至少生成：

- win-x64 自包含单文件 EXE（安装候选）
- win-x64 framework-dependent ZIP
- 包内容清单
- SHA-256 清单
- 版本与 Git 提交清单
- 构建报告
- 安装、升级、回滚、卸载验证报告
- 已知限制与人工待验清单
- 使用说明
- 安全说明

所有产物目录、文件名、界面版本、程序集信息和清单必须包含或明确记录最终打包短哈希 `e50afcb`。不得继续使用 `S-UI2` 作为对用户有误导性的最终阶段名称。采用明确最终候选标签：**S-PKG2-e50afcb**。不得为了改标签修改产品业务逻辑。

## 3. 包内容白名单 / 黑名单

**允许进入候选包：** AutoShutdown 主程序及必要 DLL、AutoShutdown.OfficeSaveHelper、必要运行时文件、使用说明、安全说明、正式运行所需的图标和资源、既有安装/升级/回滚/卸载机制明确要求的文件。

**禁止进入候选包：** 源码、tests、bin、obj、.build-tmp、Git 元数据、历史工作包、六张旧 S-UI2 截图、S-FINALUI1/S-UI2-D1 等测试截图、测试日志、用户任务和配置、runtime.json/tasks.json、正式用户日志和诊断包、证书/PFX/PIN/HMAC secret/配对信息、LibreOffice MSI、diag-btns.ps1、diag-fit.ps1、_*_extracted.txt、stash 内容、旧候选包、临时探针、自动下载器或联网更新组件、任何真实用户数据。

## 4. 安全不变量

必须继续保持：ShutdownWorkflow 是唯一生产 IPowerService 调用出口；双闸门与冻结状态机不变；OfficeSave → RunCommands → CloseApps 固定顺序不变；SchedulerEngine 是唯一仲裁路径；TaskCollection 是任务唯一事实来源；Windows Task Scheduler 只能单向同步并回调本地应用、不得直接执行电源命令；远程控制不得双向修改本地任务或配置；配置、授权、证书和未知状态必须 fail-closed；RunCommands 白名单默认空；CloseApps 目标和强杀授权默认空；不新增第二电源出口；不处理 S19 D2B/D2C；安装/升级/回滚/卸载继续执行既有路径所有权、ancestor-chain、reparse point、junction 和 symlink 安全检查。

**文件锁处理：** 不结束用户或正式 AutoShutdown 进程，不使用 taskkill/wmic/tskill，不创建结束进程的计划任务，不绕过权限；使用新的隔离输出目录；若仍无法继续，停止并提示用户从托盘正常退出。

## 5. 构建与自动化验收

至少执行并如实报告（每项记录实际命令、通过/失败/跳过、退出码、日志绝对路径，不得只写"通过"）：

1. Release restore
2. Release build
3. .NET Release 全量测试
4. S-PKG 全部聚焦测试
5. S-PKG D1–D4 安全返修测试
6. S-FINALUI1/D1 相关测试
7. S-CLOSEUI1/D1/D2/D3 相关测试
8. S22 Windows Task Scheduler 同步自动化测试
9. S-UI3 启动器测试
10. 包内容白名单/黑名单检查
11. 版本与 HEAD 一致性检查
12. EXE/DLL/ZIP/安装包 SHA-256 复算
13. 安装、升级、回滚和卸载测试
14. 候选目录 reparse point/junction/symlink 安全测试
15. 候选包连续两轮 UIA 冒烟
16. git diff --check

## 6. UIA 安全要求

- UIA 必须使用本轮 e50afcb 最终候选，禁止复用旧候选。
- 必须使用隔离数据根：TestMode=true、RealPowerEnabled=false、Windows Task Scheduler 同步关闭、无人值守关闭、远程监听关闭、RunCommands 白名单和命令列表为空、CloseApps 目标和强杀授权为空、不导入证书/PIN/secret/配对数据、不读写或覆盖正式用户数据、不执行真实电源操作。
- 只按本轮保存的精确 PID 回收本轮测试进程；不得按进程名结束，不得结束正式实例。
- 至少验证：主窗口出现、安全测试模式提示清晰可见、图标正常、首页无滚动条或截断、创建至少两条模拟任务、任务管理显示两条任务、四个批量按钮可见且状态正确、CloseApps 摘要和设置导航可用、日志与诊断页可用、关闭窗口后无真实电源动作、正式数据目录未改变、连续两轮通过。

## 7. 安装、升级、回滚和卸载

沿用既有 S-PKG 测试纪律。验证至少包括：全新安装、同版本处理、从既有版本升级、升级失败回滚、文件替换失败不留下半安装状态、安装所有权校验、候选源目录和目标目录边界、恶意相对子路径拒绝、ancestor-chain reparse point 拒绝、候选子目录 junction/symlink 拒绝、卸载只删除本产品拥有的文件、用户数据保留策略、Windows Task Scheduler 专属目录清理契约、其他应用的计划任务/文件/注册表/数据不受影响。

本阶段不得为了验证而真实修改用户当前正式安装；优先使用隔离目录、测试替身和既有安全 harness。需要真实管理员安装、真实任务计划程序写入或系统级验证的项目，标记"人工待验"，不得自行提权执行。

## 8. Windows 任务计划程序记录口径

继续写明：后端已实现；UI 已有；自动化已验证；用户真机验证未执行；本轮执行 AI 真机验证未执行；发布前状态为人工待验。本阶段不得勾选同步、不得真实创建/更新/运行/删除系统计划任务。

## 9. SHA-256 与可复现性

对所有最终候选产物计算 SHA-256（单文件 EXE、framework-dependent ZIP、安装包、关键辅助 EXE、最终清单文件）。清单记录：完整 Git HEAD、短哈希、提交日期、提交主题、产品版本、InformationalVersion、.NET SDK 版本、运行时标识、自包含/框架依赖模式、签名状态、文件大小、SHA-256。重新复算必须与清单完全一致。

## 10. 签名与发布口径

无正式代码签名证书时：如实标记 unsigned candidate；不伪造签名；不生成或导入临时生产证书；不宣称已解决 Windows SmartScreen 信誉问题。候选完成后仍不得上传、分发、发布或 git push。

## 11. 提交纪律

只允许显式暂存本阶段文件，禁止 `git add .` / `git add -A`。若不需要修改打包脚本，则只提交：S-PKG2 执行书、S-PKG2 结果记录、必要且适合 Git 保存的文本清单和测试证据。候选二进制、ZIP、安装包和 .build-tmp 不得提交 Git（既有仓库规则已 gitignore）。如确需最小修改打包脚本：先报告文件和理由；修改与测试单独提交；结果记录与文本证据再单独提交；不得混入产品业务修改。

## 12. 完成报告

完成后报告：打包基线完整 HEAD、是否修改打包脚本、脚本提交完整哈希（如有）、结果记录提交完整哈希、最终 master HEAD、所有候选产物绝对路径、每个产物大小和 SHA-256、包内容白名单/黑名单检查结果、所有测试真实数量/跳过数和退出码、安装/升级/回滚/卸载验证结果、UIA 两轮结果、签名状态、Windows Task Scheduler 真机待验状态、其他人工待验项目、git status --short、git diff --check、未提交和未跟踪工件清单、是否执行 git push（必须为否）。完成后立即停止，等待总顾问独立复验和用户最终批准。
