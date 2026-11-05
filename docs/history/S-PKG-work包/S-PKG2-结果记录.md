# AutoShutdown V2 · S-PKG2「最终打包」独立结果记录

- 阶段：S-PKG2（最终发布候选的构建、打包、验证、证据记录与本地提交）
- 执行：本阶段唯一打包执行 AI；自动化测试 + UIA 冒烟全部经隔离数据根
- 分支：`master`
- 打包基线 HEAD：`e50afcbcc1c0fe44497d6795f0100aae9049a021`（短哈希 `e50afcb`）
- 基线提交主题：`S-FINALUI1-D1: record implementation and validation results`
- 提交日期：`2026-08-21T23:04:59+08:00`
- 环境：Windows 11（10.0.26200），.NET SDK 8.0.423，`net8.0-windows`，RID `win-x64`

## 0. 基线声明

- 唯一打包基线为 `e50afcbcc1c0fe44497d6795f0100aae9049a021`。
- **干净构建输入**：构建前 `git diff HEAD -- src tests tools Directory.Build.props AutoShutdown.sln` 为空；候选从该提交对应源码全新发布，未从任何旧 artifacts 目录复制 EXE，未在构建失败后回退旧候选。
- **独立输出目录**：候选输出于 `artifacts\release\v2.0.0\S-PKG2-e50afcb\`，未覆盖、未复用 `S-UI2-457b4aa`、`S-UI2-db16739` 及任何旧 S-PKG/S-PKG2 候选。
- **候选标签**：统一为 `S-PKG2-e50afcb`（不再使用对用户有误导性的 `S-UI2` 名称），界面版本/EXE 文件名/清单/日志全部包含该标签。
- 六张已跟踪修改的旧 S-UI2 截图及历史未跟踪工件本阶段**全部排除**：未 reset / checkout / 删除 / 覆盖 / 暂存 / 提交 / 放入发布包。
- 未修改任何产品业务功能（`git diff HEAD --stat -- src/` 构建输入为空）。
- **唯一打包脚本最小修改**：`tools/test/Invoke-ASUI2Smoke.ps1` 版本正则由 `S-UI2` 扩为 `S-(?:UI2|PKG2)`，使 UIA 冒烟能识别 `S-PKG2.e50afcb` 最终候选标签（保留 S-UI2 兼容供既有候选回归）。该文件与理由已在运行 UIA 冒烟前单独报告；修改与测试单独提交（见第 6 节），未混入产品业务修改。
- 本阶段不得 `git push`，不得上传/分发，不得进入下一阶段；完成结果记录提交后立即停止。

## 1. 验证矩阵（16 项，真实执行 / 真实数量 / 真实退出码）

| 项 | 命令 / 方式 | 结果 |
|---|---|---|
| 1. Release restore | `dotnet restore AutoShutdown.sln -c Release --nologo` | exit 0 |
| 2. Release build | `dotnet build AutoShutdown.sln -c Release --nologo` | **0 警告 / 0 错误**，exit 0 |
| 3. .NET Release 全量测试 | `dotnet test AutoShutdown.sln -c Release --no-build` | **1746 通过 / 0 失败 / 0 跳过**，exit 0 |
| 4. S-PKG 全部聚焦测试 | `--filter FullyQualifiedName~S_PKG` | **72 通过 / 0 失败 / 0 跳过**，exit 0 |
| 5a. S-PKG D1–D4 xUnit 返修聚焦 | `--filter FullyQualifiedName~S_PKG_D` | **45 通过 / 0 失败 / 0 跳过**，exit 0 |
| 5b. S-PKG 生命周期 harness | `Invoke-SPkgLifecycleTests.ps1` | **21 通过 / 0 失败**，exit 0 |
| 5c. S-PKG 升级/回滚 harness | `Invoke-SPkgUpgradeTests.ps1` | **41 通过 / 0 失败**，exit 0 |
| 5d. S-PKG 边界 harness | `Invoke-SPBoundaryTests.ps1` | **12 通过 / 0 失败 / 0 跳过**，exit 0 |
| 5e. D1 所有权 harness | `Invoke-SPD1OwnershipTests.ps1` | **60 通过 / 0 失败**，exit 0 |
| 5f. D2 reparse harness | `Invoke-SPD2ReparseTests.ps1` | **51 通过 / 0 失败**，exit 0 |
| 5g. D3 ancestor-chain harness | `Invoke-SPD3AncestorTests.ps1` | **41 通过 / 0 失败**，exit 0 |
| 5h. D4 候选树 harness | `Invoke-SPD4CandidateTreeTests.ps1` | **30 通过 / 0 失败 / 1 跳过**（file-symlink 环境），exit 0 |
| 6a. S-FINALUI1 S_UI2 聚焦 | `--filter FullyQualifiedName~S_UI2_MultiTaskHomeTests` | **48 通过 / 0 失败 / 0 跳过**，exit 0 |
| 6b. S-FINALUI1 图标/首页/批量聚焦 | `--filter S12_4_3IconContractTests\|Homepage\|BulkSnooze\|BulkStop\|BulkClear\|TaskManagement_ExposesFourSafeBulkActions` | **34 通过 / 0 失败 / 0 跳过**（20 图标 + 10 首页布局 + 4 批量），exit 0 |
| 7. S-CLOSEUI1 聚焦测试 | `--filter S_CLOSEUI1` | **139 通过 / 0 失败 / 0 跳过**，exit 0 |
| 8. S22 任务计划同步聚焦 | `--filter S22_` | **115 通过 / 0 失败 / 0 跳过**，exit 0 |
| 9. S-UI3 启动器聚焦 | `--filter FullyQualifiedName~S_UI3_UiTestLauncherTests` | **7 通过 / 0 失败 / 0 跳过**，exit 0 |
| 10. 包内容白名单/黑名单检查 | `check-package-content.ps1` | **PASS：37 个文件全部在白名单内**，无禁用名称/路径/文本泄漏，exit 0 |
| 11. 版本与 HEAD 一致性检查 | `verify-artifacts.ps1`（版本信息 + SHA-256 复算） | **PASS**：EXE ProductVersion = `v2.0.0-S-PKG2.e50afcb+e50afcbcc1c0fe44497d6795f0100aae9049a021`；InformationalVersion / 完整 HEAD / 短哈希 / 提交日期 / 提交主题 / SDK / RID / 模式 / 签名状态 / 大小 全部一致 |
| 12. EXE/ZIP/辅助 EXE/清单 SHA-256 复算 | `final-hash-verify.ps1` | **PASS**（9 个产物全部复算与清单逐项一致，见第 3 节），exit 0 |
| 13. 安装/升级/回滚/卸载测试 | `candidate-lifecycle.ps1`（真实候选 EXE，隔离沙箱） | **19 通过 / 0 失败 / 0 跳过**，exit 0 |
| 14. 候选目录 reparse/junction/symlink 安全测试 | `candidate-tree-safety.ps1` | **PASS**：候选根非 reparse point、ancestor-chain 无 reparse、全树扫描 49 个 item 无 reparse，exit 0 |
| 15. UIA 冒烟 第 1 轮 | `Invoke-ASUI2Smoke.ps1 -ReleaseExe S-PKG2-e50afcb 候选 -EvidenceDir S-PKG2-截图证据` | **74 通过 / 1 失败 / 2 跳过**（P5「停用」按钮计数在 Wait-Until 边界后立即统计的时序抖动，与既有 D4 记录 B-4 first-run flake 同类） |
| 15'. UIA 冒烟 第 2 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0 |
| 15''. UIA 冒烟 第 3 轮 | 同上，独立运行 | **75 通过 / 0 失败 / 2 跳过**，exit 0（第 2、3 轮**连续两轮干净通过**，满足要求） |
| 16. 空白检查 | `git diff --check` | exit 0，无空白错误 |

冒烟每轮 2 SKIP = DPI 100% / 125% 完整电池在本机（单显示器 150%）无法覆盖，按既有口径列入人工待验。

## 2. 包内容白名单 / 黑名单检查结论

- 扫描候选目录全部 37 个文件（含 framework-dependent 子目录与嵌套 satellite 资源），全部命中白名单。
- 中文文本文件（`使用说明.txt`、`安全说明.txt`）按内容验证：恰好 2 个、内容包含最终候选标签 `S-PKG2.e50afcb`。
- satellite 资源（如 `de\Microsoft.Win32.TaskScheduler.resources.dll` 等）视为合法运行时文件。
- 未发现任何黑名单项：无源码/tests/bin/obj/.build-tmp/Git 元数据/历史工作包/旧截图/测试日志/用户任务与配置/runtime.json/tasks.json/证书或 secret/LibreOffice MSI/diag 脚本/`_*_extracted.txt`/stash/旧候选包/探针/自动下载器。
- 完整文件清单：`artifacts\release\v2.0.0\manifests\S-PKG2-e50afcb-FILE-MANIFEST.txt`（53 行，覆盖候选 EXE/ZIP/辅助 EXE/所有 manifest 文件）。

## 3. 最终候选详情（全部含 `S-PKG2-e50afcb` 标签）

| 产物 | 绝对路径 | 大小（字节） | SHA-256 |
|---|---|---|---|
| 候选 EXE（自包含单文件） | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG2-e50afcb\AutoShutdown-v2.0.0-S-PKG2.e50afcb.exe` | 156455761 | `78d78e28bd89562e73f9f529c30c14328394ffb772f350b2f1b0b070dc7feca9` |
| framework-dependent ZIP | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG2-e50afcb\AutoShutdown-v2.0.0-S-PKG2.e50afcb-win-x64-framework-dependent.zip` | 1017379 | `4435ea0176471fb9ac19e25bfb61ca6181059122a3ed471169f89395c61e43e0` |
| OfficeSaveHelper | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG2-e50afcb\AutoShutdown.OfficeSaveHelper.exe` | 9641472 | `4f56a76764e719c264bc6dc12a8adbac09336c61c85aeb5ccf4280ed0f101b40` |

- 候选 manifest：`artifacts\release\v2.0.0\manifests\S-PKG2-e50afcb-candidate.txt`（`source-commit = e50afcbcc1c0fe44497d6795f0100aae9049a021`，`sha256` 与复算一致）。
- 结构清单：`S-PKG2-e50afcb-candidate.json`、`S-PKG2-e50afcb-SHA256SUMS.txt`、`S-PKG2-e50afcb-FILE-MANIFEST.txt`、`S-PKG2-e50afcb-build-report.txt`、`S-PKG2-e50afcb-version-git.txt`。
- **SHA-256 可复现性**：独立脚本 `final-hash-verify.ps1` 对 9 个产物（EXE / ZIP / 辅助 EXE / 5 个 manifest 文本）全部重新计算，与候选 manifest 及 SHA256SUMS 逐项一致，最终判定 PASS、exit 0。
- 签名状态：`unsigned-candidate`（仓库无正式生产代码签名证书；未伪造签名、未生成/导入临时生产证书、未宣称已解决 SmartScreen 信誉问题）。

## 4. Windows 任务计划程序记录口径

后端已实现；UI 已有；自动化已验证（S22 聚焦 115 项全部通过，全部使用 `AutoShutdown.Core.Tasks.TaskService` + Fake 适配器 + 内存存储，未写入系统任务计划程序）；用户真机验证未执行；本轮执行 AI 真机验证未执行；发布前状态为**人工待验**。本阶段未勾选同步、未真实创建/更新/运行/删除任何系统计划任务。

## 5. 测试日志绝对路径

日志目录：`D:\电脑定时关机重建完整版\.build-tmp\S-PKG2-e50afcb-logs\`

| 项目 | 日志文件 |
|---|---|
| Release restore | `restore.log` |
| Release build | `release-build.log` |
| 全量测试 | `full-tests.log` |
| S-PKG 聚焦 | `focus-spkg.log` |
| S-PKG D1–D4 xUnit 聚焦 | `focus-spkg-d.log` |
| S-FINALUI1 聚焦（S_UI2 + 图标/首页/批量） | `focus-sui2.log`、`focus-finalui1.log` |
| S-CLOSEUI1 聚焦 | `focus-closeui1.log` |
| S22 聚焦 | `focus-s22.log` |
| S-UI3 聚焦 | `focus-sui3.log` |
| S-PKG harness（lifecycle/upgrade/boundary/D1/D2/D3/D4） | `harness-lifecycle.log`、`harness-upgrade.log`、`harness-boundary.log`、`harness-d1.log`、`harness-d2.log`、`harness-d3.log`、`harness-d4.log` |
| 候选发布 | `publish-candidate.log` |
| 包内容白名单/黑名单检查 | `package-content-check.log` |
| 真实候选生命周期 | `candidate-lifecycle.log` |
| 候选树安全 | `candidate-tree-safety.log` |
| UIA 冒烟第 1/2/3 轮 | `smoke-round1.log`、`smoke-round2.log`、`smoke-round3.log` |
| SHA-256 最终复算 | `final-hash-verify.ps1`（脚本）+ 输出见第 3 节 |

截图证据目录：`D:\电脑定时关机重建完整版\S-PKG-work包\S-PKG2-截图证据\`（home/weekday/onetime/tasks/office/about 共 6 张 150% PNG，冒烟通过轮次生成）。

## 6. 提交清单

1. **打包脚本最小修改提交**：`tools/test/Invoke-ASUI2Smoke.ps1`（仅版本正则 `S-UI2` → `S-(?:UI2|PKG2)` 一行 + 说明注释；文件与理由已提前报告）。单独提交，不混入其他文件。
2. **阶段文书与结果记录提交**：`AutoShutdown-V2-S-PKG2-最终打包执行书.md` + `S-PKG2-结果记录.md`（本文件）+ `S-PKG-work包/S-PKG2-截图证据/`（6 张 PNG）。
3. **未提交（明确排除）**：六张已跟踪修改的旧 S-UI2 截图（`S-UI2-截图证据/*-150percent.png`）、LibreOffice MSI、历史工作包（S-PKG-D2/D3/D4、S-UI1-D1…D4、S-UI2-D1 执行书/记录、S-PKG-B2/B3/B4 记录、S-PKG 最终独立结果记录、`_*_extracted.txt`）、S-UI2-D1 未闭环证据、`diag-btns.ps1`、`diag-fit.ps1`、`.build-tmp/`、`artifacts/`（候选二进制、ZIP、安装包、manifest，既有仓库规则已 gitignore）。以上均未删除、未移动、未覆盖。

## 7. 最终 git status

（结果记录提交后附快照与 `git diff --check`，见阶段报告。）

## 8. 人工待验清单

1. 干净真机安装/升级/回滚/卸载（需要真实管理员环境，本阶段只用隔离沙箱）。
2. Windows 任务计划程序真机创建/更新/运行/删除及权限失败处理。
3. 真实电源执行（关闭 TestMode + RealPowerEnabled）在测试机上的行为。
4. DPI 100% / 125% 完整电池（本机单显示器 150% 无法覆盖，按设计跳过）。
5. file-symlink 候选树拒绝（需管理员/dev-mode 环境）。
6. 代码签名 / SmartScreen 信誉（无生产证书，unsigned candidate）。

## 9. 后续

完成结果记录提交后立即停止，等待总顾问独立复验与用户最终批准。未 `git push`，未上传/分发，未进入下一阶段。在总顾问确认前，候选 `S-PKG2-e50afcb` 仅为「最终发布候选」，不得称为正式发布版。
