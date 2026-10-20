using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace AutoShutdown.App.Controls;

/// <summary>
/// 皮肤缩略图：把 <see cref="SkinUri"/> 指向的皮肤字典合并进自身 Resources，内部 DynamicResource
/// 就近解析到那套皮肤的令牌。宽度由布局决定，高度按 <see cref="AspectRatio"/> 自动得出；圆角裁切。
/// </summary>
public partial class SkinThumbnail : UserControl
{
    public static readonly DependencyProperty SkinUriProperty = DependencyProperty.Register(
        nameof(SkinUri),
        typeof(Uri),
        typeof(SkinThumbnail),
        new PropertyMetadata(null, OnSkinUriChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(SkinThumbnail),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>宽高比（宽 / 高），默认 132:92。</summary>
    public const double AspectRatio = 132d / 92d;

    private const double FallbackWidth = 132;

    public SkinThumbnail()
    {
        InitializeComponent();
    }

    public Uri? SkinUri
    {
        get => (Uri?)GetValue(SkinUriProperty);
        set => SetValue(SkinUriProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : FallbackWidth;
        var size = new Size(width, width / AspectRatio);
        base.MeasureOverride(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var size = new Size(arrangeBounds.Width, arrangeBounds.Width / AspectRatio);
        Frame.Clip = new RectangleGeometry(new Rect(size), CornerRadius, CornerRadius);
        return base.ArrangeOverride(size);
    }

    private static void OnSkinUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var thumbnail = (SkinThumbnail)d;
        var merged = thumbnail.Resources.MergedDictionaries;
        merged.Clear();
        if (e.NewValue is Uri uri)
        {
            try
            {
                merged.Add(new ResourceDictionary { Source = uri });
            }
            catch (Exception)
            {
                // 缩略图只是预览：字典损坏时退回显示当前皮肤，选择时由 SkinService 拒绝并提示。
            }
        }
    }
}
