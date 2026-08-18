using System.Text;
using System.Text.RegularExpressions;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>
/// 诊断包脱敏器（S-UI1）。两类规则，行为由调用方显式传入：
///
///  1. <b>密钥类（始终脱敏，默认排除）</b>：PIN、HMAC/配对 sharedSecret、证书私钥、PFX 密码、
///     令牌等，以 JSON 属性名片段匹配，命中即整值替换为 <see cref="RedactedMarker"/>；
///     无论 <c>includePrivacy</c> 是否为 true 都绝不带出。
///  2. <b>隐私类（默认脱敏，用户确认后可包含）</b>：Windows 绝对路径、IPv4、MAC 地址；
///     <c>includePrivacy</c> 为 true 时跳过隐私类脱敏（密钥类仍强制）。
///
/// 密钥类脱敏在原文（含 JSON 与普通文本）上都生效，避免属性名不含敏感词时仍泄露值的场景；
/// 这是「默认排除」的保守方向（宁可多脱敏，不可泄露）。
/// </summary>
public static class DiagnosticsRedactor
{
    /// <summary>统一脱敏标记。</summary>
    public const string RedactedMarker = "[已脱敏]";

    /// <summary>密钥类 JSON 属性名片段（不区分大小写）。命中即整值脱敏。</summary>
    private static readonly string[] SecretPropertyFragments =
    [
        "pin",
        "secret",
        "hmac",
        "privatekey",
        "pfxpassword",
        "pfxpw",
        "pfxpass",
        "password",
        "passphrase",
        "token",
        "authkey",
        "signingkey"
    ];

    /// <summary>匹配 JSON 字符串属性："prop" : "value"。</summary>
    private static readonly Regex JsonStringPropertyRegex = new(
        "\"(?<name>[^\"]{1,80})\"\\s*:\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    private static readonly Regex MacAddressRegex = new(
        @"(?<![\w-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![\w-])",
        RegexOptions.Compiled);

    private static readonly Regex Ipv4Regex = new(
        @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)",
        RegexOptions.Compiled);

    // 匹配单反斜杠（日志等普通文本）与双反斜杠（JSON 字符串中的转义路径）两种形态。
    private static readonly Regex WindowsPathRegex = new(
        @"[A-Za-z]:\\{1,2}(?:[^\\\r\n""]+\\{1,2})+[^\\\r\n""]*",
        RegexOptions.Compiled);

    /// <summary>
    /// 全文脱敏：密钥类始终处理；隐私类仅当 <paramref name="includePrivacy"/> 为 false 时处理。
    /// 逐行处理，避免长文档与正则回溯组合时出现意外输出。
    /// </summary>
    public static string Redact(string? text, bool includePrivacy)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            builder.Append(RedactLine(line, includePrivacy));
            builder.Append('\n');
        }

        // 去掉末尾多出的换行。
        if (builder.Length > 0 && builder[^1] == '\n')
        {
            builder.Length -= 1;
        }

        return builder.ToString();
    }

    /// <summary>对单个文件内容（可能为 JSON）先做密钥类整值脱敏，再按开关做隐私类脱敏。</summary>
    public static string RedactFile(string? text, bool includePrivacy)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = JsonStringPropertyRegex.Replace(text, RedactSecretPropertyMatch);
        return includePrivacy ? redacted : MaskPrivacy(redacted);
    }

    private static string RedactLine(string line, bool includePrivacy)
    {
        var redacted = JsonStringPropertyRegex.Replace(line, RedactSecretPropertyMatch);
        return includePrivacy ? redacted : MaskPrivacy(redacted);
    }

    private static string MaskPrivacy(string text)
    {
        var masked = MacAddressRegex.Replace(text, "**:**:**:**:**:**");
        masked = Ipv4Regex.Replace(masked, "[IP已脱敏]");
        masked = WindowsPathRegex.Replace(masked, "[路径已脱敏]");
        return masked;
    }

    private static string RedactSecretPropertyMatch(Match match)
    {
        var name = match.Groups["name"].Value;
        foreach (var fragment in SecretPropertyFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return $"\"{name}\": \"{RedactedMarker}\"";
            }
        }

        return match.Value;
    }
}
