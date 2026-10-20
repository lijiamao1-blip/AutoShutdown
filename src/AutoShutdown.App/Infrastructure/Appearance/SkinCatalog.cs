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
        new("light-clean", "浅色 · 简洁", "近白办公界面与墨蓝数字，让任务信息清晰有序。", IsDark: false),
        new("dark-night", "深色 · 夜间", "石墨深色表面与低亮蓝光，突出夜间倒计时。", IsDark: true),
        new(DefaultId, "经典 · 蓝紫玻璃", "软件原有的蓝紫渐变玻璃风格，柔光点缀，清新通透。", IsDark: false),
        new("tech-neon", "科技 · 霓虹", "静态网格与青色仪表数字，营造克制的未来控制台。", IsDark: true),
        new("forest-morning", "森林 · 晨光", "晨光天空与层叠针叶林，搭配深森林绿倒计时。", IsDark: false),
        new("bay-cat", "海湾 · 午睡猫", "柔蓝海湾、小木船与蜷睡白猫，留出安静的任务空间。", IsDark: false),
        new("dream-aurora", "梦幻 · 极光", "粉紫薄荷的黎明极光，衬托清晰的紫粉渐变数字。", IsDark: false),
        new("sunset-warm", "暖阳 · 日落", "桃金日落与低饱和远山，以深棕数字守住阅读焦点。", IsDark: false),
        new("ink-wash", "墨韵 · 水墨", "宣纸、叠墨远山与朱砂小印，黛青操作配浓墨数字。", IsDark: false),
        new("eye-care", "护眼 · 豆沙绿", "柔和豆沙绿表面与深墨绿文字，适合长时间常驻。", IsDark: false)
    ];

    public static SkinInfo Default { get; } = All.Single(skin => skin.Id == DefaultId);

    public static bool TryGet(string? id, out SkinInfo skin)
    {
        skin = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)) ?? Default;
        return skin.Id == id;
    }
}
