# S-PKG D3 最小返修结果记录（独立文件，不覆盖任何历史记录）

- 阶段: S-PKG-D3（D2 只检查传入 InstallDir 向下的路径，未检查文件系统根到 InstallDir 的父链 —— 卷根→目标完整祖先链 reparse 守卫最小返修）
- 日期: 2026-08-18
- 返修基线（起始提交）: `fba5959`（D2 独立结果记录）
- 结束提交: `fc4899b`（D3: parent-junction automation harness + C# contract tests + UserProfile-empty fix）
- 返修内全部提交:
  1. `a026acf` D3: volume-root -> target full ancestor-chain reparse guard, fail-closed on all entries（CP2 实现）
  2. `fc4899b` D3: parent-junction automation harness (ownership/uninstall/reinstall/replace/rollback) + C# contract tests + UserProfile-empty fix（CP3 装置 + 契约测试 + 脆弱性修复）
  3. 本结果记录（CP4 独立提交，随记录完成后创建）
- 执行人: S-PKG D3 执行 AI（全部在 `$env:TEMP\as-*` 临时沙箱；未触碰真实用户数据 / 真实安装 / 电源 / 注册表 / 防火墙 / 任务计划）
- 结论: **通过**（验收条件全绿，见第 6 节；B-4 首跑 5 项 GUI 渲染抖动如实记录于第 5 节）

---

## 1. 缺口与返修范围（对照执行书 7 项目标）

| # | 执行书目标 | 落实方式 |
| --- | --- | --- |
| 1 | 新增或修正统一绝对路径链路守卫：对任意 InstallDir、CandidateDir、DataRoot、BackupDir 及其操作目标，从卷根开始逐分量检查到目标；任何现存祖先分量是 junction/symlink/reparse point 都必须 fail-closed | 新增 `Test-ASFullChainSafe` / `Assert-ASFullChainSafe`（`[System.IO.Path]::GetPathRoot` 定位卷根 → 从根逐分量 `Get-Item -LiteralPath` 探测到目标；现存祖先命中 `ReparsePoint` 即返回 `Reason='reparse-point'`；`Assert-` 命中即 `throw`） |
| 2 | 不得先解析为物理目标后再判断；必须保留调用者传入的词法路径逐段检查 | 守卫以 `Resolve-ASPath`（= `[System.IO.Path]::GetFullPath`，纯词法，不跟随 junction）为基础路径，逐段对**词法路径**探测；全仓禁止 `Resolve-Path` 命令（契约事实 `D3_NoPhysicalResolutionBeforeProbing` 断言 0 命中） |
| 3 | 对不存在的目标，也必须检查到最后一个已存在祖先目录；该祖先若为 reparse point 必须拒绝 | 逐分量探测遇缺失分量即 `if ($null -eq $item) { break }`（此前已存在祖先全部探测过；reparse 祖先早已命中返回）——装置 I 节证明不存在目标落在父 junction 下被拒绝且目录外零写入 |
| 4 | 所有权门禁、清单删除、空目录清理、候选复制、安装备份、回滚恢复、DataRoot 备份/恢复/删除均接入此完整祖先链守卫 | 见第 3 节逐路径接入表（显式入口断言 + 共享原语 `Test-ASPathWithinRoot` / `Get-ASDirTreeSafe` / `Get-ASRelPathReparsePoint` 内部全链路探测，所有调用者继承保护） |
| 5 | 新增 D3 自动化回归：父目录 junction -> outside 的隔离路径，传入 alias\install；覆盖 ownership/uninstall/reinstall/replace/rollback，验证全部拒绝且 outside 中哨兵文件 SHA-256 不变；覆盖 CandidateDir 和 DataRoot 位于父 junction 下的拒绝 | `Invoke-SPD3AncestorTests.ps1`（10 节 41 断言，A–J，见第 4 节） |
| 6 | 修复测试在 UserProfile 为空的非交互环境下的脆弱性：不得因空路径绑定异常中断；安全跳过该专属断言或使用明确有效的临时路径；不降低真实桌面环境覆盖 | `Invoke-SPD1OwnershipTests.ps1` 用户主目录断言改为 `IsNullOrWhiteSpace($userProfile)` 时空跳（打印 SKIP，不中断），非空时仍执行原断言；`Test-ASForbiddenPath` 对空 UserProfile 安全跳过比较（契约事实 `D3_UserProfileEmptyFragility_Fixed` 断言两侧） |
| 7 | 回归 A-3b/B-2/B-3/B-4/D1/D2/D3、Release build、全量 Release test；报告真实数量、退出码、工作树并停止 | 见第 5、6 节；保留全部既有未跟踪工件；未 git push |

## 2. 实际修改文件及职责

| 文件 | 状态 | 职责 |
| --- | --- | --- |
| `tools/SPkg-Lifecycle.ps1` | 修改（98+/15−） | 新增 `Test-ASFullChainSafe` / `Assert-ASFullChainSafe` + 全部破坏性/递归入口 fail-closed 接入 + `Test-ASForbiddenPath` UserProfile 空值安全跳过（提交 `a026acf`） |
| `tools/test/Invoke-SPD3AncestorTests.ps1` | 新增 | D3 父链 junction 装置（10 节 41 断言，提交 `fc4899b`） |
| `tools/test/Invoke-SPD1OwnershipTests.ps1` | 修改（9±） | UserProfile 为空脆弱性修复（安全跳过，不降低真实桌面覆盖，提交 `fc4899b`） |
| `tests/AutoShutdown.Tests/S_PKG_D3_AncestorChainContractTests.cs` | 新增 | D3 静态文本契约测试（14 个 xUnit 事实，提交 `fc4899b`） |
| `S-PKG-work包/AutoShutdown-V2-S-PKG-D3-最小返修执行书.md` | 新增（未跟踪工件） | D3 最小返修执行书 |
| `S-PKG-work包/S-PKG-D3-结果记录.md` | 新增 | 本结果记录（独立文件） |

未改动：原 `S-PKG-D1-安装所有权与路径加固结果记录.md`、`S-PKG-D2-结果记录.md`、`S-PKG-B2/B3/B4-*`、`S-PKG-最终独立结果记录.md`、`AutoShutdown-V2-S-PKG-阶段一键执行书.docx`、S13–S23 历史工作包、`LibreOffice_26.2.5_Win_x86-64.msi`。未 git push。

## 3. 关键安全不变量核对（卷根→目标完整祖先链守卫接入全部破坏性/递归路径）

| 破坏性/递归路径 | 函数/位置 | 祖先链守卫接入 | fail-closed 行为 |
| --- | --- | --- | --- |
| 所有权门禁 | `Test-ASInstallOwnership` | 操作前 `$chain = Test-ASFullChainSafe -Path $full`，命中即返回 `Reason='reparse-point'; Message='install dir chain unsafe: ...'` | 拒绝，不进入删除/复制 |
| 清单删除 | `Remove-ASOwnedFiles` | `Test-ASPathWithinRoot` 内部升级为全链路探测 + 自身 `Assert-ASFullChainSafe -Path $base -Action 'remove owned files'` | 命中即 `throw`，绝不删除目录外文件 |
| 空目录清理 | `Remove-ASEmptyDirsUnder` | 经 `Get-ASDirTreeSafe`（内部 `Test-ASFullChainSafe -Path $base` 全链路探测） | 命中即 `throw`，绝不 `Remove-Item` 到 junction |
| 候选复制 | `Copy-ASDirContents` | `$dstChain = Test-ASFullChainSafe -Path $dst`（复制前） | 命中即 `throw`，不复制目录外内容 |
| 安装备份 | `Replace-ASBinary` | 数据根 `Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'` + 候选 `Assert-ASFullChainSafe -Path $candFull -Action 'use candidate dir'` | 命中即 `throw`，备份槽不泄漏外部内容 |
| 回滚恢复 | `Restore-ASRollback` | 数据根 `Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'` | 命中即 `throw`，不沿 junction 恢复 |
| DataRoot 备份/恢复/删除 | `Backup-ASDataRoot` / `Invoke-ASUninstall`(Remove) | 备份：`Assert-ASFullChainSafe -Path $dataRootFull -Action 'back up data root'` **先于** `New-Item`；删除：`Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'` | 命中即 `throw`，不触碰目录外内容 |
| 重装候选 | `Invoke-ASReinstall` | `Assert-ASFullChainSafe -Path $candFull -Action 'use candidate dir'` | 命中即 `throw` |

补充核对：守卫基于**词法路径**逐段探测（`Get-Item -LiteralPath $probe` 返回 reparse 点自身而非物理目标，不透明解析）；卷根自身若是 reparse 点同样拒绝；不存在目标检查到最后一个已存在祖先；`Test-ASForbiddenPath` 对空 UserProfile 安全跳过（不因 `GetFullPath('')` 异常中断测试）。

## 4. D3 装置与契约测试

- `Invoke-SPD3AncestorTests.ps1`：`cmd /c mklink /J` 建父目录 junction（`alias` → `outside`，不依赖开发者模式符号链接权限），10 节 A–J：
  - A 统一祖先链守卫函数（卷根逐分量、reparse 祖先命中、不存在目标到最后一个已存在祖先）；
  - B 所有权门禁：传入 `alias\install`（install 本身非 reparse point，父链含 junction）→ `reparse-point` 拒绝；
  - C uninstall（Keep）→ 拒绝，`outside\install` 哨兵 SHA-256 完全不变；
  - D reinstall → 拒绝，哨兵 + 哈希完全不变；
  - E replace（含安装备份路径）→ 拒绝，哨兵 + 哈希完全不变，无备份槽残留；
  - F1 `Restore-ASInstallBackup` / F2 `Restore-ASRollback` → 拒绝，哨兵 + 哈希完全不变；
  - G 候选目录位于父 junction 下 → replace/reinstall 复制拒绝；
  - H DataRoot 位于父 junction 下 → 备份/删除拒绝；
  - I 不存在的目标（`alias\install\sub\missing`）落在父 junction 下 → 所有权 `reparse-point`（非 `new`），目录外零写入；
  - J 正常（无 junction）路径回归：replace/uninstall 正常成功，无假阳性。
  - **实际运行：pass=41 fail=0，exit 0。**
- `S_PKG_D3_AncestorChainContractTests.cs`：14 个静态文本 xUnit 事实（只读源码，不执行生命周期脚本、不写目录）——统一守卫存在且从卷根逐分量、词法路径（`Resolve-ASPath`/`GetFullPath`）、`Get-Item -LiteralPath $probe` 探测、`Resolve-Path` 0 命中、不存在目标断点、七类破坏性路径全部接入、共享原语全链路、装置覆盖五条路径 + 哨兵/哈希 + CandidateDir/DataRoot 父 junction、UserProfile 空值脆弱性两侧修复。**实际运行：14/14 通过。**

## 5. 回归结果（全量，按完成条件）

| 装置 | 结果 | 退出码 |
| --- | --- | --- |
| A-3b 生命周期 | **21 / 21** pass | 0 |
| B-2 升级与回滚 | **41 / 41** pass | 0 |
| B-3 系统集成边界 | **12 / 12** pass（skip=0） | 0 |
| B-4 发布验收冒烟（GUI，UIA 驱动真实 RC EXE，8 节） | 首跑 **51 pass / 5 fail**（exit 1），重跑 **56 / 56** pass（skip=0，exit 0） | 见下节 |
| D1 所有权装置 | **60 / 60** pass | 0 |
| D2 reparse 装置 | **51 / 51** pass | 0 |
| D3 祖先链装置（新增） | **41 / 41** pass | 0 |
| .NET 全量测试（Release，`--no-build`） | **1507 通过 / 0 失败 / 0 跳过**（1493 基线 + 14 新 D3 契约事实） | 0 |
| Release build | 0 Warning / 0 Error | 0 |
| `git diff --check` | 无空白错误 | 0 |
| `SPkg-Lifecycle.ps1` 解析 | 无语法错误（PARSE OK；tokens=7922） | — |

**B-4 首跑 5 项失败的如实说明**：首跑在 A-3b/B-2/B-3 连续运行后执行，C3 首页分区渲染 8 项中断言 3 项通过（创建定时任务区/时间模式/倒计时）、5 项失败（关机 RadioButton/创建任务 button/当前任务区/调度服务区/运行正常），全部为 UIA `Find-Descendant` 未命中。D3 仅修改 `tools/SPkg-Lifecycle.ps1` 与测试文件，冒烟装置与真实 RC EXE 自 B4 基线（`ba7b017`）后字节级未变，C3 区间无任何 lifecycle 调用——判定为 GUI 渲染时序环境性抖动而非代码回归。立即重跑：**56/56 pass、exit 0**。首跑与重跑结果均如实记录。

## 6. 返修验收核对（对照执行书 7 项目标）

- [x] 统一绝对路径祖先链守卫已实现：从卷根逐分量检查到目标，任何现存祖先 reparse 即 fail-closed；
- [x] 守卫保留调用者传入的词法路径逐段检查，绝无先解析物理目标（`Resolve-Path` 全仓 0 命中）；
- [x] 不存在目标也检查到最后一个已存在祖先目录，该祖先为 reparse 即拒绝（装置 I 节，目录外零写入）；
- [x] 所有权门禁 / 清单删除 / 空目录清理 / 候选复制 / 安装备份 / 回滚恢复 / DataRoot 备份·恢复·删除全部接入完整祖先链守卫；
- [x] D3 装置用「父目录 junction -> outside」的 alias\install 覆盖 ownership/uninstall/reinstall/replace/rollback，全部拒绝且 outside 哨兵 SHA-256 完全不变；CandidateDir / DataRoot 位于父 junction 下拒绝已覆盖；
- [x] UserProfile 为空脆弱性已修复（安全跳过、不中断、不降低真实桌面覆盖，D1 装置 + 生命周期守卫两侧修复）；
- [x] A-3b/B-2/B-3/B-4/D1/D2/D3、Release build（0W/0E）、全量 Release test（1507/0/0）回归全绿；原 D1/D2/B2/B3/B4/最终结果记录与全部未跟踪工件原样保留；未 git push。

## 7. 过程中发现并处理的问题（如实记录）

1. **D2 漏洞按真实攻击场景复现成功（两步）**：先按物理路径写 owner 标记得到 `path-mismatch`（非漏洞），改为按 alias 路径（词法路径）写标记后复现漏洞——所有权门禁放行、`Invoke-ASUninstall` 删除 `outside\install` 哨兵。D3 修复后同场景返回 `reason=reparse-point`，哨兵 SHA-256 `AE216C2EF5247A3782C135EFA279A3E4CDC61094270F5D2BE58C6204B7A612C9` 前后完全不变。
2. **PS 5.1 `"$Action: ..."` 驱动器限定变量解析错误**（“':' 后面的变量名称字符无效”）：改为 `"${Action}: ..."`。
3. **D3 装置空目录快照被管道解包**（空目录 `@() | Sort-Object` + 一元逗号产生含 `$null` 的单元素数组，StrictMode 下 `.Rel` 抛 `PropertyNotFoundStrict`）：`Get-DirHashSnapshot` 改 List 累加器 + `return ,@($snap.ToArray() | Sort-Object Rel)`。
4. **`Replace-ASBinary` / `Invoke-ASReinstall` 候选目录块文本相同导致 Edit 歧义**：以尾随上下文（`# 0) 所有权门禁` / `$ownership = Test-ASInstallOwnership`）消歧。

## 8. 未执行 / 人工待验项（自动化不覆盖，标「未执行/人工待验」）

- [ ] 真机检查：真实安装目录（非 `$env:TEMP`）父链含恶意 junction 时，实际卸载/重装/替换/回滚是否同样 fail-closed（沙箱已验证语义，真实目录未执行）。
- [ ] 真实设备电源边界与真实安装互操作（全程 TestMode，不触真实电源）。
- [ ] 干净机真实安装/升级/卸载 Keep/重装/卸载 Remove 完整人工操作路径（见 B-3 待验清单）。

## 9. 遗留问题（最多 3 项）

1. `Test-ASFullChainSafe` 对多级 reparse 链仅报告首个命中分量路径（fail-closed 语义正确，未继续探测深层）。
2. PSScriptAnalyzer 预置警告（`Ensure-ASSpkgBackupsRoot`、`Replace-ASBinary` 动词）为历史遗留，本返修未引入，未处理。
3. B-4 冒烟 C3 首跑存在 GUI 渲染时序抖动（重跑 56/56 全绿）；装置未加渲染稳定等待，复现时为环境性抖动非代码回归。

## 10. 下一步

满足 D3 阶段验收。可以进入下一阶段（仍需 ChatGPT 独立验收后决定；本返修未 git push、未进入 S24）。
