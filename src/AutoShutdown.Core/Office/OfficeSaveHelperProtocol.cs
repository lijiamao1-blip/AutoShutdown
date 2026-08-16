namespace AutoShutdown.Core.Office;

/// <summary>
/// Office 保存辅助进程与主程序之间的固定最小结果协议（S17 独立验收 D2）。
/// 标准输出只允许一行「状态 | 已保存数 | 无路径数 | 失败数」四段整数字符串，
/// 绝不包含文档正文、文件名、完整路径、凭据或用户自由文本；应用枚举由启动方
/// 单独控制，不经此协议传递。
/// </summary>
public static class OfficeSaveHelperProtocol
{
    /// <summary>四段分隔符。</summary>
    private const char Separator = '|';

    /// <summary>
    /// 将单应用保存结果序列化为固定最小结果行。忽略 Application 字段：
    /// 应用归属由父进程在启动时已知，无需重复下发。
    /// </summary>
    public static string Format(OfficeApplicationSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Join(
            Separator,
            (int)result.Status,
            result.SavedCount,
            result.NoPathCount,
            result.FailedCount);
    }

    /// <summary>
    /// 解析一行固定结果；格式非法、字段非整数或状态越界时返回 false 且不抛异常
    /// （父进程据此回退为 TimedOut，绝不信任未知内容）。
    /// </summary>
    public static bool TryParse(string? line, out OfficeApplicationSaveResult result)
    {
        result = new OfficeApplicationSaveResult();

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Trim().Split(Separator);
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var statusValue)
            || !Enum.IsDefined((OfficeAppStatus)statusValue)
            || !int.TryParse(parts[1], out var saved)
            || !int.TryParse(parts[2], out var noPath)
            || !int.TryParse(parts[3], out var failed))
        {
            return false;
        }

        result = new OfficeApplicationSaveResult
        {
            Status = (OfficeAppStatus)statusValue,
            SavedCount = saved,
            NoPathCount = noPath,
            FailedCount = failed
        };
        return true;
    }
}
