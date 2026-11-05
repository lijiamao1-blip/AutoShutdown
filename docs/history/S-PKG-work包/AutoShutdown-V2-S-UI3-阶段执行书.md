# AutoShutdown V2 · S-UI3 阶段执行书

## 目标

提供根目录一键入口，构建并打开当前源码的真实可交互 GUI，同时用固定 `UiTestSandbox`、`TestMode=true` 和应用内二次校验隔离正式数据与真实电源。

## 实现边界

- cmd 只委托 `tools/Start-AutoShutdownUiTest.ps1`。
- 启动器验证工作区、Git HEAD、安全配置、已有实例和构建结果；任何不确定状态均拒绝启动。
- 测试实例不转发到已有正式实例，不结束任何进程。
- 不改变调度、任务、Office、远程、Task Scheduler 或电源业务架构。
- 日志与诊断页仅增加只读测试环境卡及本地按钮。

## 验收

执行 Release build、全量测试、S-UI3 聚焦测试、启动器契约测试、至少一轮安全 UIA、`git diff --check`；记录真实通过/失败/跳过与退出码。实现与结果记录分别提交，不推送。
