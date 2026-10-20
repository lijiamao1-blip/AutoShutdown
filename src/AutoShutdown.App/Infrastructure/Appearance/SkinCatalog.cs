namespace AutoShutdown.App.Infrastructure.Appearance;

/// <summary>一套皮肤的目录信息。显示名与描述同时写在皮肤字典的 SkinDisplayName / SkinDescription 令牌里（契约测试校验两者一致）。</summary>
public sealed record SkinInfo(string Id, string DisplayName, string Description, bool IsDark)
{
    /// <summary>皮肤字典的组件 URI（编译为 App 程序集内的 Page 资源；相对形式无需预先注册 pack 协议）。</summary>
    public Uri Uri { get; } = new($"/AutoShutdown.App;component/Themes/Skins/{Id}.xaml", UriKind.Relative);
}

/// <summary>
/// 皮肤目录：顺序即画廊顺序。皮肤纯属外观，与电源安全配置完全解耦；
/// 未知或损坏的选择一律回退到 <see cref="Default"/>（软件原有外观）。
/// </summary>
public static class SkinCatalog
{
    public const string DefaultId = "classic-glass";

    public static IReadOnlyList<SkinInfo> All { get; } =
    [
        new(DefaultId, "经典 · 蓝紫玻璃", "软件原有的蓝紫渐变玻璃风格，柔光点缀，清新通透。", IsDark: false)
    ];

    public static SkinInfo Default { get; } = All.Single(skin => skin.Id == DefaultId);

    public static bool TryGet(string? id, out SkinInfo skin)
    {
        skin = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)) ?? Default;
        return skin.Id == id;
    }
}
