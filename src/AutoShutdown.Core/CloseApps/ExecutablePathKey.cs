namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 关闭目标的「规范化 EXE 完整绝对路径」键（S-CLOSEUI1）。执行期匹配、添加去重、
/// 失效重选全部以规范化后的键为唯一依据；只做路径文本规范化，绝不参与任何进程操作。
/// 规范化规则：去首尾空白 → 展开为完整绝对路径（<see cref="Path.GetFullPath"/>）；
/// 比较一律大小写不敏感。路径为 null/空白/无法展开（非法字符）时返回 null（fail-closed：
/// 绝不能把无法规范化的路径当作合法目标）。
/// </summary>
public static class ExecutablePathKey
{
    /// <summary>
    /// 返回规范化路径；无法规范化时返回 null（绝不返回空字符串或半规范化结果）。
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        try
        {
            var full = Path.GetFullPath(trimmed);
            return full.Length == 0 ? null : full;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 含非法字符 / 含 NUL / 路径过长：无法确认 → fail-closed。
            return null;
        }
    }

    /// <summary>规范化后比较（大小写不敏感）；任一侧无法规范化即视为不等（fail-closed）。</summary>
    public static bool EqualsNormalized(string? a, string? b)
    {
        var keyA = Normalize(a);
        var keyB = Normalize(b);
        return keyA is not null
            && keyB is not null
            && string.Equals(keyA, keyB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化路径；无法规范化时返回原 trimmed 文本（仅供展示，绝不用于匹配）。</summary>
    public static string DisplayNormalized(string? path)
        => Normalize(path) ?? (string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim());
}
