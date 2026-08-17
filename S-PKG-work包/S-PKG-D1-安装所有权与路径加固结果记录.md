# S-PKG D1 安装所有权与破坏性路径加固 结果记录（独立执行书收尾）

- 阶段: S-PKG D1（安装所有权门禁 `AutoShutdown.owner.json` + 危险路径硬守卫 + 备份元数据绑定 + 清单式删除 + DataRoot 保护）
- 日期: 2026-08-18
- 谱系（更正版）:
  - 功能基线: `97cef24`（S23-D1 result record: remote DoS boundaries + HMAC comment fix verified）
  - 源码提交: `fe54711`（A3 checkpoint: data-root override + minimal version entry for S-PKG sandboxing）
  - 释放包 EXE: `artifacts/release/v2.0.0/PKG-fe54711/AutoShutdown-v2.0.0-PKG.fe54711.exe`
  - SHA-256: `e13191ea4a66806f9003d12e99d2f19454b09c746d48ab6078843b622b7aa90b`
- 当前 HEAD（续接点）: `ba7b017`（B4: release-acceptance GUI smoke + uninstall/reinstall launchability tests）
- 执行人: S-PKG 唯一执行 AI（全部在 `$env:TEMP\as-*` 临时沙箱；未触碰真实用户数据 / 真实安装 / 电源 / 注册表 / 防火墙 / 任务计划）
- 结论: 全部既有装置 + D1 新增装置 + GUI 冒烟 + Release build/test 全绿；实现 + 记录已本地提交；未 git push、未进入 S24。

## 1. 依据

S-PKG D1 续接包（短续接包，2026-08-18 检查点）：`tools/SPkg-Lifecycle.ps1` 的 D1 核心实现
在续接点已就位但**有意未提交**（中间态会让既有 harness 变红）。本执行书须：

1. 只读检查 git diff / 脚本解析 / 续接包边界；**保留**改动（不 reset / checkout / 覆盖 / 全仓重扫）；
2. 先完成既有 A-3b / B-2 / B-3 / B-4 装置的所有权标记适配与回归，再新增 D1 所有权测试与契约测试；
3. 以**全部既有装置 + GUI 冒烟 + Release build/test + 独立记录**为完成条件（不把「探针通过」当完成）；
4. 独立结果记录须做谱系更正，**不覆盖**原 S-PKG 最终/B2/B3/B4 记录；
5. 本地提交后报告数量/退出码/工作树并停止（不得 git push、不得进入 S24）。

## 2. 本阶段新增/修改内容

| 文件 | 职责 |
| --- | --- |
| `tools/SPkg-Lifecycle.ps1` | D1 核心：`Write/Read-ASOwnerMarker`、`Test-ASInstallOwnership`（新/空放行，非空未拥有 fail-closed）、`Test-ASForbiddenPath`（fs 根/主目录/SystemRoot/仓库根/artifacts）、`backup.json` 的 `sourceInstallDir` 绑定、`Remove-ASOwnedFiles`+`Remove-ASEmptyDirsUnder` 清单式删除、`Test-ASSpkgBackupsOwned` DataRoot 保护。**新增 `Copy-ASDirContents` 逐子项合并复制**（修复候选/备份复制到已存在同名子目录时的 `Copy-Item` 嵌套真 bug）。所有破坏性操作（替换/回滚/卸载/重装）统一先过所有权门禁。 |
| `tools/test/Invoke-SPkgLifecycleTests.ps1` | A-3b：加 UTF-8 BOM；修 L105 `WriteAllText(@{...}\|ConvertTo-Json,…)` 解析错误（先存变量）；在两处失败注入场景后加 `Write-ASOwnerMarker` 种子 |
| `tools/test/Invoke-SPkgUpgradeTests.ps1` | B-2：初始 V1 EXE 与 `Reset-V1State` 内各加 1 处所有权标记种子 |
| `tools/test/Invoke-SPBoundaryTests.ps1` | B-3：初始 V1 EXE 后加 1 处所有权标记种子 |
| `tools/test/Invoke-ASReleaseSmoke.ps1` | B-4：初始安装槽加所有权标记种子（`Get-ASRelFileList` 全量清单） |
| `tools/test/Invoke-SPD1OwnershipTests.ps1` | **新增** D1 所有权装置：A 危险路径 / B 所有权状态机 / C fail-closed / D 有效所有权+清单删除 / E DataRoot 误指 / F 嵌套回归（60 断言） |
| `tests/AutoShutdown.Tests/S_PKG_D1_OwnershipContractTests.cs` | **新增** D1 契约测试（9 个 xUnit 静态事实：标记/门禁/守卫/绑定/清单删除/禁整目录递归/DataRoot 保护/装置覆盖/既有 harness 种子/合并复制无嵌套） |

## 3. 验证结果（全量，按完成条件）

| 装置 | 结果 | 退出码 |
| --- | --- | --- |
| A-3b 生命周期 | **21 / 21** pass | 0 |
| B-2 升级与回滚 | **41 / 41** pass | 0 |
| B-3 系统集成边界 | **12 / 12** pass | 0 |
| B-4 发布验收冒烟（GUI，UIA 驱动真实 RC EXE，8 节 56 断言） | **56 / 56** pass | 0 |
| D1 所有权装置（新增） | **60 / 60** pass | 0 |
| .NET 全量测试（Release，`-c Release`） | **1483 通过 / 0 失败 / 0 跳过**（1474 基线 + 9 新 D1 契约事实） | 0 |
| Release build | 0 Warning / 0 Error | 0 |
| `SPkg-Lifecycle.ps1` 解析 | 无语法错误 | — |

> 注：B-4 冒烟输出尾部 `EXITCODE=` 为空是 PowerShell 5.1 经管道输出时的 `$LASTEXITCODE` 展示缺陷，
> 脚本本身末尾 `if ($fail -gt 0) { exit 1 }`；上述 pass/fail 计数为脚本自报，全绿即退出 0。

## 4. 过程中发现并修复的真 bug（如实记录，均入回归）

1. **A-3b L105 解析错误**（续接包已声明）：`[System.IO.File]::WriteAllText($path, @{...} | ConvertTo-Json, $enc)` 语法不合法。
   修复为先 `$contaminatedConfig = @{...} | ConvertTo-Json` 再写入。另为 A-3b 补 UTF-8 BOM（PowerShell 5.1 中文必需）。
2. **候选/备份复制 `Copy-Item` 嵌套真 bug**（B-4 回归暴露，续接包 18 探针未覆盖）：`Replace-ASBinary` 候选复制循环对
   **已存在的同名子目录**用 `Copy-Item <源目录> -Destination <目标> -Recurse`，PowerShell 将其嵌套成
   `<目标>\<同名>\…`。B-4 C8 场景：候选含 `win-x64-framework-dependent/` 目录，安装槽已有同名目录 → 产生
   `install\win-x64-framework-dependent\win-x64-framework-dependent\`，其内文件不在单层 appFiles 清单中，
   卸载 Keep 后成为**孤儿残留** → 重装因「无所有权标记的非空目录」fail-closed。回滚恢复（`Restore-ASReplaceFailure` /
   `Restore-ASInstallBackup`）的同名子目录复制存在同类缺陷。
   **修复**：新增 `Copy-ASDirContents`（逐文件、逐级建目录、目标已存在时合并、绝不嵌套），替换全部 4 处复制点
   （替换候选复制 / 重装候选复制 / 替换失败恢复 / 安装备份恢复）。**持久化回归**：D1 装置新增 section F
   （候选子目录叠到已有同名子目录 → 无双重嵌套、候选文件并入、既有文件保留、深层本地化目录文件在位、所有权仍 owned），
   并新增契约事实 `D1_NoNestingMergeCopy_Present`。
3. **D1 装置自身两处小缺陷**（开发期）：byte 数组越界（`1..2048` 值 >255）改为 `($_ % 251)`；`Join-Path` 三参形式不合法改为嵌套。

## 5. 谱系与提交

- 谱系更正：功能基线 **`97cef24`**、源码提交 **`fe54711`**、释放包 SHA-256 **`e13191ea4a66806f9003d12e99d2f19454b09c746d48ab6078843b622b7aa90b`**
  （与释放包 `artifacts/release/v2.0.0/PKG-fe54711/AutoShutdown-v2.0.0-PKG.fe54711.exe` 实测一致）。
- 本次本地提交：`tools/SPkg-Lifecycle.ps1` + 4 个既有 harness 适配 + 新增 D1 装置/契约测试 + 本结果记录。
- 原 S-PKG 最终 / B2 / B3 / B4 记录**未覆盖、未改动**。

## 6. 最终工作树状态

- 已提交：上述实现与测试 + D1 记录。
- 保持未跟踪、未删除、未覆盖：`S-PKG-work包/` 下原记录、S13–S23 工作包与历史证据工件、`LibreOffice_26.2.5_Win_x86-64.msi`。
- 临时调试文件（`_parsecheck.ps1`、`_dbg_b4c8.ps1`、`_dbg_emptydirs.ps1`、`_dbg_ed2.ps1`）已删除。
- 未 `git push`、未进入 S24、未新增业务功能。

## 7. 人工待验项（自动化不覆盖）

- [ ] 干净机真实安装：全新安装 / V1→V2 升级 / 卸载 Keep / 重装 / 卸载 Remove 的完整人工操作路径（含自启/防火墙/任务计划交互，见 B-3 待验清单）。
- [ ] 真实电源边界：TestMode 关闭 + RealPowerEnabled 的真实关机执行（全程 TestMode，不触真实电源）。
- [ ] 真实数据根下 `AutoShutdown.owner.json` 与 `backup.json` 的生命周期互操作（沙箱验证绑定语义，真实目录路径未验证）。
