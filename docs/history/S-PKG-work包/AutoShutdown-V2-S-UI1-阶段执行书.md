# AutoShutdown V2 · S-UI1「发布前 UI 可用性、导航页与诊断中心收尾」阶段执行书

- 阶段: S-UI1（发布前 UI 可用性、导航页与诊断中心收尾；独立执行书，不覆盖原记录）
- 基线: `93357ad`（S-PKG-D4 提交；当前 HEAD）
- 分支: `master`；不 reset、不 checkout、不 git push、不进入 S24
- 工作包目录: `S-PKG-work包/`（既有历史工作包与 LibreOffice MSI 全部保留、未覆盖）
- 执行人: S-UI1 唯一执行 AI
- 沙箱纪律: 冒烟/测试全部使用 `AUTOSHUTDOWN_DATA_ROOT` 隔离数据根；真实电源全程 TestMode 隔离

## 0. 硬性约束（不得违反）

1. 不修改 S-PKG-D1/D2/D3/D4、B2/B3/B4、最终结果记录；不 reset/checkout/push；不进入 S24。
2. 不得改变冻结状态机、双闸门、`ShutdownWorkflow` 唯一 `IPowerService` 出口、
   `SchedulerEngine` 唯一仲裁路径、`TaskCollection` 唯一事实源、远程单向回调与 fail-closed 规则。
3. 不得借 UI 阶段补做 S19 D2B/D2C；`RunCommands` 本地白名单仍默认空。
4. 先建本执行书与 `S-UI1-结果记录.md`（untracked）；实现单独提交，结果记录单独提交，提交后停止等待独立验收。
5. 原记录文件（`S-PKG-最终独立结果记录.md`、`S-PKG-B2/B3/B4-*.md`）不得覆盖。

## 1. 阶段目标

### A. 全局布局与滚动
- 首页 / 软件设置 / 日志与诊断 / 任务管理等页面在 100%/125%/150% DPI 与小窗口（至少 900×580）下内容不裁切。
- 使用真正可操作、可见滚动条的垂直 `ScrollViewer`；禁止用固定 `Viewbox` 放大后裁切替代滚动。
- 保留顶部状态栏与左侧导航；内容区滚动时导航不消失。
- 水平方向不无故溢出；长路径/长错误信息换行或提供复制。

### B. 导航不再是空占位
- 「高级功能」「网络唤醒」「日志与诊断」「关于软件」从「后续开放」占位改为真实可用页面。
- 高级功能：接线 无人值守、关机前关闭应用、一次性 RTC 唤醒、Windows 任务计划同步；
  高风险项默认关闭，保留原双重确认与风险说明。
- 网络唤醒：展示真实 WoL 目标配置与局域网远程控制入口；不扫描、不自动发现、不出公网。
- 关于软件：产品名、真实 V2 版本/构建提交、候选包签名状态（如实标示 unsigned-candidate）、
  数据目录、日志目录、TestMode/配置状态、隐私与安全边界、帮助与反馈说明。
- 删除或修正左下角硬编码「v1.0.0 测试版」，与当前真实 VersionText 一致。

### C. 新手可理解的输入说明
- WoL 目标配置必须有可见标签（不只依赖 Tooltip）：机器名称、MAC 地址、广播地址（可选）、UDP 端口（可选）。
- 界面说明格式与示例：MAC 如 AA:BB:CC:DD:EE:FF；广播地址可留空；端口可留空默认 9；禁止填写公网 IP。
- 远程控制页为 S23 功能做易懂说明：默认关闭、默认只读、监听地址/端口、TLS、PIN 配对、白名单风险说明；
  不得弱化 TLS/HMAC/PIN/白名单要求。
- 配置缺失或损坏时，明确显示原因与「初始化安全配置」恢复入口；初始化后仍必须 TestMode=true，绝不自动开启真实电源操作。

### D. 日志与诊断中心
- 保留日志目录与最近活动，新增：刷新、错误筛选、搜索、复制选中记录、打开日志目录、复制诊断摘要。
- 「安全自检」：只读检查 配置健康、数据目录可写性、本地任务、任务计划同步、WoL 目标合法性、远程监听/TLS 状态；
  不自动修复、不执行电源操作。
- 「导出脱敏诊断包」：用户显式点击后生成；默认排除 PIN、HMAC/配对 secret、证书私钥、PFX 密码。
  路径、任务名、IP/MAC 等隐私信息明确提示并可由用户确认。
- 「截图当前窗口」：只由用户主动保存当前窗口截图并打开所在目录；绝不实现自动上传、远程桌面、实时监控或后台外传。

### E. 专项：首页文字与按钮截断
- 时间模式（八种）与电源动作（五种）文字完整可见，不省略、不裁切、不缩小字体掩盖。
- 时间模式与电源动作区域不得固定为导致文字挤压的 UniformGrid 列宽；使用可自适应换行的布局
  （WrapPanel 或合理 Grid 分列），按钮宽度按内容保留安全内边距。
- 在 900×580、100%/125%/150% DPI 下验证八种时间模式、五种动作文字完整、控件可点击、彼此不重叠。

## 2. 技术方案要点

1. **布局重构**：移除 `MainWindow.xaml` 中固定 `Width=900/Height=620` 的 `Viewbox`，
   每页改为 `ScrollViewer VerticalScrollBarVisibility="Auto"` + 自然尺寸内容；首页创建区/当前任务
   区改为可换行布局（列用 `*` 自适应，不再固定 900 宽）。导航与顶部状态栏保持固定。
2. **WrapPanel**：时间模式 8 项、电源动作 5 项从 `UniformGrid` 改 `WrapPanel`，
   SegmentRadioStyle 增加安全内边距与最小宽度；「每月第N个工作日」「唤醒他机」完整可见。
3. **导航页真实化**：`NavItem` 全部 `IsPlaceholder=false`；新增 `advanced/wol/logs/about` 页面 XAML；
   「高级功能」复用设置页既有分区（无人值守/关闭应用/RTC/任务计划同步/远程控制）绑定与命令，
   不复制逻辑；「网络唤醒」展示 WoL 目标配置（带可见标签）与远程控制入口卡片。
4. **关于页**：产品名、`VersionText`、构建提交（自 InformationalVersion 提取）、签名状态
   （运行时 Authenticode 探测，如实标示 unsigned-candidate）、数据目录（DataRootResolver.Resolve()）、
   日志目录、TestMode/配置状态、隐私与安全边界说明、帮助与反馈。
5. **诊断中心**：
   - `DiagnosticsCenterViewModel`：日志目录、日志文件列表（磁盘刷新）、级别筛选/搜索/复制选中、
     打开日志目录、复制诊断摘要、安全自检、导出脱敏诊断包、截图当前窗口。
   - `SafetySelfCheckRunner`：只读检查六项，产出报告，不自动修复。
   - `DiagnosticsPackageExporter` + `DiagnosticsRedactor`：默认排除 PIN/HMAC/配对 secret/证书私钥/
     PFX 密码（不复制 remote-*.json / *.dpapi / *.pfx）；隐私信息（路径/IP/MAC/任务名）默认脱敏、
     用户可确认勾选后包含。
   - `WindowScreenshotService`：仅用户主动 SaveFileDialog 选择路径后 RenderTargetBitmap 截图并打开所在目录。
   - `ShellOpenService`：打开目录/定位文件（唯一 Process.Start 白名单新增点）。
6. **DPI**：新增 `app.manifest` PerMonitorV2 DPI 感知；全部布局 DIP 化；125%/150% 受限于当前
   系统 DPI（96=100%），如实标注人工待验，并以 DIP 布局论证。
7. **测试**：新增聚焦测试（导航真实页、关于页字段、诊断导出脱敏、安全自检只读、配置恢复、截图 no-op）；
   更新 S11 契约测试（占位文案移除、Process.Start 白名单新增 ShellOpenService）；新增/更新 UIA 冒烟。
8. **B-4 冒烟稳定性**：将 `Start-ReleaseApp` 固定 `Start-Sleep -Seconds 3` 改为有界、可诊断的
   UI 就绪等待（等待首帧 + 绑定内容就绪信号，超时上报明细），不无限等待、不以重试掩盖失败。

## 3. 验证与验收

- Release 构建 0 错误；全量 Release 测试、相关 UI 测试、`git diff --check` 实际执行并报告真实数量/退出码。
- 新增 S-UI1 UIA 冒烟：900×580 窗口、各页面可达、内容可滚动访问、关键按钮可见可点击、
  WoL 字段标签可见、诊断页功能可见；并检查控件可见性/边界在窗口可视区域内/文本未被裁切，附截图证据。
- B-4 冒烟连续至少两轮全绿。
- DPAPI、真机 WoL/RTC、双机远程控制、125%/150% DPI 等环境受限项如实标「未执行/人工待验」。

## 4. 提交计划

1. 实现提交（单独）：XAML/VM/诊断服务/测试/冒烟脚本/契约测试更新。
2. 结果记录提交（单独）：`S-UI1-结果记录.md`。
3. 提交后停止，等待独立验收；不进入 S24。
