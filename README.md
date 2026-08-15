# AutoShutdown 新版工程（已完成 S1–S2）

这是新版本 AutoShutdown 的独立工程。它与旧 PowerShell 版本完全隔离；当前已经完成 S1 项目骨架和 S2 安全文件存储。

## 当前范围

- `AutoShutdown.Core`：纯逻辑模型和抽象，不引用 WPF、注册表或 Win32。
- `AutoShutdown.App`：最小 WPF 宿主，只注册 `FakePowerService`，尚无窗口。
- `AutoShutdown.Tests`：验证模拟电源服务只记录调用，绝不执行真实系统操作。
- `AutoShutdown.Core/Storage`：JSON 读取、同目录临时写入、落盘刷新、反序列化验证、原子替换和旧版本备份。

当前没有配置业务校验、调度、状态机实现、托盘、开机自启或真实电源实现。这些内容不得在 S2 中提前加入。

## 环境要求

安装 .NET 8 SDK（只有 .NET 运行时不够），然后在本目录执行：

```powershell
dotnet restore .\AutoShutdown.sln
dotnet build .\AutoShutdown.sln --configuration Release --no-restore
dotnet test .\AutoShutdown.sln --configuration Release --no-build
```

## S1–S2 验收顺序

1. 确认三个项目均能还原依赖并编译。
2. 确认全部测试通过。
3. 搜索并确认不存在 P/Invoke、真实电源 API、`shutdown.exe`、进程启动、调度循环、claim、lease 或 Revision。
4. 确认 `AutoShutdown.Core` 不引用 WPF 或依赖注入包。
5. 确认 `AutoShutdown.Tests` 只引用 Core，不引用 App。
6. 验证缺失文件返回 `NotFound`，损坏 JSON 返回 `Corrupt`，不回退危险默认值。
7. 验证写入中断不会改变正式文件，不会留下临时文件。
8. 验证替换正式文件时，旧版本进入 `backups` 目录。
9. 验收完成即停止，不进入 S3。

## 安全边界

`FakePowerService` 是目前唯一的 `IPowerService` 实现。它只保存内存调用记录，并返回 `WasSimulated=true`。项目中不存在 `Win32PowerService`。
