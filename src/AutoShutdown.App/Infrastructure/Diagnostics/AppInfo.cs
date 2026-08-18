using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>
/// 关于页与诊断中心的只读产品/构建/签名信息快照（S-UI1）。
///
/// 全部为运行时自证数据：版本、构建提交、候选包签名状态均如实读取，绝不伪称。
/// 当前仓库无签名证书，发布候选在账本中登记为 unsigned-candidate；此处运行时可执行
/// 文件做 Authenticode 探测并把结果如实附在标签之后。
/// </summary>
public static class AppInfo
{
    public const string ProductName = "电脑自动关机助手";

    /// <summary>程序集完整 InformationalVersion（含 SDK 附加的 +{commit} 后缀，若有）。</summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion();

    /// <summary>展示版本文本，例如 v2.0.0-PKG.fe54711（与顶部状态栏 VersionText 同一逻辑）。</summary>
    public static string VersionText { get; } = FormatVersion(InformationalVersion);

    /// <summary>构建提交（InformationalVersion 中 + 之后的部分；无则如实说明）。</summary>
    public static string BuildCommitText { get; } = FormatCommit(InformationalVersion);

    /// <summary>候选包签名状态：如实标记 unsigned-candidate，并附运行时 Authenticode 探测结果。</summary>
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
            return "v1.0.0-dev";
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
            return "unsigned-candidate（无法定位可执行文件，未做 Authenticode 签名）";
        }

        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            var subject = string.IsNullOrWhiteSpace(certificate.Subject)
                ? "（未知颁发者）"
                : certificate.Subject;
            return $"unsigned-candidate（候选包发布物；本机运行文件带 Authenticode 证书 {subject}，仍以发布签名清单为准）";
        }
        catch (CryptographicException)
        {
            return "unsigned-candidate（当前运行文件未做 Authenticode 签名，如实标记）";
        }
        catch (Exception)
        {
            return "unsigned-candidate（签名状态检测不可用，如实标记）";
        }
    }
}
