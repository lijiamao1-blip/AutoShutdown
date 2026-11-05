# S-PKG D4 最小返修结果记录（独立文件，不覆盖任何历史记录）

- 阶段: S-PKG-D4（候选根正常、候选子目录为 junction 时，Invoke-ASReinstall 先删除旧 EXE/owner marker 再复制期才拒 junction → 半安装 —— 候选整树预检前置最小返修）
- 日期: 2026-08-18
- 返修基线（起始提交）: `7427f8e`（D3 独立结果记录）
- 结束提交: `3f25c63`（D4: candidate subdir-junction harness + C# contract tests）
- 返修内全部提交:
  1. `9047bf7` D4: candidate-tree pre-check (Get-ASDirTreeSafe + full ancestor-chain) wired BEFORE any backup-slot/delete/write in Replace-ASBinary and Invoke-ASReinstall（CP2 实现）
  2. `3f25c63` D4: candidate subdir-junction harness (reinstall+replace byte-invariant, no half-install/backup residue, symlink env-skip) + C# contract tests（CP3 装置 + 契约测试）
  3. 本结果记录（CP4 独立提交，随记录完成后创建）
- 执行人: S-PKG D4 执行 AI（全部在 `$env:TEMP\as-*` 临时沙箱；未触碰真实用户数据 / 真实安装 / 电源 / 注册表 / 防火墙 / 任务计划）
- 结论: **通过**（验收条件全绿，见第 6 节；B-4 首跑 10 项 GUI 渲染抖动如实记录于第 5 节）

---

## 1. 缺口与返修范围（对照执行书 7 项目标）

| # | 执行书目标 | 落实方式 |
| --- | --- | --- |
| 1 | 对 Replace-ASBinary 和 Invoke-ASReinstall，在任何备份槽创建、删除旧 appFiles、删除 owner marker、创建安装目录或复制候选文件之前，完整预检 CandidateDir 整棵树 | 新增 `Test-ASCandidateTreeSafe` / `Assert-ASCandidateTreeSafe`；`Assert-` 调用置于两函数候选根校验之后、任何破坏性步骤之前（`Invoke-ASReinstall` 在 `Remove-ASOwnedFiles`/owner 删除/安装目录新建之前；`Replace-ASBinary` 在 `rollback-install.new` 删除/新建之前） |
| 2 | 预检必须使用现有 Get-ASDirTreeSafe / 全祖先链守卫；候选根、任意子目录或文件含 junction/symlink/reparse point 时立即拒绝 | 预检内 `$chain = Test-ASFullChainSafe -Path $candFull`（候选根至卷根全祖先链）+ `$tree = Get-ASDirTreeSafe -BaseDir $candFull`（base 自身 + 整树子节点 reparse 探测）；命中即返回失败，`Assert-` 抛错 |
| 3 | Invoke-ASReinstall 的候选树预检失败时，旧 EXE、所有权标记、用户文件、安装目录内容必须字节级不变 | 预检位于任何删除之前；D4 装置 B/E 节证明旧 EXE SHA-256 不变、owner 内容不变、用户文件不变、安装目录快照完整、全新安装目录未被创建 |
| 4 | Replace-ASBinary 的候选树预检失败时，安装目录与既有 rollback-install 槽必须不变，且不得留下 rollback-install.new 或任何新备份写入 | 预检位于任何槽创建之前；D4 装置 C/D 节证明既有槽快照不变、无 `rollback-install.new`、全新数据根 `backups\spkg` 未被创建 |
| 5 | 新增 D4 装置：候选根正常、候选子目录 junction 指向临时 outside；覆盖 reinstall 与 replace；断言操作拒绝、outside 哨兵 SHA-256 不变、旧 EXE SHA-256 不变、owner marker 内容不变、用户文件不变、无半安装/无备份残留 | `Invoke-SPD4CandidateTreeTests.ps1`（7 节 30 断言 + 1 环境跳过，A–G，见第 4 节） |
| 6 | 若环境支持文件 symlink，再补候选顶层文件 symlink 覆盖；不支持时明确标为环境跳过，不得伪称已执行 | 实测本环境 `cmd /c mklink`（文件 symlink）因权限不足失败（无管理员/开发者模式）；D4 装置 F 节以 try/catch 探测，失败打印 `SKIP ... NOT faked as executed` 明确标「环境跳过」，未伪称 |
| 7 | 回归 A-3b、B-2、B-3、B-4、D1、D2、D3、D4、Release build、全量 Release test；报告真实数量、退出码、工作树并停止 | 见第 5、6 节；保留全部既有未跟踪工件；未 git push |

## 2. 实际修改文件及职责

| 文件 | 状态 | 职责 |
| --- | --- | --- |
| `tools/SPkg-Lifecycle.ps1` | 修改（43+/0−） | 新增 `Test-ASCandidateTreeSafe` / `Assert-ASCandidateTreeSafe` + `Replace-ASBinary` / `Invoke-ASReinstall` 候选整树预检前置（提交 `9047bf7`） |
| `tools/test/Invoke-SPD4CandidateTreeTests.ps1` | 新增 | D4 候选子 junction 装置（7 节 30 断言 + 1 环境跳过，提交 `3f25c63`） |
| `tests/AutoShutdown.Tests/S_PKG_D4_CandidateTreeContractTests.cs` | 新增 | D4 静态文本契约测试（7 个 xUnit 事实，提交 `3f25c63`） |
| `S-PKG-work包/AutoShutdown-V2-S-PKG-D4-最小返修执行书.md` | 新增（未跟踪工件） | D4 最小返修执行书 |
| `S-PKG-work包/S-PKG-D4-结果记录.md` | 新增 | 本结果记录（独立文件） |

未改动：原 `S-PKG-D1-安装所有权与路径加固结果记录.md`、`S-PKG-D2-结果记录.md`、`S-PKG-D3-结果记录.md`、`S-PKG-B2/B3/B4-*`、`S-PKG-最终独立结果记录.md`、`AutoShutdown-V2-S-PKG-阶段一键执行书.docx`、S13–S23 历史工作包、`LibreOffice_26.2.5_Win_x86-64.msi`。未 git push。

## 3. 关键安全不变量核对（候选整树预检前置）

| 破坏性/递归路径 | 函数/位置 | 预检接入 | 顺序证据（行号） |
| --- | --- | --- | --- |
| 候选整树预检 | `Test-ASCandidateTreeSafe` / `Assert-ASCandidateTreeSafe` | `Test-ASFullChainSafe -Path $candFull`（全祖先链）+ `Get-ASDirTreeSafe -BaseDir $candFull`（整树不跟随枚举）；候选根/任意子目录/文件含 reparse 即失败 | 复用 D2/D3 现有原语 |
| replace 安装备份/槽创建 | `Replace-ASBinary` | `Assert-ASCandidateTreeSafe ... -Action 'replace binary'` | 预检行 742 < 槽创建 `New-Item $slotNew` 行 760 |
| reinstall 删除旧 EXE/owner/建安装目录 | `Invoke-ASReinstall` | `Assert-ASCandidateTreeSafe ... -Action 'reinstall from candidate'` | 预检行 1143 < `Remove-ASOwnedFiles` 行 1150 < owner 删除 |

补充核对：预检基于词法路径逐段探测，不先解析物理目标（沿用 `Resolve-ASPath`=纯 `GetFullPath`，全仓无 `Resolve-Path`）；候选根自身 reparse 校验（D2）与候选根全祖先链校验（D3）原样保留，契约事实 `D4_PriorCandidateGuards_StillPresent` 断言未破坏；预检为纯只读枚举（`Get-ChildItem`），失败路径零写入。

## 4. D4 装置与契约测试

- `Invoke-SPD4CandidateTreeTests.ps1`：`cmd /c mklink /J` 建「候选根正常、候选子目录 junction -> outside」，7 节 A–G：
  - A `Test-ASCandidateTreeSafe` 预检函数（干净候选树 ok / 候选子 junction 拒绝并报告到该 junction / 候选根 junction 拒绝 / 缺失候选拒绝）；
  - B **reinstall**：候选子 junction → 预检即拒，outside 哨兵 + 哈希不变、旧 EXE SHA-256 不变、owner marker 内容不变、用户文件不变、安装目录快照完整（无半安装）；
  - C **replace（既有 rollback-install 槽）**：预检即拒，既有槽快照不变、无 `rollback-install.new`、安装目录旧 EXE SHA-256 不变、owner 内容不变、outside 哨兵不变；
  - D **replace（全新数据根）**：预检即拒，`backups\spkg` 未被创建（无任何新备份写入）、安装目录不变、outside 不变；
  - E **reinstall（安装目录不存在）**：预检即拒，安装目录未被创建、outside 不变；
  - F **候选顶层文件 symlink**：环境不支持（`mklink` 权限不足）→ 明确标 `SKIP`，不伪称已执行；
  - G 正常（无 junction）候选树回归：replace/reinstall 正常成功，无假阳性。
  - **实际运行：pass=30 fail=0 skip=1，exit 0。**
- `S_PKG_D4_CandidateTreeContractTests.cs`：7 个静态文本 xUnit 事实（只读源码，不执行生命周期脚本、不写目录）——预检函数存在且复用 `Get-ASDirTreeSafe` / `Test-ASFullChainSafe`、命中 reparse fail-closed、`Replace-ASBinary` 预检行号 < 槽创建行号（顺序断言）、`Invoke-ASReinstall` 预检行号 < `Remove-ASOwnedFiles` < owner 删除（顺序断言）、既有 D2/D3 候选守卫未破坏、装置覆盖 reinstall/replace + SHA256/owner/用户文件/哨兵哈希 + 无半安装/无备份残留 + symlink 环境跳过。**实际运行：7/7 通过。**

## 5. 回归结果（全量，按完成条件）

| 装置 | 结果 | 退出码 |
| --- | --- | --- |
| A-3b 生命周期 | **21 / 21** pass | 0 |
| B-2 升级与回滚 | **41 / 41** pass | 0 |
| B-3 系统集成边界 | **12 / 12** pass（skip=0） | 0 |
| B-4 发布验收冒烟（GUI，UIA 驱动真实 RC EXE，8 节） | 首跑 **46 pass / 10 fail**（exit 1），重跑 **56 / 56** pass（skip=0，exit 0） | 见下节 |
| D1 所有权装置 | **60 / 60** pass | 0 |
| D2 reparse 装置 | **51 / 51** pass | 0 |
| D3 祖先链装置 | **41 / 41** pass | 0 |
| D4 候选树装置（新增） | **30 pass / 0 fail / 1 skip** | 0 |
| .NET 全量测试（Release，`--no-build`） | **1514 通过 / 0 失败 / 0 跳过**（1507 基线 + 7 新 D4 契约事实） | 0 |
| Release build | 0 Warning / 0 Error | 0 |
| `git diff --check` | 无空白错误 | 0 |
| `SPkg-Lifecycle.ps1` 解析 | 无语法错误（PARSE OK；tokens=8150） | — |

**B-4 首跑 10 项失败的如实说明**：首跑 C5 设置页 10 项（强制 TLS / 白名单 4 项 / 当前 PIN / 暂无已配对设备 / 未监听 / 轮换 PIN / 保存并应用）UIA `Find-Descendant` 全部未命中，而 C1–C4、C6–C8 全绿。D4 仅修改 `tools/SPkg-Lifecycle.ps1` 与测试文件，冒烟装置与真实 RC EXE 自 B4 基线（`ba7b017`）后字节级未变，C5 区间无任何 lifecycle 调用——判定为 GUI 渲染时序环境性抖动（与 D3 阶段 B-4 首跑 C3 5 项抖动同类），非代码回归。立即重跑：**56/56 pass、exit 0**。首跑与重跑结果均如实记录。

## 6. 返修验收核对（对照执行书 7 项目标）

- [x] `Replace-ASBinary` 与 `Invoke-ASReinstall` 在任何备份槽创建、删除旧 appFiles、删除 owner marker、创建安装目录或复制候选文件之前，对 CandidateDir 整棵树完整预检；
- [x] 预检复用现有 `Get-ASDirTreeSafe` / 全祖先链守卫；候选根、任意子目录或文件含 junction/symlink/reparse point 即拒绝；
- [x] reinstall 预检失败：旧 EXE SHA-256 不变、owner marker 内容不变、用户文件不变、安装目录快照完整（无半安装）、全新安装目录未被创建；
- [x] replace 预检失败：安装目录与既有 rollback-install 槽不变、无 `rollback-install.new`、全新数据根 `backups\spkg` 未被创建；
- [x] D4 装置覆盖 reinstall 与 replace（候选根正常 + 候选子 junction -> outside），断言操作拒绝、outside 哨兵 SHA-256 不变、旧 EXE SHA-256 不变、owner 内容不变、用户文件不变、无半安装/无备份残留，实际运行通过；
- [x] 候选顶层文件 symlink：本环境不支持（无管理员/开发者模式），装置明确标「环境跳过」并注明 `NOT faked as executed`，未伪称已执行；
- [x] A-3b/B-2/B-3/B-4/D1/D2/D3/D4、Release build（0W/0E）、全量 Release test（1514/0/0）回归全绿；原 D1/D2/D3/B2/B3/B4/最终结果记录与全部未跟踪工件原样保留；未 git push。

## 7. 过程中发现并处理的问题（如实记录）

1. **D4 缺口按攻击场景复现成功**：候选根正常、候选子目录 `plugins` junction → outside；`Invoke-ASReinstall` 先删除旧 EXE/owner marker 再复制期才拒 junction → 安装目录被清空（半安装），outside 哨兵不变（与验收报告一致）。修复后同一场景预检即拒，`oldExeExists=True`、`ownerExists=True`、安装条目完整、无半安装。
2. **PS 5.1 原生 stderr + `$ErrorActionPreference='Stop'` 使 `2>$null`/`2>&1` 仍把 mklink「权限不足」变终止错误中断装置**：symlink 探测改为 try/catch 包裹，失败即明确标「环境跳过」，不伪称已执行。
3. **`Set-Content -Encoding UTF8` 追加 CRLF 导致 user file 内容断言 `-eq 'user file'` 失败**：断言改为 `.Trim()` 后比较（只判内容、容忍行尾）。
4. **`Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $appFiles` 在 lifecycle 中重复出现**（`Invoke-ASReinstall` 与另一函数）：C# 顺序断言改用「函数体切片后 IndexOf」定位函数内首个出现，避免跨函数误判。

## 8. 未执行 / 人工待验项（自动化不覆盖，标「未执行/人工待验」）

- [ ] 真机检查：真实安装目录（非 `$env:TEMP`）候选树含恶意子 junction 时，实际替换/重装是否同样 fail-closed（沙箱已验证语义，真实目录未执行）。
- [ ] 候选顶层文件 symlink 覆盖：本环境无管理员/开发者模式无法创建文件 symlink，标「环境跳过」；需在支持 symlink 的环境（管理员/开发者模式）验证同布局拒绝。
- [ ] 真实设备电源边界与真实安装互操作（全程 TestMode，不触真实电源）。
- [ ] 干净机真实安装/升级/卸载 Keep/重装/卸载 Remove 完整人工操作路径（见 B-3 待验清单）。

## 9. 遗留问题（最多 3 项）

1. 候选树预检对多级 reparse 链仅报告首个命中节点路径（fail-closed 语义正确，未继续探测深层）。
2. PSScriptAnalyzer 预置警告（`Ensure-ASSpkgBackupsRoot`、`Replace-ASBinary` 动词）为历史遗留，本返修未引入，未处理。
3. B-4 冒烟 C5 首跑存在 GUI 渲染时序抖动（重跑 56/56 全绿）；装置未加渲染稳定等待，复现时为环境性抖动非代码回归。

## 10. 下一步

满足 D4 阶段验收。可以进入下一阶段（仍需 ChatGPT 独立验收后决定；本返修未 git push、未进入 S24）。
