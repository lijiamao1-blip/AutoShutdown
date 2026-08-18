# AutoShutdown V2 · S-UI2「首页重构、多任务可用性与发布信息收尾」阶段执行书

- 阶段: S-UI2（首页重构、多任务可用性与发布信息收尾；独立执行书，不覆盖原记录）
- 基线: `b41fe3a`（S-UI1 结果记录提交；当前 HEAD，读只验证已通过）
- 分支: `master`；不 reset、不 checkout、不 git push、不进入 S-UI3
- 工作包目录: `S-PKG-work包/`（既有历史工作包、S13–S23 工作包与 LibreOffice MSI 全部保留、未覆盖）
- 执行人: S-UI2 唯一执行 AI（下层编码执行）
- 沙箱纪律: 冒烟/测试全部使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根；真实电源全程 TestMode 隔离，禁止真实电源操作

## 0. 硬性约束（不得违反）

1. 不修改/覆盖 S-UI1、S-PKG-D1/D2/D3/D4、B2/B3/B4、S-PKG 最终结果记录及历史工作包；不 reset/checkout/push；不进入 S-UI3。
2. 不得破坏：`ShutdownWorkflow` 唯一 `IPowerService` 出口；双闸门与冻结状态机；`OfficeSave → RunCommands → CloseApps` 固定顺序；`SchedulerEngine` 唯一仲裁路径；`TaskCollection` 唯一事实源；Task Scheduler 单向同步；远程控制只读本地配置；未知状态 fail-closed；`RunCommands` 白名单默认空。
3. 不得处理 S19 D2B/D2C；不得 git push。
4. 全程 TestMode，禁止真实电源操作。
5. 多任务创建不得静默覆盖、删除、停用旧任务；不得直接修改 JSON 绕过服务。
6. 版本统一不得只在 XAML 硬编码；构建提交哈希必须真实。
7. 先建本执行书与 `S-UI2-结果记录.md`（untracked）；实现单独提交，结果记录单独提交，提交后停止等待独立验收。
8. 关于页不得填写虚假邮箱、网址或客服账号。

## 1. 阶段目标（按工作令节次）

### 三. 多任务可用性诊断
- 复现并记录「存在活动任务时创建按钮显示『已有活动任务』且禁用」的原因；判定限制在
  ViewModel CanExecute 还是 SchedulerEngine/TaskCollection。
- 验证 2+ 任务的持久化与恢复；追踪新任务的冲突检测/保存/调度路径。
- 结论（已探明）：限制在 `MainWindowViewModel.CanCreateNow`（2213-2217 行，`_currentInstance` 非终结即拒绝）；
  底层 SchedulerEngine（`Dictionary<Guid,TaskInstance>` 按 SourceTaskId）、TaskCollection（Dictionary）、
  RuntimeState/TasksDocument（字典/列表）、TaskSchedulerMapper 均支持多任务。修复点：移除 VM 层
  一刀切拦截，保留引擎层按任务 ID 的冲突检测与既有仲裁。若底层不支持，须停止并上报，不得绕过调度引擎。

### 四. 每周指定星期（7 天）
- 「每周工作日」改名「每周指定星期」；勾选周一~周日 7 项；默认周一~周五；允许周六/周日；拒绝零勾选。
- 周六/周日须贯通：任务定义 → 持久化 → 恢复 → 下次执行计算 → Task Scheduler 映射。
- 旧任务兼容：旧任务 Weekdays=周一~周五 数据不变仍正确。
- 不得改变「下个工作日」「每月第N个工作日」的既有工作日语义（仍为周一~周五剔除节假日）。
- 测试：仅周六、仅周日、周六+周日、全 7 天、跨周计算、零勾选拒绝、保存/重载、旧数据兼容。

### 五. 一次性日期 UI
- 「执行日期」「执行时间」区域清晰。
- 日历弹层放大：月份标题、切换按钮、星期列、日期数字均放大；区分今天/选中/不可选日期；「今天」快捷。
- 禁止过去时间并给出明确提示。
- 键盘支持；100/125/150% DPI 不裁切/不溢出；不引入大型第三方日历依赖。

### 六. 首页无最外层滚动条
- 仅首页移除最外层垂直滚动条，其余页面保留各自滚动条。
- 左=创建任务，右上=当前任务，右下=最近活动；可保留紧凑状态卡。
- 时间模式与电源动作可换行；全部文字完整可见；最近活动只用内部列表滚动。
- 1600×900 / 1920×1080 / 150% DPI 完整显示；900×580 紧凑/折叠/页签布局不得裁切关键控件。
- 不得恢复固定 Viewbox 放大裁切。

### 七. 多任务创建
- 创建第 2/3 个任务；任务管理展示全部；创建按钮不被已有活动任务一刀切禁用。
- 非冲突任务直接创建；冲突任务走既有冲突检测 + 用户选择；不静默覆盖/删除/停用旧任务；
  不直接改 JSON 绕过服务；重启恢复全部；Task Scheduler 同步仍由本地 TaskCollection 驱动。
- 测试：2 个非冲突任务、3 个任务、冲突仲裁、取消一个不影响其他、单独启用/停用、
  重启恢复、多任务同步、首页摘要与任务管理一致。

### 八. Office 说明卡
- 「高级功能」内只读「Office 文档自动保存」说明卡：支持运行中的 Word/Excel/PowerPoint；
  关机前按既有路径保存；不 SaveAs 新文档；不支持 WPS/LibreOffice；不自行启动/关闭 Office；
  失败/超时记日志；FailurePolicy=Continue（不能保证阻塞关机）；提供「查看日志与诊断」页导航。
- 不得加开关/立即保存/测试按钮；不得改 Office 后端/Pre-Pipeline/失败策略。

### 九. 关于页文案完善
- 「隐私与安全边界」：本地数据、不自动上传日志/截图/诊断/文档；诊断默认剔除
  PIN/HMAC secret/私钥/PFX 密码；IP/MAC/机器名/路径/任务名为隐私；WoL 仅显式局域网目标，
  不扫描、不出公网；远程控制默认关/只读；Office 不读取/上传文档内容；真实电源操作受
  TestMode/确认/双闸门门控。
- 「帮助与反馈」：新手快速开始、常见问题、故障排查入口、日志与诊断入口、数据/日志目录，
  并明确「当前版本暂无在线反馈通道。请导出脱敏诊断包，并通过你获取本软件的原渠道反馈。」。
- 不得填虚假邮箱、网址或客服账号。

### 十. 版本统一为 V2（2.0.0）
- 单一版本源：`Directory.Build.props` 增加 `Version/AssemblyVersion/FileVersion = 2.0.0`
  （SDK 自动追加真实 `+{commit}` 哈希；不靠 XAML 硬编码）。
- 统一位置：程序集三个版本属性、顶部状态栏、左下角、关于页、候选包名、manifest/账本。
- 候选包名与 manifest/账本经 `Publish-ReleaseCandidate.ps1`/`New-CandidateLedger.ps1` 已是 2.0.0（沿用）。
- `Publish-SafeRelease.ps1`（V1 遗留脚本）保持 `$version = '1.0.0'` 不动：有 `S12_4PublishContractTests`
  契约测试将其锁定为 V1 基线，改动会破坏契约且与本次「候选发布走 ReleaseCandidate」不符。
- 「真实电源模式」是运行模式而非版本；旧配置/旧任务兼容。

### 十一. 验收
- Release 构建；.NET Release 全量测试；S-UI2 聚焦测试；每周 7 天测试；多任务创建/恢复/冲突测试；
  版本单一源测试；Office 与关于 UI 测试；UIA 冒烟连续两轮；`git diff --check`；`git status`。
- UIA 冒烟须检查：首页无最外层滚动条、首页关键内容完整可见、周一~周日可见可选、日历可用、
  连续创建 ≥2 个任务、任务管理同时显示两个、Office 卡可见、隐私与帮助正文可见、
  各版本位置显示 V2.0.0。
- DPI 或真机无法在本环境执行的项目如实标「人工待验」。

## 2. 技术方案要点

1. **多任务**：移除 `CanCreateNow` 中 `_currentInstance` 非终结即拒绝的拦截（2213-2217），
   保持引擎级按 SourceTaskId 冲突检测与 `TaskArbitrator` 仲裁；`SetTaskEnabledCommand` 已存在，
   接线每任务启用/停用；首页「当前任务」绑定 `TaskItems` 与任务管理一致。
2. **每周 7 天**：VM 增 `WeekdaySaturday/WeekdaySunday` 属性并纳入 `GetSelectedWeekdays()`；
   XAML 改 7 个复选框与「每周指定星期」文案；零勾选错误文案明确。
   Core（NextExecutionCalculator/TaskDefinitionValidator/TaskSchedulerMapper）已支持任意 DayOfWeek，不改。
3. **一次性日期**：重排「执行日期/执行时间」区域；DatePicker 定制日历样式（月份标题/切换/日期数字放大、
   今天高亮、DisplayDateStart=今天 使过去日期不可选）+「今天」按钮；VM 在创建校验时对
   日期+时间落在过去给出明确错误；保持无第三方依赖。
4. **首页布局**：首页去外层 ScrollViewer 改两列 Grid（左=创建任务内滚，右上=当前任务，
   右下=最近活动内滚），紧凑状态卡保留；时间模式/动作用换行布局；其余页保留 ScrollViewer。
5. **Office 说明卡**：高级功能页加只读卡片 + `GoToLogsCommand` 导航到日志页；不改后端。
6. **关于页文案**：重写 `PrivacyBoundaryText`/`HelpFeedbackText` 常量。
7. **版本**：`Directory.Build.props` 加 `<Version>2.0.0</Version>`（带动三个程序集版本）；
   `MainWindowViewModel.VersionText` 与 `AppInfo` 回退串 `v1.0.0-dev` → `v2.0.0-dev`；
   构建后 InformationalVersion 自动带 `+{commit}` 真实哈希。
8. **测试**：新增 `S_UI2_*` 聚焦测试（多任务 VM、周天 VM、一次性日期校验、Office 卡、版本单一源、
   首页摘要一致性）；新增 `tools/test/Invoke-ASUI2Smoke.ps1`（仿 S-UI1）；发布候选 `-Step S-UI2`。

## 3. 验证与验收

- Release 构建 0 错误；全量 Release 测试、S-UI2 聚焦测试、`git diff --check` 实际执行并报告真实数量/退出码。
- 新增 S-UI2 UIA 冒烟连续至少两轮全绿；截图证据入 `S-UI2-截图证据/`。
- 100%/125% DPI、真机 WoL/RTC/远程、Office 真实文档端到端等环境受限项如实标「人工待验」。

## 4. 提交计划

1. 实现提交（单独）：XAML/VM/测试/冒烟脚本/契约测试更新。
2. 结果记录提交（单独）：`S-UI2-结果记录.md`。
3. 提交后停止，等待独立验收；不进入 S-UI3。
