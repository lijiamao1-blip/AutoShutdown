using AutoShutdown.Core.Office;

namespace AutoShutdown.OfficeSaveHelper;

/// <summary>
/// 辅助进程启动参数解析（S17 独立验收 D2）。仅接受受控 Office 枚举名与 GUID 最小标识；
/// 拒绝任意命令、文件路径、文档正文、凭据、用户自由文本、越界枚举与多余参数。
/// </summary>
internal static class HelperArguments
{
    public static bool TryParse(
        string[] args,
        out OfficeApplicationKind application,
        out string correlationId)
    {
        application = default;
        correlationId = string.Empty;

        if (args.Length != 4)
        {
            return false;
        }

        string? appToken = null;
        string? idToken = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app" when i + 1 < args.Length && appToken is null:
                    appToken = args[++i];
                    break;
                case "--id" when i + 1 < args.Length && idToken is null:
                    idToken = args[++i];
                    break;
                default:
                    return false;
            }
        }

        if (appToken is null || idToken is null)
        {
            return false;
        }

        // 仅接受受控枚举名与 GUID 标识；拒绝任意命令/路径/自由文本/越界枚举。
        if (!Enum.TryParse<OfficeApplicationKind>(appToken, ignoreCase: true, out application)
            || !Enum.IsDefined(application)
            || !Guid.TryParse(idToken, out _))
        {
            return false;
        }

        correlationId = idToken;
        return true;
    }
}
