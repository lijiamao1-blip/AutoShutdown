# AutoShutdown V2 · S-PKG3「启动缺陷返修后的最终重新打包」结果记录

- 阶段：S-PKG3（总顾问复验通过后，对修正基线 `4af4b6f` 的最终正式重新打包与完整验收）
- 执行：本阶段唯一打包执行 AI；自动化测试 + UIA 冒烟 + 启动专项全部经隔离数据根 / 隔离沙箱
- 分支：`master`
- 打包基线 HEAD：`4af4b6f03ab9a993bebdf8c24abec5df9171eed0`（短哈希 `4af4b6f`）
- 基线提交主题：`S-PKG3-recheck-correct: fix execution-book baseline + sandboxed contract-test cleanup`
- 基线提交日期：`2026-08-22T22:51:54+08:00`
- 环境：Windows 11（10.0.26200），.NET SDK 8.0.423（`C:\Users\李佳茂\Documents\Codex\2026-08-10\new-chat-5\work\.dotnet-sdk\dotnet.exe`），`net8.0-windows`，RID `win-x64`
- 性质：**用户自用候选，不对外公开分发**；`git push = 否`

## 0. 基线声明

- 唯一正式打包基线为 `4af4b6f03ab9a993bebdf8c24abec5df9171eed0`。`6e3e124` 仅为阶段起始 + 预检源基线，
  **禁止**作为最终打包基线；`S-PKG3-6e3e124` 为预检候选，未复用、未覆盖。
- 干净构建输入：构建前 `git diff HEAD -- src tests tools Directory.Build.props AutoShutdown.sln` 为空；
  工具脚本 `tools/test/Invoke-SStartupD1Loop.ps1`、`tools/test/Invoke-ASUI3Smoke.ps1` 与 HEAD 一致。
- 候选从该 HEAD 对应源码**全新** restore/build/publish，未从任何旧 artifacts 复制 EXE，未在失败后回退旧候选。
- 候选输出于 `artifacts\release\v2.0.0\S-PKG3-4af4b6f\`（独立目录）。
- 版本标签统一为 `v2.0.0-S-PKG3.4af4b6f`；ProductVersion / InformationalVersion 含完整提交
  `4af4b6f03ab9a993bebdf8c24abec5df9171eed0`。
- 候选二进制、ZIP、Helper、publish 目录与 `.build-tmp` **均不入 Git**（gitignore，已核对）。
- 全部既有 dirty/untracked 工件（六张 S-UI2 截图、LibreOffice MSI、历史工作包、`S-PKG3-6e3e124` 预检候选、
  `.build-tmp` 产物）本阶段全部保留：未 reset / checkout / 删除 / 覆盖 / 暂存 / 提交。
- 未修改任何产品业务功能、测试脚本或安全边界（`git diff HEAD --stat -- src/` 为空）。
- 本阶段不 `git push`，不上传 / 不分发 / 不装生产；完成结果记录提交后立即停止。

## 1. 验证矩阵（真实执行 / 真实数量 / 真实退出码）

| 项 | 方式 | 结果 |
|---|---|---|
| 0. 预检 | master / HEAD=`4af4b6f...` / staging 空 / 工具脚本干净 / `git diff --check` | ✅ 全过，exit 0 |
| 1. Release restore | `dotnet restore AutoShutdown.sln -c Release` | ✅ exit 0（全部 up-to-date） |
| 2. Release build | `dotnet build AutoShutdown.sln -c Release` | ✅ **0 警告 / 0 错误**，exit 0 |
| 3. .NET Release 全量测试（最终复跑） | `dotnet test AutoShutdown.sln -c Release --no-build` | ✅ **1767 通过 / 0 失败 / 0 跳过**，exit 0 |
| 3'. 全量首次运行 | 同命令，第一遍 | ⚠️ **2/1767 失败**（`S15_SchedulerIdleIntegrationTests.IdleTask_WithNoWarning_ExecutesAfterArming` Executing/Executed 时序、`S_UI2_MultiTaskHomeTests.DisableOneTask_DisabledDoesNotFire_OtherStillFires` 3000ms 超时）；聚焦单测复跑 2/2 通过（23ms）确认负载时序抖动，非回归；最终全量复跑干净。**首次失败文本日志已被干净复跑覆盖，仅保留最终 PASS 的 test-full.log**（如实说明，未伪造） |
| 4. 聚焦启动-生命周期 | `--filter` 启动 D1 生命周期 | ✅ 16 通过 / 0 失败 / 0 跳过 |
| 5. 聚焦 S23-CP5-远程 | `S23_` 相关 | ✅ 15 通过 / 0 失败 / 0 跳过 |
| 6. 聚焦 S22 任务同步 | `S22_` | ✅ 115 通过 / 0 失败 / 0 跳过 |
| 7. 聚焦 S-UI3 启动器 | `S_UI3_UiTestLauncherTests` | ✅ 12 通过 / 0 失败 / 0 跳过 |
| 8. 聚焦 S-PKG 打包 | `S_PKG` | ✅ 72 通过 / 0 失败 / 0 跳过 |
| 9. 聚焦 S-PKG D | `S_PKG_D` | ✅ 45 通过 / 0 失败 / 0 跳过 |
| 10. harness 边界 | `Invoke-SPBoundaryTests` | ✅ 12 通过 / 0 失败 / 0 跳过 |
| 11. harness D1 所有权 | `Invoke-SPD1OwnershipTests` | ✅ 60 通过 / 0 失败 |
| 12. harness D2 reparse | `Invoke-SPD2ReparseTests` | ✅ 51 通过 / 0 失败 |
| 13. harness D3 祖先链 | `Invoke-SPD3AncestorTests` | ✅ 41 通过 / 0 失败 |
| 14. harness D4 候选树 | `Invoke-SPD4CandidateTreeTests` | ✅ 30 通过 / 0 失败 / 1 跳过 |
| 15. harness 隔离根删除边界 | `Invoke-ASUI3RemoveBoundaryTests` | ✅ 34 通过 / 0 失败 |
| 16. 包内容白名单/黑名单 | `check-package-content.ps1` | ✅ **PASS**：37 文件全部白名单内，无禁用名称/路径/文本（含 6e3e124/e50afcb 等泄漏） |
| 17. reparse 扫描 | 候选树递归 | ✅ 0 reparse point |
| 18. SHA-256 复算 | `final-hash-verify.ps1` | ✅ **PASS**：EXE/ZIP 与 SHA256SUMS 逐项一致 + Helper 独立复算 |
| 19. 版本-HEAD 一致性 | `final-hash-verify.ps1` | ✅ **PASS**：ProductVersion 含完整提交；candidate.txt/json；git HEAD 匹配 |
| 20. Helper 冒烟 | 无参启动候选 Helper | ✅ 拒绝 `helper-error: invalid arguments`，无挂起 |
| 21. 安装/升级/回滚/卸载/重装 | `candidate-lifecycle.ps1`（真实候选，隔离沙箱） | ✅ **19 通过 / 0 失败 / 0 跳过** |
| 22. UIA 冒烟 | `Invoke-ASUI3Smoke.ps1 -Rounds 2`（候选 EXE） | ✅ **连续两轮，52/52 断言全过**，exit 0 |
| 23. 启动专项 30 轮 | `Invoke-SStartupD1Loop.ps1 -Rounds 30`（候选 EXE） | ⚠️ **真实 28/30**（见 §4） |
| 24. 独立 close 诊断（A/B） | UIA Close 8 轮 + PostMessage(WM_CLOSE) 8 轮 | ✅ **16/16** 稳定隐藏（~210ms） |
| 25. 空白检查 | `git diff --check` | ✅ exit 0 |

## 2. 候选详情（全部含 `S-PKG3.4af4b6f` 标签）

| 产物 | 绝对路径 | 大小（字节） | SHA-256 |
|---|---|---|---|
| 候选 EXE（自包含单文件） | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG3-4af4b6f\AutoShutdown-v2.0.0-S-PKG3.4af4b6f.exe` | 156463953 | `8c99949d95f719fa9f5d94f9a988312d9c4235e33003672fb36ba0438061aa9f` |
| framework-dependent ZIP | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG3-4af4b6f\AutoShutdown-v2.0.0-S-PKG3.4af4b6f-win-x64-framework-dependent.zip` | 1021319 | `da3bc003682affb7b84fbe15841a8e730aabd280ca778a71da1db20927f8e36e` |
| OfficeSaveHelper | `D:\电脑定时关机重建完整版\artifacts\release\v2.0.0\S-PKG3-4af4b6f\AutoShutdown.OfficeSaveHelper.exe` | 67987734 | `33ea44138e908f71ea20e9f1d06becbc14765bda653bab9824268eb9ca4e374e` |

- **ProductVersion（EXE 与 Helper 一致）**：`v2.0.0-S-PKG3.4af4b6f+4af4b6f03ab9a993bebdf8c24abec5df9171eed0`
  （InformationalVersion=`v2.0.0-S-PKG3.4af4b6f`，`IncludeSourceRevisionInInformationalVersion` 追加完整提交）。
- 候选 manifest（已存在，真实生成）：`artifacts\release\v2.0.0\manifests\S-PKG3-4af4b6f-candidate.txt` / `-candidate.json` / `-SHA256SUMS.txt`
- 本阶段新增：`S-PKG3-4af4b6f-build-report.txt`、`S-PKG3-4af4b6f-version-git.txt`（同目录，gitignore 工件）
- 签名状态：`unsigned-candidate`（仓库无正式生产代码签名证书；未伪造签名，首次运行 SmartScreen 属预期）

### Helper 构建口径（如实说明）

- 预检候选 Helper 为 9,641,472 字节（为已裁剪单文件）；本阶段复现该口径时裁剪会在 TreatWarningsAsErrors 下触发
  IL2026（Core 反射式 System.Text.Json），故最终采用与 App EXE 一致的**未裁剪自包含单文件**（67,987,734 字节），
  并重新生成 `AutoShutdown.OfficeSaveHelper.runtimeconfig.json`（`includedFrameworks: Microsoft.NETCore.App 8.0.29`）。
  尺寸差异系裁剪配置所致，非缺陷；已如实记录，未伪装成与预检一致。

## 3. 独立验证（非验收循环本身）

- **独立 close 诊断（A/B，本会话 23:21）**：UIA WindowPattern.Close 8 轮 + 真实用户等效 PostMessage(WM_CLOSE) 8 轮，
  全部在 ~210ms 内隐藏窗口（8/8 + 8/8 = **16/16**）→ 应用「关闭→隐藏到托盘」处理器本身可靠。
- **UIA 冒烟连续两轮（52/52）**：每轮覆盖 6 项核心口径——激活（activated）、关闭到托盘（winClose）、
  日志顺序（traySeq）、残留=0（residual）、正式目录不变（formalUnchanged）、清理=Deleted（cleanup），
  两轮全部通过。独立于 30 轮循环。
- **诊断用 12 轮循环（带 winclose 逐采样 trace，23:19，仅诊断非验收）**：9/12，另 3 轮同样仅 `window_close_ok`
  抖动；trace 显示失败轮窗口全程 `IsWindowVisible=True`（close 未生效），通过轮 212ms 内隐藏。为归档保留。

## 4. 启动专项 30 轮 —— 真实口径 28/30

- 命令：`Invoke-SStartupD1Loop.ps1 -Exe <候选 EXE> -Rounds 30 -ReportPath <logs>`，每轮独立隔离数据根、
  TestMode=true、RealPowerEnabled=false、空 RunCommands/CloseApps、默认关闭。
- **真实结果：28 通过 / 2 失败（第 4、13 轮）**，CSV 与完整控制台日志保留。
- 两轮失败项**仅 `window_close_ok=False`**（UIA 关闭后 10s 内未观测到窗口隐藏到托盘）；该轮其余全部绿色：
  `activated=TRUE`、`traySeq=TRUE`（TrayExitRequested→ApplicationStopping→ApplicationStopped 顺序正确）、
  `residual=0`、`formalUnchanged=True`、`cleanup=Deleted`、二次启动激活/转发正常、精确 PID 真实托盘退出正常。
- 失败证据完整保留：`round-4-fail-39360/`、`round-13-fail-24104/`（各含该轮完整 app 日志）。
- **已知非阻断项（用户已接受）**：在快速连续自动化启动（约 7% 概率）下，「快速关闭窗口」存在自动化时序抖动 /
  潜在重新显示竞态。**不得改写为 30/30，不得表述为「缺陷已彻底消除」**；标记为：**用户接受的已知非阻断项，
  公开分发前建议继续观察**。本轮未通过重跑/多跑取成功/强杀/删除失败证据美化结果。
- 独立诊断归因：App「关闭→隐藏到托盘」处理器为同步 `e.Cancel=true; Hide()`，A/B 诊断 16/16 与 UIA 冒烟
  52/52 均证明其可靠；抖动出现在自动化快速关闭与延迟激活重显示交错的高负载窗口。

## 5. Windows 任务计划程序记录口径

自动化 S22 聚焦 115 项全部使用 `AutoShutdown.Core.Tasks.TaskService` + Fake 适配器 + 内存存储，
**未创建/更新/运行/删除任何真实系统计划任务**；本阶段未勾选同步、未远程监听启用、未导入证书/HMAC/配对。
**真机任务计划验证仍为人工待验**。

## 6. 测试日志与证据（本记录提交内）

- 证据目录：`S-PKG-work包\S-PKG3-结果记录-证据\`
  - `loop-30-s-pkg3-4af4b6f.csv`、`loop-30-console.log` —— 28/30 原始 CSV 与控制台（含失败轮明细）
  - `uia-smoke-2rounds.csv` —— UIA 冒烟连续两轮（52/52）
  - `round-4-fail-39360/autoshutdown.log`、`round-13-fail-24104/autoshutdown.log` —— 两轮失败证据
  - `final-hash-version-verify.txt` —— 版本/HEAD/SHA-256 复算 PASS
  - `package-content-check.txt` —— 包内容白名单 PASS（37 文件）
  - `diag-close-ab-16of16.log` —— 独立 close A/B 诊断（16/16）
- 完整工作日志（gitignore，不入 Git）：`.build-tmp\S-PKG3-4af4b6f-logs\`
  （restore/build/test-full/focused/harness/publish/hash/content/lifecycle/ui3/loop/trace 全部原始输出）。

## 7. 提交与停止状态

- 本结果记录 + 上述证据以**独立提交**落地（候选与 `.build-tmp` 不入 Git）。
- `git push = 否`；不上传、不分发、不装生产。提交后立即停止。
