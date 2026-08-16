using System.IO;

namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 本地命令白名单的校验与授权（S19）。纯路径/参数字符串逻辑，不触碰文件系统、不启动进程、
/// 不触碰电源。可执行路径必须规范化为绝对路径后，与白名单条目做大小写不敏感匹配；
/// 通配符只允许「规范化绝对路径前缀」（目录后 "*"）与「受限文件扩展名」（"*.ext"），
/// 且解析后再次确认仍位于允许根之下（防前缀碰撞与路径穿越）。
/// </summary>
public static class CommandWhitelist
{
    /// <summary>结构校验白名单，返回错误列表（空 = 合法）。</summary>
    public static IReadOnlyList<string> Validate(LocalCommandWhitelist whitelist)
    {
        ArgumentNullException.ThrowIfNull(whitelist);

        var errors = new List<string>();

        if (whitelist.Allow is null)
        {
            errors.Add("RunCommands.Whitelist.Allow must not be null.");
            return errors;
        }

        for (var index = 0; index < whitelist.Allow.Length; index++)
        {
            var entry = whitelist.Allow[index];
            var prefix = $"RunCommands.Whitelist.Allow[{index}]";

            if (entry is null)
            {
                errors.Add($"{prefix} must not be null.");
                continue;
            }

            if (!TryParseExecutablePattern(entry.Executable, out _, out _, out _, out _, out _))
            {
                errors.Add(
                    $"{prefix}.Executable must be a normalized absolute path, optionally ending with a directory-prefix \"*\" or a restricted \"*.ext\" wildcard.");
            }

            if (entry.Arguments is null)
            {
                errors.Add($"{prefix}.Arguments must not be null.");
            }
        }

        return errors;
    }

    /// <summary>
    /// 授权一条命令（可执行路径 + 参数）。Allowed 为真时必须使用返回的规范化绝对路径启动。
    /// 默认拒绝：空路径/非绝对/路径穿越/未匹配白名单/参数不匹配一律返回对应拒绝原因。
    /// </summary>
    public static CommandWhitelistDecision Authorize(
        LocalCommandWhitelist whitelist,
        string executable,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(whitelist);

        if (!TryNormalizeExecutable(executable, out var normalized, out var reason))
        {
            return new CommandWhitelistDecision { Allowed = false, RejectionReason = reason };
        }

        var executableMatched = false;
        foreach (var entry in whitelist.Allow)
        {
            if (entry is null)
            {
                continue;
            }

            if (!MatchesExecutable(entry.Executable, normalized))
            {
                continue;
            }

            executableMatched = true;
            if (MatchesArguments(entry.Arguments, arguments))
            {
                return new CommandWhitelistDecision
                {
                    Allowed = true,
                    NormalizedExecutablePath = normalized
                };
            }
        }

        return new CommandWhitelistDecision
        {
            Allowed = false,
            RejectionReason = executableMatched
                ? CommandRejectionReason.ArgumentMismatch
                : CommandRejectionReason.NotWhitelisted
        };
    }

    /// <summary>
    /// 校验命令可执行路径（具体绝对路径，不含通配符），返回错误描述或 null（合法）。
    /// 供配置校验复用，避免校验逻辑分散。
    /// </summary>
    public static string? DescribeExecutablePathError(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "must not be empty.";
        }

        var trimmed = executable.Trim();

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return "must be an absolute path.";
        }

        if (ContainsTraversal(trimmed))
        {
            return "must not contain path traversal (\"..\").";
        }

        if (trimmed.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            return "must not contain wildcard characters.";
        }

        try
        {
            _ = Path.GetFullPath(trimmed);
        }
        catch
        {
            return "must be a valid absolute path.";
        }

        return null;
    }

    private static bool TryNormalizeExecutable(
        string raw,
        out string normalized,
        out CommandRejectionReason reason)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = CommandRejectionReason.ExecutableEmpty;
            return false;
        }

        var trimmed = raw.Trim();

        if (!Path.IsPathFullyQualified(trimmed))
        {
            reason = CommandRejectionReason.ExecutableNotAbsolute;
            return false;
        }

        if (ContainsTraversal(trimmed))
        {
            reason = CommandRejectionReason.ExecutableTraversal;
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(trimmed);
        }
        catch
        {
            // 非法路径字符等：解析失败默认拒绝。
            reason = CommandRejectionReason.ExecutableNotAbsolute;
            return false;
        }

        reason = CommandRejectionReason.Unknown;
        return true;
    }

    /// <summary>
    /// 匹配可执行模式。三种形式：精确路径（无通配符）、目录前缀通配（"C:\Tools\*"）、
    /// 受限扩展通配（"C:\Tools\*.exe"）。通配符仅允许出现在最后一个路径段且唯一。
    /// </summary>
    private static bool MatchesExecutable(string pattern, string normalizedCandidate)
    {
        if (!TryParseExecutablePattern(pattern, out var root, out var extension, out var exactPath, out var isExact, out _))
        {
            return false;
        }

        if (isExact)
        {
            return string.Equals(exactPath, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
        }

        // 通配：解析后再次确认仍位于允许根之下（防前缀碰撞与路径穿越）。
        if (!IsUnderRoot(root, normalizedCandidate))
        {
            return false;
        }

        return extension is null
            || string.Equals(Path.GetExtension(normalizedCandidate), extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesArguments(string[] patterns, IReadOnlyList<string> arguments)
    {
        // 空模式 = 仅允许无参数形式。
        if (patterns is null || patterns.Length == 0)
        {
            return arguments is null || arguments.Count == 0;
        }

        if (arguments is null || arguments.Count != patterns.Length)
        {
            return false;
        }

        for (var index = 0; index < patterns.Length; index++)
        {
            if (patterns[index] == "*")
            {
                continue; // 任意单个参数。
            }

            if (!string.Equals(patterns[index], arguments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 解析白名单条目可执行模式。返回目录根（通配形式）或精确路径（精确形式）。
    /// </summary>
    private static bool TryParseExecutablePattern(
        string pattern,
        out string root,
        out string? extension,
        out string exactPath,
        out bool isExact,
        out bool isWildcard)
    {
        root = string.Empty;
        extension = null;
        exactPath = string.Empty;
        isExact = false;
        isWildcard = false;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var trimmed = pattern.Trim();

        if (ContainsTraversal(trimmed))
        {
            return false;
        }

        var starIndex = trimmed.IndexOf('*');
        if (starIndex < 0)
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            try
            {
                exactPath = Path.GetFullPath(trimmed);
            }
            catch
            {
                return false;
            }

            isExact = true;
            return true;
        }

        // 含通配符：只允许单个 '*'，且仅在最后一段。
        if (trimmed.IndexOf('*', starIndex + 1) >= 0)
        {
            return false;
        }

        var lastSeparator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSeparator < 0 || starIndex <= lastSeparator)
        {
            return false; // '*' 不在最后一段，或目录为空。
        }

        var directoryPart = trimmed[..lastSeparator];
        var filePart = trimmed[(lastSeparator + 1)..];

        if (directoryPart.Length == 0
            || ContainsTraversal(directoryPart)
            || directoryPart.Contains('*')
            || !Path.IsPathFullyQualified(directoryPart))
        {
            return false;
        }

        try
        {
            root = Path.GetFullPath(directoryPart);
        }
        catch
        {
            return false;
        }

        if (filePart == "*")
        {
            isWildcard = true;
            extension = null;
            return true;
        }

        // *.ext 形式：'*' 必须在最前，且后面为不含通配符/分隔符/额外点的字面扩展名。
        if (filePart.Length > 2 && filePart[0] == '*' && filePart[1] == '.')
        {
            var ext = filePart[1..];
            if (ext.IndexOfAny(new[] { '*', '\\', '/' }) >= 0)
            {
                return false;
            }

            if (ext.Count(character => character == '.') != 1)
            {
                return false;
            }

            extension = ext;
            isWildcard = true;
            return true;
        }

        return false;
    }

    private static bool ContainsTraversal(string path)
    {
        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedRoot.Length == 0)
        {
            return false;
        }

        return candidate.StartsWith(
            trimmedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
