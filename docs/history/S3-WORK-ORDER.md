# AutoShutdown S3 工作单：ConfigurationService

## 角色与范围

你是机械实现助手。本轮只实现配置加载、迁移入口、字段校验和安全失败。不得进入 S4，不得实现状态机、调度、任务执行、WPF 页面、托盘或真实电源操作。

工作目录：`D:\电脑定时关机重建完整版`

开始前只回复：准备修改/新增的文件清单、测试清单、范围确认。总计不超过 500 字。随后直接实现，不等待批准。

## 不可修改的边界

- 不改变现有 `IStorage`、`FileStorage`、`IPowerService` 和电源模型的公开语义。
- 不引入 claim、lease、Revision、跨进程锁或后台循环。
- 不创建 `Win32PowerService`，不使用 P/Invoke、`shutdown.exe`、`Process.Start`。
- 不使用无效配置的部分字段继续运行。
- 不得在加载失败时返回 `new AppConfig()` 作为成功结果。
- 不增加第三方 NuGet 包。
- 测试不得触碰正式用户目录，只能使用内存替身或独立临时目录。

## 必须实现

### 1. 配置服务接口与结果

在 Core 中新增配置服务抽象和结构化结果：

- `IConfigurationService.LoadAsync(CancellationToken)`
- `IConfigurationService.SaveAsync(AppConfig, CancellationToken)`
- `ConfigurationLoadStatus` 至少包含：`Unknown`、`Success`、`Missing`、`Corrupt`、`IoFailure`、`Invalid`、`UnsupportedVersion`、`MigrationUnavailable`。
- 加载结果包含 `Status`、可空 `Config`、只读错误列表。
- 只有 `Success` 可以携带可用配置；其他状态 `Config` 必须为 null，并视为安全失败。

### 2. 加载流程

固定读取 `config.json`：

1. 先通过 `IStorage` 读取 `JsonElement`，不得直接假定当前模型。
2. 映射存储层的 `NotFound`、`Corrupt`、`IoFailure`，不得抛弃状态。
3. JSON 根节点必须是对象，且必须存在整数 `SchemaVersion`。
4. 当前版本固定为 1。
5. 版本大于 1：`UnsupportedVersion`。
6. 版本小于 1：进入迁移链；没有完整迁移链时返回 `MigrationUnavailable`。
7. 迁移完成后才反序列化为 `AppConfig`。
8. 最后执行全部字段校验；任一失败返回 `Invalid`，不得返回部分配置。

### 3. 迁移壳

新增 `IConfigurationMigration`：

- `SourceVersion`
- `TargetVersion`
- 接收 `JsonElement` 并返回迁移后的 `JsonElement`（允许异步或同步，但必须可测试）。

`ConfigurationService` 接收迁移实现集合，按连续版本执行。S3 不编写任何虚构旧版本迁移；默认集合为空。

### 4. 字段校验

当前 `AppConfig` 的合法条件：

- `SchemaVersion == 1`
- `DefaultWarningSeconds`：0–86400
- `DefaultSnoozeSeconds`：1–86400
- `AllowedActions` 非 null、至少一项、不得重复，只允许 Shutdown/Restart/Sleep/Hibernate，不允许 Unknown
- `Logging` 非 null
- `Logging.Level` 只允许 Information/Warning/Error
- `Logging.RetentionDays`：1–365

校验器必须是无文件副作用的纯逻辑类，并返回所有可识别错误，不只返回第一项。

### 5. 保存流程

- 保存前必须执行相同校验。
- 校验失败不得调用 `IStorage.WriteAsync`。
- 校验通过才保存到 `config.json`。
- 存储失败必须结构化返回，不能声称成功。

### 6. DI

在 App 组合根将 `IConfigurationService` 注册为 Singleton；迁移集合默认为空。不得启动或调用服务。

## 必须测试

至少覆盖：

1. 合法配置加载成功。
2. 文件缺失返回 Missing，Config=null。
3. 损坏 JSON 返回 Corrupt，Config=null。
4. I/O 失败返回 IoFailure。
5. 根节点不是对象返回 Invalid。
6. 缺少/非整数 SchemaVersion 返回 Invalid。
7. 高版本返回 UnsupportedVersion。
8. 低版本且无迁移返回 MigrationUnavailable。
9. 完整迁移链成功的测试替身用例。
10. 上述每条字段规则的非法用例。
11. 一份配置同时有多个错误时返回多个错误。
12. 非法配置保存时写入次数为 0。
13. 合法配置保存时写入一次且目标为 `config.json`。
14. 存储写入失败被正确返回。

旧有 10 项测试必须继续通过。

## 验证命令

使用：

`C:\Users\李佳茂\Documents\Codex\2026-08-10\new-chat-5\work\.dotnet-sdk\dotnet.exe`

执行 Release build 和全部 test。不得只运行新增测试。

## 最终回复格式

只回复以下四项，总计不超过 1200 字：

1. 修改文件清单。
2. Release 编译：错误数、警告数、退出码。
3. 测试：通过/失败/跳过/总数、退出码。
4. 未完成项或阻塞项；没有则写“无”。

不要输出长篇分析、架构复述、完整代码或 SHA-256。完成后停止，不进入 S4。
