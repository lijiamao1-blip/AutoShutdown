using System.Text.RegularExpressions;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// 皮肤令牌契约（源码层）：界面只能经 DynamicResource 引用皮肤令牌，不得写死颜色；
/// 被引用的令牌必须全部在经典皮肤 classic-glass.xaml（契约唯一事实源）中定义。
/// 纯源码检查，不启动应用。
/// </summary>
public sealed class SkinContractTests
{
    private static readonly string[] SkinnedXamlFiles =
    [
        "MainWindow.xaml",
        "ReminderWindow.xaml",
        "ProcessPickerWindow.xaml",
        "ProcessPickerSummaryWindow.xaml",
        Path.Combine("Themes", "Controls.xaml"),
        Path.Combine("Themes", "SkinSystemControls.xaml"),
        Path.Combine("Controls", "SkinThumbnail.xaml")
    ];

    private static readonly Regex HexColor = new(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b", RegexOptions.Compiled);
    private static readonly Regex NamedColorAttribute = new(
        @"(?:Foreground|Background|BorderBrush|Fill|Stroke|Color|Value)=""(?:White|Black|Red|Blue|Green|Gray|Yellow)""",
        RegexOptions.Compiled);

    [Fact]
    public void SkinnedXaml_HasNoHardcodedColors()
    {
        foreach (var file in SkinnedXamlFiles)
        {
            var source = StripComments(ReadAppFile(file));
            Assert.False(HexColor.IsMatch(source), $"{file} 仍写死颜色：{HexColor.Match(source).Value}");
            Assert.False(NamedColorAttribute.IsMatch(source), $"{file} 仍写死颜色：{NamedColorAttribute.Match(source).Value}");
            Assert.DoesNotContain("<DropShadowEffect", source);
        }
    }

    [Fact]
    public void SkinnedXaml_ReferencesTokensOnlyThroughDynamicResource()
    {
        var tokens = ReadClassicTokenKeys();
        foreach (var file in SkinnedXamlFiles)
        {
            var source = ReadAppFile(file);
            foreach (Match match in Regex.Matches(source, @"\{StaticResource (\w+)\}"))
            {
                Assert.False(tokens.Contains(match.Groups[1].Value), $"{file} 以 StaticResource 引用令牌 {match.Groups[1].Value}，切换皮肤后不会更新。");
            }
        }
    }

    [Fact]
    public void EveryDynamicResourceReference_IsDefinedByClassicSkin()
    {
        var tokens = ReadClassicTokenKeys();
        foreach (var file in SkinnedXamlFiles.Append(Path.Combine("Themes", "Skins", "classic-glass.xaml")))
        {
            foreach (Match match in Regex.Matches(ReadAppFile(file), @"\{DynamicResource (\w+)\}"))
            {
                Assert.True(tokens.Contains(match.Groups[1].Value), $"{file} 引用了未定义的令牌 {match.Groups[1].Value}。");
            }
        }
    }

    [Fact]
    public void AppXaml_MergesClassicSkinFirst_ThenControls()
    {
        var app = ReadAppFile("App.xaml");
        var skin = app.IndexOf("Themes/Skins/classic-glass.xaml", StringComparison.Ordinal);
        var controls = app.IndexOf("Themes/Controls.xaml", StringComparison.Ordinal);

        Assert.True(skin >= 0, "App.xaml 未合并经典皮肤。");
        Assert.True(controls > skin, "皮肤字典必须是第 0 个合并字典，位于 Controls.xaml 之前。");
        Assert.False(File.Exists(Path.Combine(FindAppSourceRoot(), "Themes", "Colors.xaml")), "Colors.xaml 已并入经典皮肤，不应再存在。");
    }

    [Fact]
    public void ClassicSkin_KeepsOriginalCountdownAppearance()
    {
        // 经典皮肤必须与令牌化之前完全一致：倒计时白字 + #B681FF 紫色辉光，提醒窗口白字 + #C985FF 辉光。
        var classic = ReadAppFile("Themes", "Skins", "classic-glass.xaml");

        Assert.Contains("<SolidColorBrush x:Key=\"CountdownTextBrush\" Color=\"#FFFFFF\" />", classic);
        Assert.Contains("<DropShadowEffect x:Key=\"CountdownGlowEffect\" Color=\"#B681FF\" BlurRadius=\"12\" ShadowDepth=\"0\" Opacity=\"0.55\" />", classic);
        Assert.Contains("<SolidColorBrush x:Key=\"ReminderCountdownBrush\" Color=\"#FFFFFF\" />", classic);
        Assert.Contains("<DropShadowEffect x:Key=\"ReminderCountdownGlowEffect\" Color=\"#C985FF\" BlurRadius=\"16\" ShadowDepth=\"0\" Opacity=\"0.7\" />", classic);
        Assert.Contains("<FontFamily x:Key=\"CountdownFontFamily\">Microsoft YaHei UI</FontFamily>", classic);
    }

    [Fact]
    public void HomeCountdown_KeepsGlowInStyleSetter_SoIdleTriggerCanRemoveIt()
    {
        // 辉光必须放在 Style.Setter 中（不能作元素局部值），空闲态触发器 Effect={x:Null} 才能生效。
        var home = ReadAppFile("MainWindow.xaml");

        Assert.Contains("<Setter Property=\"Effect\" Value=\"{DynamicResource CountdownGlowEffect}\"/>", home);
        Assert.Contains("<Setter Property=\"Effect\" Value=\"{x:Null}\"/>", home);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource CountdownIdleTextBrush}\"/>", home);
        Assert.Contains("ContentTemplate=\"{DynamicResource SkinBackdropTemplate}\" IsHitTestVisible=\"False\"", home);
    }

    internal static HashSet<string> ReadClassicTokenKeys()
        => Regex.Matches(ReadAppFile("Themes", "Skins", "classic-glass.xaml"), @"x:Key=""(\w+)""")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string StripComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    internal static string ReadAppFile(params string[] relativeParts)
        => File.ReadAllText(Path.Combine(new[] { FindAppSourceRoot() }.Concat(relativeParts).ToArray()));

    internal static string FindAppSourceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source directory was not found.");
    }
}
