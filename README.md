# 电脑自动关机助手 AutoShutdown

AutoShutdown 是一款面向 Windows 10/11 的定时电源与任务管理工具。当前版本为 **v2.0.16**，支持真实关机、重启、睡眠、休眠、唤醒他机，以及多种定时规则。

> 本软件能够执行真实系统电源操作。首次使用请先保存重要文件，并认真阅读下面的安全提醒。

## 主要功能

- 8 种时间模式：倒计时、今天指定时间、每天固定时间、每周指定星期、下个工作日、每月第几个上班日、一次性指定、空闲触发。
- 5 种电源动作：关机、重启、睡眠、休眠、通过 Wake-on-LAN 唤醒其他电脑。
- 同时管理多条任务，可停用、延迟、停止、清除任务并查看最近活动。
- 关机前优雅关闭指定应用，可直接从正在运行的进程中选择。
- 支持 Office 文档保存流程，并可配置无人值守授权。
- 支持“最终强制完成系统关机”：应用无响应时由 Windows 强制结束。该选项可能导致未保存内容丢失。
- 支持多个例外日期，每行填写一个 `yyyy-MM-dd` 日期。
- 支持托盘菜单、开机自启、Windows 任务计划程序同步、日志诊断和安全自检。
- 支持局域网远程控制、TLS、设备配对及命令白名单，默认关闭并以只读能力为主。

## 下载和使用

普通用户不需要下载源码或安装 .NET SDK。请前往仓库右侧的 **Releases**，下载最新的“发给别人使用”ZIP 压缩包：

1. 下载 ZIP 文件并完整解压。
2. 双击其中的 `AutoShutdown-*.exe`。
3. Windows 首次提示安全确认时，请核对文件来源后再运行。
4. 在首页选择时间模式、电源动作和提醒时间，然后创建任务。

[前往 Releases 下载页面](https://github.com/lijiamao1-blip/AutoShutdown/releases)

## 常用设置说明

### 例外日期

例外日期表示这些日期跳过当前任务，不执行关机。每行只填写一个完整日期，例如：

```text
2026-10-01
2026-10-02
2026-10-03
```

输入一个日期后按回车键换行，再输入下一个日期，依次类推。留空表示没有例外日期。

### 每月第几个上班日

软件从每月 1 日开始计算，只统计星期一至星期五，并跳过填写在“例外日期”中的日期。例如选择“第 4 个上班日”和“22:00”，任务将在每个月计算出的第 4 个上班日晚上 22:00 执行。

### 关闭应用与强制关机

关机流程会先尝试保存 Office 文档，再并行关闭配置的应用，最后执行系统电源操作。应用数量多并不会直接导致失败；如果某个程序拒绝退出，未启用最终强制关机时，任务会为保护未保存内容而停止。启用最终强制关机后，无响应程序可能被 Windows 强制结束。

若旧版本只弹出“关闭 Windows”窗口，请检查关闭目标中是否有 `Explorer.EXE`。修复版会自动跳过旧配置中的 Explorer，并禁止在进程选择器中添加它，将桌面进程交给 Windows 在系统关机阶段处理；其他应用的关闭失败仍按原规则处理。

## 安全提醒

- 正式关机测试前，请先手动保存所有重要文件。
- 不确定时不要启用“最终强制完成系统关机”。
- 不建议把 `Explorer.EXE`、`TextInputHost.exe` 等 Windows 系统组件加入关闭目标。
- 局域网远程控制默认关闭；只有确有需要时才启用，并保持 TLS 和最小命令白名单。
- 软件设置和任务数据保存在当前 Windows 用户的本地应用数据目录，不会包含在分享压缩包中。

## 开发与构建

开发环境需要 Windows 和 .NET 8.0.131 SDK（包含 .NET 8.0.31 运行时）：

```powershell
dotnet restore .\AutoShutdown.sln
dotnet build .\AutoShutdown.sln --configuration Release --no-restore
dotnet test .\AutoShutdown.sln --configuration Release --no-build
```

生成发布候选包：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Publish-ReleaseCandidate.ps1 -Version 2.0.16 -Step S24 -Build release -DistributionMode Production
```

## 项目结构

- `src/AutoShutdown.App`：WPF 桌面界面、系统集成、电源操作和应用服务。
- `src/AutoShutdown.Core`：任务规则、调度、工作流、配置、存储和关闭应用逻辑。
- `src/AutoShutdown.OfficeSaveHelper`：Office 文档保存辅助程序。
- `tests/AutoShutdown.Tests`：单元测试、集成测试与界面契约测试。
- `tools`：构建、测试和发布脚本。

## 当前版本

**v2.0.16** — 保留 v2.0.15 的正式配置初始化和 Explorer 关闭修复，将自包含运行时更新到 .NET 8.0.31，并隔离单实例测试使用的互斥锁，避免正在运行的正式程序干扰构建验证。
