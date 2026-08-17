namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// 严格 MAC 地址解析（S21）。接受常见书写格式：
/// <list type="bullet">
/// <item>冒号分隔：<c>AA:BB:CC:DD:EE:FF</c></item>
/// <item>短横分隔：<c>AA-BB-CC-DD-EE-FF</c></item>
/// <item>点分四组：<c>AABB.CCDD.EEFF</c></item>
/// <item>无分隔：<c>AABBCCDDEEFF</c></item>
/// </list>
/// 大小写不敏感；输出统一规范化为大写的 <c>AA:BB:CC:DD:EE:FF</c>。
/// 任何混合分隔符、多余字符、非十六进制、字节位数不正确都一律拒绝（严格校验，绝不宽松接受）。
/// </summary>
public static class MacAddress
{
    /// <summary>严格解析 MAC 并输出规范形式；非法输入返回 false 且不产生输出。</summary>
    public static bool TryParse(string? value, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // 点分四组：AABB.CCDD.EEFF（3 组 × 4 十六进制位 = 12 hex digits = 6 字节）。
        if (TryParseDotGrouped(text, out mac))
        {
            return true;
        }

        // 无分隔：12 个十六进制位 = 6 字节。
        if (text.Length == 12 && IsHexOnly(text))
        {
            mac = FromHexPairs(text);
            return mac is not null;
        }

        // 冒号或短横分隔：6 组 × 2 十六进制位。
        var separator = text.IndexOf(':') >= 0 ? ':' : '-';
        if (text.Count(character => character == separator) == 5)
        {
            var groups = text.Split(separator);
            if (groups.Length == 6 && groups.All(group => group.Length == 2 && IsHexOnly(group)))
            {
                mac = FromHexPairs(string.Concat(groups));
                return mac is not null;
            }
        }

        return false;
    }

    /// <summary>把 6 字节 MAC 格式化为规范形式 <c>AA:BB:CC:DD:EE:FF</c>。</summary>
    public static string ToCanonical(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("A MAC address must contain exactly 6 bytes.", nameof(mac));
        }

        return string.Join(
            ":",
            mac.ToArray().Select(byteValue => byteValue.ToString("X2")));
    }

    private static bool TryParseDotGrouped(string text, out byte[] mac)
    {
        mac = [];
        if (text.Length != 14)
        {
            return false;
        }

        if (text[4] != '.' || text[9] != '.')
        {
            return false;
        }

        var first = text[..4];
        var second = text[5..9];
        var third = text[10..14];
        if (!IsHexOnly(first) || !IsHexOnly(second) || !IsHexOnly(third))
        {
            return false;
        }

        mac = FromHexPairs(first + second + third);
        return mac is not null;
    }

    private static bool IsHexOnly(string text)
        => text.Length > 0 && text.All(IsHexDigit);

    private static bool IsHexDigit(char character)
        => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static byte[] FromHexPairs(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(hex.AsSpan(index * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[index]))
            {
                return [];
            }
        }

        return bytes;
    }
}
