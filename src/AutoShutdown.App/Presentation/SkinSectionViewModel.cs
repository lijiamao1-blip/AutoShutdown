using System.Windows.Input;
using AutoShutdown.App.Infrastructure.Appearance;

namespace AutoShutdown.App.Presentation;

/// <summary>画廊中的一张皮肤卡片。</summary>
public sealed class SkinOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public SkinOptionViewModel(SkinInfo skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        Skin = skin;
    }

    public SkinInfo Skin { get; }

    public string Id => Skin.Id;

    public string DisplayName => Skin.DisplayName;

    public string Description => Skin.Description;

    public Uri SkinUri => Skin.Uri;

    /// <summary>读屏名称：皮肤名 + 是否当前皮肤。</summary>
    public string AutomationName => IsSelected ? $"{DisplayName}（当前皮肤）" : $"{DisplayName}皮肤";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }
}

/// <summary>
/// 皮肤选择（顶栏「皮肤」画廊与软件设置页「外观 · 皮肤」卡片共用）：点击即应用并自动保存。
/// 不直接操作 Application 资源，全部经 <see cref="ISkinService"/>。
/// </summary>
public sealed class SkinSectionViewModel : ObservableObject
{
    private readonly ISkinService _skinService;
    private bool _isGalleryOpen;
    private string _statusText = string.Empty;

    public SkinSectionViewModel(ISkinService skinService)
    {
        ArgumentNullException.ThrowIfNull(skinService);
        _skinService = skinService;
        Options = skinService.Skins.Select(skin => new SkinOptionViewModel(skin)).ToList();
        SelectSkinCommand = new RelayCommand(parameter => _ = SelectAsync(parameter));
        ToggleGalleryCommand = new RelayCommand(() => IsGalleryOpen = !IsGalleryOpen);
        CloseGalleryCommand = new RelayCommand(() => IsGalleryOpen = false);
        _skinService.SkinChanged += (_, _) => SyncSelection();
        SyncSelection();
    }

    public IReadOnlyList<SkinOptionViewModel> Options { get; }

    public SkinOptionViewModel? CurrentOption => Options.FirstOrDefault(option => option.IsSelected);

    public string CurrentDisplayName => _skinService.Current.DisplayName;

    public string CurrentDescription => _skinService.Current.Description;

    public bool IsGalleryOpen
    {
        get => _isGalleryOpen;
        set => SetProperty(ref _isGalleryOpen, value);
    }

    /// <summary>仅在切换失败时显示（例如皮肤文件损坏）；成功时为空。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand SelectSkinCommand { get; }

    public ICommand ToggleGalleryCommand { get; }

    public ICommand CloseGalleryCommand { get; }

    public async Task<bool> SelectAsync(object? parameter)
    {
        var id = parameter switch
        {
            SkinOptionViewModel option => option.Id,
            string text => text,
            _ => null
        };
        if (id is null)
        {
            return false;
        }

        var applied = await _skinService.SelectAsync(id, CancellationToken.None);
        StatusText = applied ? string.Empty : "该皮肤加载失败，已保持当前外观。";
        SyncSelection();
        return applied;
    }

    private void SyncSelection()
    {
        foreach (var option in Options)
        {
            option.IsSelected = option.Id == _skinService.Current.Id;
        }

        OnPropertyChanged(nameof(CurrentOption));
        OnPropertyChanged(nameof(CurrentDisplayName));
        OnPropertyChanged(nameof(CurrentDescription));
    }
}
