using System.Windows;
using System.Windows.Media;
using AutoShutdown.App.Infrastructure.Appearance;
using Xunit;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace AutoShutdown.Tests;

/// <summary>
/// 皮肤文件契约（加载层）：Themes/Skins 下每个皮肤字典都能作为编译后的资源加载；
/// x:Key 集合与类型和经典皮肤完全一致；关键配色满足对比度、危险色辨识度与提醒窗口不透明度要求。
/// 在 STA 线程加载，不创建 Application、不打开窗口。
/// </summary>
public sealed class SkinFileContractTests
{
    private const string ClassicId = "classic-glass";

    public static TheoryData<string> SkinIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in EnumerateSkinIds())
        {
            data.Add(id);
        }

        return data;
    }

    [Fact]
    public void ClassicSkinFile_Exists()
    {
        Assert.Contains(ClassicId, EnumerateSkinIds());
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void SkinFile_HasExactlyTheClassicTokenKeys_WithMatchingTypes(string skinId)
    {
        var (classic, skin) = OnSta(() => (Snapshot(Load(ClassicId)), Snapshot(Load(skinId))));

        var missing = classic.Keys.Except(skin.Keys).OrderBy(key => key).ToList();
        var extra = skin.Keys.Except(classic.Keys).OrderBy(key => key).ToList();
        Assert.True(missing.Count == 0, $"{skinId} 缺少令牌：{string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{skinId} 多出令牌：{string.Join(", ", extra)}");

        foreach (var (key, classicType) in classic)
        {
            var skinType = skin[key];
            var compatible = typeof(Brush).IsAssignableFrom(classicType)
                ? typeof(Brush).IsAssignableFrom(skinType)
                : skinType == classicType;
            Assert.True(compatible, $"{skinId} 的 {key} 类型为 {skinType.Name}，契约要求 {classicType.Name}。");
        }
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void SkinFile_MetadataMatchesFileNameAndCatalog(string skinId)
    {
        var (id, name, description, isDark) = OnSta(() =>
        {
            var dictionary = Load(skinId);
            return ((string)dictionary["SkinId"], (string)dictionary["SkinDisplayName"], (string)dictionary["SkinDescription"], (bool)dictionary["SkinIsDark"]);
        });

        Assert.Equal(skinId, id);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(string.IsNullOrWhiteSpace(description));
        if (SkinCatalog.TryGet(skinId, out var info))
        {
            Assert.Equal(info.DisplayName, name);
            Assert.Equal(info.Description, description);
            Assert.Equal(info.IsDark, isDark);
        }
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void NonClassicSkins_MergeSystemControlStyles_ClassicDoesNot(string skinId)
    {
        var source = SkinContractTests.ReadAppFile("Themes", "Skins", skinId + ".xaml");
        var merges = source.Contains("/AutoShutdown.App;component/Themes/SkinSystemControls.xaml", StringComparison.Ordinal);

        Assert.Equal(skinId != ClassicId, merges);
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void SkinFile_KeyColorPairs_MeetContrastRequirements(string skinId)
    {
        var failures = OnSta(() =>
        {
            var d = Load(skinId);
            var window = Colors(d, "WindowBackgroundBrush");
            var reminderWindow = Colors(d, "ReminderWindowBackgroundBrush");
            var glass = Over(Colors(d, "GlassFillBrush"), window);
            var accentCard = Over(Colors(d, "GlassFillPurpleBrush"), window);
            var reminderCard = Over(Colors(d, "ReminderCardFillBrush"), reminderWindow);
            var header = Over(Colors(d, "HeaderBackgroundBrush"), window);
            var nav = Over(Colors(d, "NavBackgroundBrush"), window);

            var checks = new List<(string Name, IReadOnlyList<Color> Foreground, IReadOnlyList<Color> Background, double Minimum)>
            {
                ("正文 TextPrimaryBrush / 卡片", Colors(d, "TextPrimaryBrush"), glass, 4.5),
                ("次要文字 TextSecondaryBrush / 卡片", Colors(d, "TextSecondaryBrush"), glass, 4.5),
                ("提示文字 TextHintBrush / 卡片", Colors(d, "TextHintBrush"), glass, 3.0),
                ("错误文字 ErrorTextBrush / 卡片", Colors(d, "ErrorTextBrush"), glass, 4.5),
                ("页面标题 PageTextBrush / 窗口背景", Colors(d, "PageTextBrush"), window, 4.5),
                ("当前任务信息 AccentCardTextBrush / 强调卡片", Colors(d, "AccentCardTextBrush"), accentCard, 4.5),
                ("倒计时紧急态 CountdownUrgentBrush / 强调卡片", Colors(d, "CountdownUrgentBrush"), accentCard, 4.5),
                ("空闲等待小字 CountdownIdleTextBrush / 强调卡片", Colors(d, "CountdownIdleTextBrush"), accentCard, 4.5),
                ("倒计时状态行 CountdownCaptionBrush / 强调卡片", Colors(d, "CountdownCaptionBrush"), accentCard, 4.5),
                ("提醒标题 ReminderTitleBrush / 提醒卡片", Colors(d, "ReminderTitleBrush"), reminderCard, 4.5),
                ("提醒说明 ReminderMessageBrush / 提醒卡片", Colors(d, "ReminderMessageBrush"), reminderCard, 4.5),
                ("提醒倒计时紧急态 / 提醒卡片", Colors(d, "ReminderCountdownUrgentBrush"), reminderCard, 4.5),
                ("危险按钮文字 / 危险实心底", Colors(d, "DangerTextOnSolidBrush"), Colors(d, "DangerSolidBrush"), 4.5),
                ("描边危险按钮 DangerBrush / 卡片", Colors(d, "DangerBrush"), glass, 3.0),
                ("描边危险按钮 DangerBrush / 提醒卡片", Colors(d, "DangerBrush"), reminderCard, 3.0),
                ("主按钮文字 / 主按钮底", Colors(d, "PrimaryButtonTextBrush"), Colors(d, "AccentBlueBrush"), 3.0),
                ("次级按钮文字 / 卡片", Colors(d, "SecondaryButtonTextBrush"), glass, 3.0),
                ("模式标签文字 / 标签底", Colors(d, "ModeTagTextBrush"), Over(Colors(d, "ModeTagBackgroundBrush"), header), 4.5),
                ("软件名 HeaderTitleBrush / 顶栏", Colors(d, "HeaderTitleBrush"), header, 4.5),
                ("顶栏按钮文字 / 顶栏按钮", Colors(d, "HeaderButtonTextBrush"), Over(Colors(d, "HeaderButtonBackgroundBrush"), header), 4.5),
                ("导航文字 NavTextBrush / 导航栏", Colors(d, "NavTextBrush"), nav, 4.5),
                ("导航选中文字 / 选中底", Colors(d, "HighlightTextBrush"), Over(Colors(d, "HighlightBrush"), nav), 3.0),
                ("提示胶囊文字 / 胶囊", Colors(d, "TooltipTextBrush"), Over(Colors(d, "TooltipBackgroundBrush"), window), 4.5),
                ("日历日期 / 日历底", Colors(d, "CalendarDayTextBrush"), Colors(d, "CalendarBackgroundBrush"), 4.5)
            };

            // 经典皮肤的大号倒计时是原有的「白字 + 紫色辉光」（靠辉光勾边），按「外观完全不变」的要求豁免。
            if (skinId != ClassicId)
            {
                checks.Add(("倒计时 CountdownTextBrush / 强调卡片", Colors(d, "CountdownTextBrush"), accentCard, 4.5));
                checks.Add(("提醒倒计时 / 提醒卡片", Colors(d, "ReminderCountdownBrush"), reminderCard, 4.5));
                checks.Add(("输入框文字 / 输入框", Colors(d, "TextPrimaryBrush"), Over(Colors(d, "InputBackgroundBrush"), glass), 4.5));
                checks.Add(("下拉浮层文字 / 浮层", Colors(d, "TextPrimaryBrush"), Over(Colors(d, "PopupBackgroundBrush"), window), 4.5));
                checks.Add(("表格文字 / 表格底", Colors(d, "TextPrimaryBrush"), Over(Colors(d, "DataGridBackgroundBrush"), window), 4.5));
            }

            var messages = new List<string>();
            foreach (var (name, foreground, background, minimum) in checks)
            {
                var worst = MinContrast(foreground, background);
                if (worst < minimum)
                {
                    messages.Add($"{name}：{worst:F2} < {minimum}");
                }
            }

            return messages;
        });

        Assert.True(failures.Count == 0, $"{skinId} 对比度不达标：\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void StatusCards_AreThreeDistinctTints_ThatStandOutFromTheCard_WithReadableText(string skinId)
    {
        // 首页「当前任务」卡底部三张状态小卡（任务状态 / 调度服务 / 配置状态）：与经典皮肤一样各用一种色调，
        // 彼此可辨、也要从所在卡片上凸显出来，标题与取值文字都要清楚。
        var failures = OnSta(() =>
        {
            var d = Load(skinId);
            var card = Over(Colors(d, "GlassFillPurpleBrush"), Colors(d, "WindowBackgroundBrush"));
            var tints = new[] { "StatusCardCyanBrush", "StatusCardBlueBrush", "StatusCardGreenBrush" }
                .Select(key => (Key: key, Colors: Over(Colors(d, key), card)))
                .ToList();
            var messages = new List<string>();
            for (var i = 0; i < tints.Count; i++)
            {
                if (MinDistance(tints[i].Colors, card) < 18)
                {
                    messages.Add($"{tints[i].Key} 与所在卡片几乎同色（距离 {MinDistance(tints[i].Colors, card):F0} < 18）");
                }

                for (var j = i + 1; j < tints.Count; j++)
                {
                    if (MinDistance(tints[i].Colors, tints[j].Colors) < 20)
                    {
                        messages.Add($"{tints[i].Key} 与 {tints[j].Key} 难以区分（距离 {MinDistance(tints[i].Colors, tints[j].Colors):F0} < 20）");
                    }
                }

                var title = MinContrast(Colors(d, "StatusCardTitleBrush"), tints[i].Colors);
                var value = MinContrast(Colors(d, "StatusCardValueBrush"), tints[i].Colors);
                if (title < 4.5)
                {
                    messages.Add($"{tints[i].Key} 上的标题对比度 {title:F2} < 4.5");
                }

                if (value < 3.0)
                {
                    messages.Add($"{tints[i].Key} 上的取值对比度 {value:F2} < 3");
                }
            }

            return messages;
        });

        Assert.True(failures.Count == 0, $"{skinId} 状态小卡：\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void DisabledDangerButton_IsLegibleAndStandsOffTheCard(string skinId)
    {
        // 禁用的危险实心按钮（如「清除记录」）：半透明方案沿用正常态配色；不透明方案（深色皮肤）须文字可读、轮廓可见。
        var (opacity, text, background, border, card) = OnSta(() =>
        {
            var d = Load(skinId);
            return ((double)d["DangerSolidDisabledOpacity"], Colors(d, "DangerSolidDisabledTextBrush"),
                Colors(d, "DangerSolidDisabledBrush"), Colors(d, "DangerSolidDisabledBorderBrush"),
                Over(Colors(d, "GlassFillPurpleBrush"), Colors(d, "WindowBackgroundBrush")));
        });

        Assert.InRange(opacity, 0.4, 1.0);
        if (opacity >= 0.99)
        {
            Assert.True(MinContrast(text, Over(background, card)) >= 3.0, $"{skinId} 禁用态文字对比度不足 3:1。");
            Assert.True(MinContrast(border, card) >= 1.5, $"{skinId} 禁用态描边在卡片上看不出轮廓。");
        }
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void SkinFile_DangerIsUnmistakablyRed_AndDistinctFromAccent(string skinId)
    {
        var (danger, dangerSolid, accent) = OnSta(() =>
        {
            var d = Load(skinId);
            return (Colors(d, "DangerBrush"), Colors(d, "DangerSolidBrush"), Colors(d, "AccentBlueBrush"));
        });

        foreach (var color in danger.Concat(dangerSolid))
        {
            var (hue, saturation, _) = Hsl(color);
            Assert.True(hue >= 350 || hue <= 10, $"{skinId} 危险色 {color} 色相 {hue:F0}° 不在 350°–10°。");
            Assert.True(saturation >= 0.6, $"{skinId} 危险色 {color} 饱和度 {saturation:F2} < 0.6。");
            foreach (var accentColor in accent)
            {
                var (accentHue, accentSaturation, _) = Hsl(accentColor);
                if (accentSaturation < 0.15)
                {
                    continue; // 近灰/墨色主色与红色天然可分。
                }

                var distance = Math.Abs(hue - accentHue);
                distance = Math.Min(distance, 360 - distance);
                Assert.True(distance >= 25, $"{skinId} 危险色与主色 {accentColor} 色相仅差 {distance:F0}°。");
            }
        }
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void ReminderWindow_IsOpaqueEnough_AndCalendarStaysLight(string skinId)
    {
        var (reminder, calendar) = OnSta(() =>
        {
            var d = Load(skinId);
            return (Colors(d, "ReminderWindowBackgroundBrush"), Colors(d, "CalendarBackgroundBrush"));
        });

        Assert.All(reminder, color => Assert.True(color.A >= 235, $"{skinId} 提醒窗口背景 {color} 不透明度低于 0.92。"));
        Assert.All(calendar, color => Assert.True(Luminance(color) >= 0.6, $"{skinId} 日历底色 {color} 不是浅色（标题与星期行沿用系统深色文字）。"));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void CountdownFont_UsesTabularDigitFonts(string skinId)
    {
        var family = OnSta(() => ((FontFamily)Load(skinId)["CountdownFontFamily"]).Source);
        var first = family.Split(',')[0].Trim();
        var allowed = skinId == ClassicId
            ? new[] { "Microsoft YaHei UI" }
            : new[] { "Bahnschrift", "Segoe UI", "Segoe UI Variable", "Segoe UI Variable Display", "Consolas" };

        Assert.Contains(first, allowed);
    }

    // ---------------------------------------------------------------- helpers

    internal static IReadOnlyList<string> EnumerateSkinIds()
        => Directory.EnumerateFiles(Path.Combine(SkinContractTests.FindAppSourceRoot(), "Themes", "Skins"), "*.xaml")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    internal static ResourceDictionary Load(string skinId)
    {
        EnsurePackScheme();
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/AutoShutdown.App;component/Themes/Skins/{skinId}.xaml", UriKind.Absolute)
        };
    }

    /// <summary>
    /// 测试进程里没有 Application 实例（也不能新建：其他测试会建）。访问静态成员即可触发 Application 的
    /// 静态构造，它会注册 pack: 协议并预载 application:/// 资源包，组件 URI 才能解析。
    /// </summary>
    private static void EnsurePackScheme() => _ = System.Windows.Application.Current;

    private static Dictionary<string, Type> Snapshot(ResourceDictionary dictionary)
        => dictionary.Keys.OfType<string>().ToDictionary(key => key, key => dictionary[key].GetType(), StringComparer.Ordinal);

    internal static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA 加载失败：" + failure.Message, failure);
        }

        return result;
    }

    /// <summary>画刷的全部代表色（纯色 1 个；渐变取每个色标），已乘上画刷 Opacity。</summary>
    internal static IReadOnlyList<Color> Colors(ResourceDictionary dictionary, string key)
    {
        var brush = (Brush)dictionary[key];
        IEnumerable<Color> colors = brush switch
        {
            SolidColorBrush solid => [solid.Color],
            GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color),
            _ => throw new InvalidOperationException($"{key} 不是纯色或渐变画刷，无法计算对比度。")
        };
        return colors.Select(color => Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B)).ToList();
    }

    /// <summary>半透明前景色叠在背景每个色标上（背景本身若半透明，先叠在白色上——窗口背景应为不透明）。</summary>
    internal static IReadOnlyList<Color> Over(IReadOnlyList<Color> top, IReadOnlyList<Color> bottom)
    {
        var results = new List<Color>();
        foreach (var under in bottom.Select(color => Blend(color, System.Windows.Media.Colors.White)))
        {
            results.AddRange(top.Select(color => Blend(color, under)));
        }

        return results;
    }

    private static Color Blend(Color top, Color bottom)
    {
        var alpha = top.A / 255d;
        byte Mix(byte a, byte b) => (byte)Math.Round(a * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    /// <summary>两组颜色之间最接近的一对的 RGB 欧氏距离（0–441）。</summary>
    private static double MinDistance(IReadOnlyList<Color> a, IReadOnlyList<Color> b)
        => a.SelectMany(x => b.Select(y => Math.Sqrt(Math.Pow(x.R - y.R, 2) + Math.Pow(x.G - y.G, 2) + Math.Pow(x.B - y.B, 2)))).Min();

    internal static double MinContrast(IReadOnlyList<Color> foreground, IReadOnlyList<Color> background)
    {
        var worst = double.MaxValue;
        foreach (var back in background)
        {
            foreach (var fore in foreground)
            {
                worst = Math.Min(worst, Contrast(Blend(fore, back), back));
            }
        }

        return worst;
    }

    internal static double Contrast(Color a, Color b)
    {
        var (light, dark) = (Luminance(a), Luminance(b));
        if (light < dark)
        {
            (light, dark) = (dark, light);
        }

        return (light + 0.05) / (dark + 0.05);
    }

    internal static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static (double Hue, double Saturation, double Lightness) Hsl(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max - min < 1e-9)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (max == r)
        {
            hue = (g - b) / delta + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            hue = (b - r) / delta + 2;
        }
        else
        {
            hue = (r - g) / delta + 4;
        }

        return (hue * 60, saturation, lightness);
    }
}
