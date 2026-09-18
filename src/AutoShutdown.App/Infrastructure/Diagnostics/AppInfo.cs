using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutoShutdown.App.Infrastructure;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>
/// 关于页与诊断中心的只读产品/构建/签名信息快照（S-UI1）。
///
/// 全部为运行时自证数据：版本、构建提交、签名状态均如实读取，绝不伪称。
///
/// 签名状态（S-UI1-D1）：此前无论探测结果如何，标签一律以 unsigned-candidate 开头——
/// 即使真的探测到了 Authenticode 证书，也会输出「unsigned-candidate（…带 Authenticode
/// 证书…）」这种自相矛盾的句子，且一旦将来真的签了名，关于页仍会说没签名。
/// 现在改为如实反映探测结果：探到证书报 signed，探不到报 unsigned，探测本身失败报未知。
/// </summary>
public static class AppInfo
{
    public const string ProductName = "电脑自动关机助手";

    /// <summary>程序集完整 InformationalVersion（含 SDK 附加的 +{commit} 后缀，若有）。</summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion();

    /// <summary>展示版本文本，例如 v2.0.0-PKG.fe54711（与顶部状态栏 VersionText 同一逻辑）。</summary>
    public static string VersionText { get; } = FormatVersion(InformationalVersion);

    /// <summary>构建提交（InformationalVersion 中 + 之后的部分；无则如实说明）。</summary>
    public static string BuildCommitText { get; } = UiTestEnvironment.IsRequested && UiTestEnvironment.BuildCommit.Length > 0
        ? UiTestEnvironment.BuildCommit
        : FormatCommit(InformationalVersion);

    /// <summary>
    /// 当前运行的可执行文件的 Authenticode 签名状态，如实反映运行时探测结果。
    /// 注意语义边界：探测只确认文件内嵌了证书，不校验证书链、吊销状态与时间戳，
    /// 因此 signed 只表示「带签名」，不表示「签名可信」。
    /// </summary>
    public static string SigningStatusText { get; } = DetectSigningStatus();

    /// <summary>数据目录（AUTOSHUTDOWN_DATA_ROOT 覆盖或默认 %LocalAppData%\AutoShutdown）。</summary>
    public static string DataDirectory => Infrastructure.DataRootResolver.Resolve();

    private static string ReadInformationalVersion()
        => CustomAttributeExtensions.GetCustomAttribute<AssemblyInformationalVersionAttribute>(
                typeof(AppInfo).Assembly)
            ?.InformationalVersion ?? string.Empty;

    private static string FormatVersion(string informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return "v2.0.0-dev";
        }

        var plusIndex = informational.IndexOf('+');
        var version = plusIndex >= 0 ? informational[..plusIndex] : informational;
        return version.StartsWith('v') ? version : "v" + version;
    }

    private static string FormatCommit(string informational)
    {
        var plusIndex = informational.IndexOf('+');
        if (plusIndex < 0 || plusIndex == informational.Length - 1)
        {
            return "（版本信息未携带构建提交）";
        }

        return informational[(plusIndex + 1)..];
    }

    private static string DetectSigningStatus()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown（无法定位当前可执行文件，签名状态不可判定）";
        }

        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            var subject = string.IsNullOrWhiteSpace(certificate.Subject)
                ? "（未知颁发者）"
                : certificate.Subject;
            // 仅确认内嵌了证书；未校验证书链/吊销/时间戳，措辞不得越过这一边界。
            return $"signed（当前运行文件带 Authenticode 证书 {subject}；未校验证书链与吊销状态）";
        }
        catch (CryptographicException)
        {
            return "unsigned（当前运行文件未做 Authenticode 签名）";
        }
        catch (Exception)
        {
            return "unknown（签名状态检测不可用）";
        }
    }
}
