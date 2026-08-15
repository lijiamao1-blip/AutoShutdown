namespace AutoShutdown.App.Infrastructure.Power;

/// <summary>
/// Windows 电源原生 API 的隔离接口。生产实现 <see cref="Win32PowerNativeApi"/>
/// 是唯一允许包含 P/Invoke 的文件；测试注入替身，自动化测试绝不调用真实电源。
/// 每个方法返回 (成功?, 系统错误码?)。
/// </summary>
public interface IPowerNativeApi
{
    (bool Succeeded, int? NativeErrorCode) Shutdown();

    (bool Succeeded, int? NativeErrorCode) Restart();

    (bool Succeeded, int? NativeErrorCode) Sleep();

    (bool Succeeded, int? NativeErrorCode) Hibernate();
}
