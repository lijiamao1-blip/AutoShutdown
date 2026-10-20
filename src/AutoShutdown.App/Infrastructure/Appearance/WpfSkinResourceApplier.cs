using System.Windows;

namespace AutoShutdown.App.Infrastructure.Appearance;

/// <summary>
/// 替换 Application.Resources 的第 0 个合并字典（App.xaml 约定该位置为皮肤字典）。
/// 界面全部经 DynamicResource 引用令牌，替换后已创建的窗口立即重新取色。
/// </summary>
public sealed class WpfSkinResourceApplier : ISkinResourceApplier
{
    public void Apply(SkinInfo skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("No WPF application is available.");

        // 先完整加载并校验，再替换：加载失败时不触碰当前资源。
        var dictionary = new ResourceDictionary { Source = skin.Uri };
        if (!string.Equals(dictionary["SkinId"] as string, skin.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"皮肤字典的 SkinId 与目录不一致：{skin.Uri}");
        }

        var merged = application.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            merged[0] = dictionary;
        }
    }
}
