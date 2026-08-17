using System.IO;

namespace AutoShutdown.App.Infrastructure;

/// <summary>
/// 应用数据根目录解析（S-PKG 发布收口新增，最小发布/测试能力，不属于业务功能）。
///
/// 默认目录与既有行为完全一致：%LocalAppData%\AutoShutdown。
/// 环境变量 <see cref="EnvironmentVariable"/>（AUTOSHUTDOWN_DATA_ROOT）可用于把数据
/// 目录指向隔离沙箱——S-PKG 的干净机安装 / 升级 / 回滚 / 卸载与 GUI 冒烟全部在隔离
/// 沙箱上执行，绝不触碰真实用户数据目录。
///
/// 安全语义：
///  - 变量为空 / 空白时回退默认目录（绝不静默换目录）；
///  - 变量非空时按 Path.GetFullPath 解析（相对路径按当前工作目录）；
///  - 仅影响数据/日志/审计/证书文件的存放位置；不改变电源语义、双闸门、
///    TestMode、自启或任何业务契约。
/// </summary>
public static class DataRootResolver
{
    public const string EnvironmentVariable = "AUTOSHUTDOWN_DATA_ROOT";

    public const string DefaultDataDirectoryName = "AutoShutdown";

    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultDataDirectoryName);
    }
}
