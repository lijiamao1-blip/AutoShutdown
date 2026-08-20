using System.IO;
using System.Text.Json;

namespace AutoShutdown.App.Infrastructure;

/// <summary>由受控 S-UI3 启动器声明并由应用再次校验的本地 UI 安全测试环境。</summary>
public static class UiTestEnvironment
{
    public const string ModeVariable = "AUTOSHUTDOWN_UI_TEST";
    public const string CommitVariable = "AUTOSHUTDOWN_BUILD_COMMIT";

    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable(ModeVariable), "1", StringComparison.Ordinal);

    public static string BuildCommit =>
        Environment.GetEnvironmentVariable(CommitVariable)?.Trim() ?? string.Empty;

    public static string ExpectedDataRoot => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoShutdown", "UiTestSandbox"));

    public static bool TryValidate(string dataRoot, out string error)
    {
        error = string.Empty;
        if (!IsRequested) return true;

        var fullRoot = Path.GetFullPath(dataRoot);
        if (!string.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                ExpectedDataRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            error = "安全测试数据目录不是固定的 UiTestSandbox。";
            return false;
        }

        if (Directory.Exists(fullRoot)
            && new DirectoryInfo(fullRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            error = "安全测试数据目录是 reparse point，拒绝启动。";
            return false;
        }

        if (BuildCommit.Length < 7)
        {
            error = "缺少有效的 Git 构建提交。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, "config.json")));
            var root = document.RootElement;
            if (!ReadBool(root, "TestMode") || ReadBool(root, "RealPowerEnabled") || ReadBool(root, "StartWithWindows"))
                throw new InvalidDataException("TestMode/RealPowerEnabled/StartWithWindows 不安全。");

            var run = root.GetProperty("RunCommands");
            if (run.GetProperty("Commands").GetArrayLength() != 0
                || run.GetProperty("Whitelist").GetProperty("Allow").GetArrayLength() != 0)
                throw new InvalidDataException("RunCommands 不为空。");

            if (root.GetProperty("CloseApps").GetProperty("Targets").GetArrayLength() != 0)
                throw new InvalidDataException("CloseApps 目标不为空。");

            foreach (var forbidden in new[] { "unattended.json", "remote-devices.json", "remote-pairing-lock.json",
                         "remote-server-cert.dpapi", "remote-imported-cert-password.dpapi" })
            {
                if (File.Exists(Path.Combine(fullRoot, forbidden)))
                    throw new InvalidDataException($"检测到禁止的安全数据：{forbidden}。");
            }

            ValidateDisabledDocument(fullRoot, "task-sync.json");
            ValidateDisabledDocument(fullRoot, "remote-settings.json");
            return true;
        }
        catch (Exception exception)
        {
            error = "安全测试配置校验失败：" + exception.Message;
            return false;
        }
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static void ValidateDisabledDocument(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (ReadBool(document.RootElement, "Enabled"))
            throw new InvalidDataException($"{name} 已启用。");
    }
}
