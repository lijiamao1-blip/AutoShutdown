# S-PKG D2 最小返修结果记录（独立文件，不覆盖任何历史记录）

- 阶段: S-PKG-D2（已拥有安装目录内 NTFS junction/symlink 沿 reparse point 越界删除 —— 全链路 reparse 守卫最小返修）
- 日期: 2026-08-18
- 返修基线（起始提交）: `e1eb10d`（D1: install-ownership gate + dangerous-path hardening + nest-free merge-copy）
- 结束提交: `dabb220`（D2: junction automation harness + C# contract tests）
- 返修内全部提交:
  1. `543ce2a` D2: unified within-root + full-chain no-ReparsePoint guard, fail-closed on all destructive paths（CP2 实现）
  2. `dabb220` D2: junction automation harness (uninstall/reinstall/replace/rollback) + C# contract tests（CP3 装置 + 契约测试）
  3. 本结果记录（CP4 独立提交，随记录完成后创建）
- 执行人: S-PKG D2 执行 AI（全部在 `$env:TEMP\as-*` 临时沙箱；未触碰真实用户数据 / 真实安装 / 电源 / 注册表 / 防火墙 / 任务计划）
- 结论: **通过**（验收条件全绿，见第 6 节）

---

## 1. 缺口与返修范围（对照执行书第 3 节五项目标）

| # | 执行书目标 | 落实方式 |
| --- | --- | --- |
| 1 | 新增统一的「路径在根目录内 + 全链路无 ReparsePoint」校验，不得仅作字符串/FullPath 前缀判断 | 新增 `Test-ASPathWithinRoot`（逐分量包含 + 从根到目标逐分量探测 `ReparsePoint`）、`Get-ASRelPathReparsePoint`（拒绝 rooted/`..`，返回首个 reparse 全路径）、`Assert-ASRelPathSafe`（命中即抛错）、`Get-ASDirTreeSafe`（显式栈 + 逐层 `Get-ChildItem` 的非跟随安全枚举） |
| 2 | 所有破坏性/递归路径前 fail-closed：所有权门禁、清单删除、空目录清理、候选复制、安装备份、回滚恢复、DataRoot 备份/恢复 | 见第 3 节逐路径接入表 |
| 3 | 遇到安装目录、候选目录、备份目录或其子路径中的 junction/symlink/reparse point：在任何复制、删除、写入前拒绝，不触碰目录外内容 | 全部在操作前 `throw`（fail-closed），绝不停留/继续；装置证明目录外哨兵 + 哈希完全不变 |
| 4 | D2 自动化装置（临时目录 junction，`cmd /c mklink /J`，不依赖开发者模式符号链接权限）覆盖 uninstall/reinstall/replace/rollback 至少四条路径 + C# 契约测试 | `Invoke-SPD2ReparseTests.ps1`（8 节 51 断言）+ `S_PKG_D2_ReparseContractTests.cs`（10 个 xUnit 静态事实），见第 4 节 |
| 5 | 回归 A-3b/B-2/B-3/B-4/D1/D2、Release build、全量 Release test；报告实际数量、退出码、工作树 | 见第 5、6 节；保留全部既有未跟踪工件 |

## 2. 实际修改文件及职责

| 文件 | 状态 | 职责 |
| --- | --- | --- |
| `tools/SPkg-Lifecycle.ps1` | 修改（265+/28−） | 统一 reparse 守卫 + 七类破坏性/递归路径 fail-closed 接入（提交 `543ce2a`） |
| `tools/test/Invoke-SPD2ReparseTests.ps1` | 新增 | D2 junction 装置（8 节 51 断言，提交 `dabb220`） |
| `tests/AutoShutdown.Tests/S_PKG_D2_ReparseContractTests.cs` | 新增 | D2 静态文本契约测试（10 个 xUnit 事实，提交 `dabb220`） |
| `S-PKG-work包/S-PKG-D2-结果记录.md` | 新增 | 本结果记录（独立文件） |

未改动：原 `S-PKG-D1-安装所有权与路径加固结果记录.md`、`S-PKG-B2/B3/B4-*`、`S-PKG-最终独立结果记录.md`、`AutoShutdown-V2-S-PKG-阶段一键执行书.docx`、S13–S23 历史工作包、`LibreOffice_26.2.5_Win_x86-64.msi`。未 git push。

## 3. 关键安全不变量核对（全链路 reparse 守卫接入七类破坏性/递归路径）

| 破坏性/递归路径 | 函数/位置 | reparse 守卫 | fail-closed 行为 |
| --- | --- | --- | --- |
| 所有权门禁 | `Test-ASInstallOwnership` | 安装目录自身 + 整树 `Get-ASDirTreeSafe` 扫描 | `reparse-point` 拒绝，不进入删除/复制 |
| 清单删除 | `Remove-ASOwnedFiles` | 安装目录自身 + 每条相对路径 `Test-ASPathWithinRoot` 全链路探测 | 命中即 `throw`，绝不删除目录外文件 |
| 空目录清理 | `Remove-ASEmptyDirsUnder` | 安全枚举 `Get-ASDirTreeSafe` + 每目录 `Assert-ASRelPathSafe` | 命中即 `throw`，绝不 `Remove-Item` 到 junction |
| 候选复制 | `Copy-ASDirContents` | 源树 `Get-ASDirTreeSafe` + 目标链路 `Assert-ASRelPathSafe` | 命中即 `throw`，不复制目录外内容 |
| 安装备份 | `Replace-ASBinary` 备份步骤 | 安装目录整树 `Get-ASDirTreeSafe` + 逐文件安全复制（替换 `Copy-Item -Recurse`，含数量核对） | 命中即 `throw`，备份槽不泄漏外部内容 |
| 回滚恢复 | `Restore-ASReplaceFailure` / `Restore-ASInstallBackup` / `Restore-ASRollback` | 匹配备份路径链 + 目标/源链路 `Get-ASRelPathReparsePoint` | 命中即 `throw`，不沿 junction 恢复 |
| DataRoot 备份/恢复 | `Backup-ASDataRoot` / `Restore-ASRollback` / `Invoke-ASUninstall`(Remove) | 数据根整树扫描 + `backups\spkg` 链路校验 + 备份根删除前整树校验 | 命中即 `throw`，不触碰目录外内容 |

补充核对：绝无 `Remove-Item <InstallDir> -Recurse`（契约事实 `D2_NoWholeInstallDirRecursiveDelete` 断言 0 命中）；备份成功仍为一切替换前置；DataRoot 受保护只删除经 `AutoShutdown.owner.json` 标记的 `backups\spkg`；不改变业务契约；`ShutdownWorkflow` 仍为唯一 `IPowerService` 调用者；所有自动化全程 TestMode/Fake，未触真实电源。

## 4. D2 装置与契约测试

- `Invoke-SPD2ReparseTests.ps1`：`cmd /c mklink /J` 建临时目录 junction（不依赖开发者模式符号链接权限），8 节 A–H：
  - A 统一守卫函数（含 `..`/根路径拒绝、全链路探测到叶）；
  - B `Remove-ASOwnedFiles` 直接注入 `evil\sentinel.bin` 条目 → 拒绝；
  - C uninstall（Keep）安装目录含 junction + 攻击性 `appFiles` 条目 → 拒绝，目录外哨兵字节 + SHA-256 完全不变；
  - D reinstall → 拒绝，目录外哨兵 + 哈希完全不变；
  - E replace（含安装备份路径）→ 拒绝，目录外哨兵 + 哈希完全不变，无备份槽残留；
  - F1/F2 `Restore-ASInstallBackup` / `Restore-ASRollback` → 拒绝，目录外哨兵 + 哈希完全不变；
  - G1 候选目录含 junction → 复制拒绝；G2 DataRoot 顶层 junction → 备份拒绝；G3 `DataRoot\backups` junction → 拒绝；G4 备份根内部 junction → uninstall(Remove) 拒绝；
  - H 正常（无 junction）路径回归：replace/uninstall 正常成功，无假阳性。
  - **实际运行：pass=51 fail=0，exit 0。**
- `S_PKG_D2_ReparseContractTests.cs`：10 个静态文本 xUnit 事实（只读源码，不执行生命周期脚本、不写目录）——四个统一守卫函数存在、七类破坏性路径全部接入 reparse 校验、无整目录递归删除、装置覆盖四路径 + 哨兵/哈希断言、装置含正常路径回归。**实际运行：10/10 通过。**

## 5. 回归结果（全量，按完成条件）

| 装置 | 结果 | 退出码 |
| --- | --- | --- |
| A-3b 生命周期 | **21 / 21** pass | 0 |
| B-2 升级与回滚 | **41 / 41** pass | 0 |
| B-3 系统集成边界 | **12 / 12** pass（skip=0） | 0 |
| B-4 发布验收冒烟（GUI，UIA 驱动真实 RC EXE，8 节） | **56 / 56** pass（skip=0） | 0 |
| D1 所有权装置 | **60 / 60** pass | 0 |
| D2 reparse 装置（新增） | **51 / 51** pass | 0 |
| .NET 全量测试（Release，`--no-build`） | **1493 通过 / 0 失败 / 0 跳过**（1483 基线 + 10 新 D2 契约事实） | 0 |
| Release build | 0 Warning / 0 Error | 0 |
| `git diff --check` | 无空白错误 | 0 |
| `SPkg-Lifecycle.ps1` 解析 | 无语法错误（PARSE OK；tokens=7485） | — |

## 6. 返修验收核对

- [x] 统一「路径在根目录内 + 全链路无 ReparsePoint」校验已实现且非仅字符串/FullPath 前缀判断；
- [x] 所有权门禁 / 清单删除 / 空目录清理 / 候选复制 / 安装备份 / 回滚恢复 / DataRoot 备份与恢复全部 fail-closed 接入 reparse 校验；
- [x] 安装目录、候选目录、备份目录或其子路径含 junction/symlink/reparse point 时，在任何复制/删除/写入前拒绝，目录外哨兵文件与 SHA-256 哈希完全不变（装置逐路径证明）；
- [x] D2 装置覆盖 uninstall/reinstall/replace/rollback 至少四条路径，全部实际运行通过；C# 契约测试通过；
- [x] A-3b/B-2/B-3/B-4/D1/D2、Release build、全量 Release test 全绿；原 S-PKG-D1/B2/B3/B4/最终结果记录与全部未跟踪工件原样保留；未 git push。

## 7. 过程中发现并处理的问题（如实记录）

1. **PS 5.1 中文路径 + 无 BOM 临时脚本导致 dot-source 路径乱码**：D2 装置开发期临时脚本缺 UTF-8 BOM 且含中文，PS 5.1 按 ANSI 读入使仓库路径乱码。解决：临时脚本改 ASCII-only，仓库路径作 `param` 传入；交付的仓库 harness 与 `SPkg-Lifecycle.ps1` 均为 UTF-8 BOM。
2. **`Remove-Item` 对 junction 目录无 `-Recurse` 会挂起**：实测驱动设计——空目录清理与标记删除对 junction 一律先链路径探测拒绝，绝不尝试删除 junction 本身。
3. **`Get-DirHashSnapshot` 单文件目录被管道解包**（数组塌缩成单元素，`.Count` 在 StrictMode 下抛错）：用一元逗号 `return ,[object[]](...)` 修复。
4. **装置多处缺失目录先创建**（`$outsideG1/G2/G3/G4` 先于 `Set-Content`）：补 `New-Item` 列表。

## 8. 未执行 / 人工待验项（自动化不覆盖，标「未执行/人工待验」）

- [ ] 真机检查：真实安装目录（非 `$env:TEMP`）内含恶意 junction 时，实际卸载/重装/替换/回滚是否同样 fail-closed（沙箱已验证语义，真实目录未执行）。
- [ ] 真实设备电源边界与真实安装互操作（全程 TestMode，不触真实电源）。
- [ ] 干净机真实安装/升级/卸载 Keep/重装/卸载 Remove 完整人工操作路径（见 B-3 待验清单）。

## 9. 遗留问题（最多 3 项）

1. `Test-ASPathWithinRoot` / `Get-ASDirTreeSafe` 对目标不存在但链上某层为 junction 的场景以「探测到该层 reparse」拒绝——行为正确但消息需定位到具体层；已在装置 A 节覆盖。
2. `Replace-ASBinary` 备份改为逐文件复制，性能随安装目录文件数线性增长；对本应用规模无影响，未做优化。
3. PSScriptAnalyzer 预置警告（`Ensure-ASSpkgBackupsRoot`、`Replace-ASBinary` 动词）为历史遗留，本返修未引入，未处理。

## 10. 下一步

满足 D2 阶段验收。可以进入下一阶段（仍需 ChatGPT 独立验收后决定；本返修未 git push、未进入 S24）。
