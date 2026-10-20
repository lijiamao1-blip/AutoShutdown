using AutoShutdown.App.Infrastructure.Logging;

namespace AutoShutdown.App.Infrastructure.Appearance;

/// <summary>皮肤切换与持久化。界面层只经此接口选择皮肤，不直接操作 Application 资源。</summary>
public interface ISkinService
{
    IReadOnlyList<SkinInfo> Skins { get; }

    SkinInfo Current { get; }

    event EventHandler? SkinChanged;

    /// <summary>启动时（创建主窗口之前）应用已保存的皮肤；任何失败都回退默认皮肤，绝不抛出。</summary>
    void ApplySavedSkin();

    /// <summary>立即应用并保存。未知 Id 或字典加载失败返回 false，当前外观保持不变。</summary>
    Task<bool> SelectAsync(string skinId, CancellationToken cancellationToken);
}

/// <summary>把皮肤字典放进应用资源（WPF 实现见 <see cref="WpfSkinResourceApplier"/>），便于单元测试替换。</summary>
public interface ISkinResourceApplier
{
    void Apply(SkinInfo skin);
}

public sealed class SkinService : ISkinService
{
    private readonly AppearanceSettingsStore _store;
    private readonly ISkinResourceApplier _applier;
    private readonly IApplicationLogger _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SkinService(AppearanceSettingsStore store, ISkinResourceApplier applier, IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _applier = applier;
        _logger = logger;
    }

    public IReadOnlyList<SkinInfo> Skins => SkinCatalog.All;

    public SkinInfo Current { get; private set; } = SkinCatalog.Default;

    public event EventHandler? SkinChanged;

    public void ApplySavedSkin()
    {
        AppearanceSettingsLoadResult load;
        try
        {
            load = _store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            load = new AppearanceSettingsLoadResult(AppearanceSettingsLoadStatus.IoFailure, Error: exception.Message);
        }

        if (load.Status is not (AppearanceSettingsLoadStatus.Success or AppearanceSettingsLoadStatus.NotFound))
        {
            _logger.Warning("SkinLoadFallback", $"外观设置读取失败（{load.Status}：{load.Error}），使用默认皮肤。");
            return;
        }

        if (load.Status == AppearanceSettingsLoadStatus.NotFound || load.SkinId == SkinCatalog.DefaultId)
        {
            // App.xaml 已合并默认皮肤，无需重复加载。
            return;
        }

        if (!SkinCatalog.TryGet(load.SkinId, out var skin))
        {
            _logger.Warning("SkinLoadFallback", $"未知皮肤「{load.SkinId}」，使用默认皮肤。");
            return;
        }

        if (TryApply(skin))
        {
            Current = skin;
            _logger.Info("SkinApplied", $"已应用保存的皮肤：{skin.Id}。");
        }
    }

    public async Task<bool> SelectAsync(string skinId, CancellationToken cancellationToken)
    {
        if (!SkinCatalog.TryGet(skinId, out var skin))
        {
            _logger.Warning("SkinChangeRejected", $"未知皮肤「{skinId}」，保持当前皮肤。");
            return false;
        }

        if (skin.Id != Current.Id)
        {
            if (!TryApply(skin))
            {
                return false;
            }

            var previous = Current;
            Current = skin;
            _logger.Info("SkinChanged", $"皮肤已切换：{previous.Id} → {skin.Id}。");
            SkinChanged?.Invoke(this, EventArgs.Empty);
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 连续快速点击时只落盘最后生效的选择。
            var latest = Current.Id;
            if (!await _store.SaveAsync(latest, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warning("SkinSaveFailed", $"皮肤已切换但保存失败（{latest}），下次启动将使用上次保存的皮肤。");
            }
        }
        finally
        {
            _saveGate.Release();
        }

        return true;
    }

    private bool TryApply(SkinInfo skin)
    {
        try
        {
            _applier.Apply(skin);
            return true;
        }
        catch (Exception exception)
        {
            // 应用器先完整加载字典再替换，加载失败时界面仍是原皮肤。
            _logger.Warning("SkinLoadFallback", $"皮肤「{skin.Id}」加载失败，保持当前皮肤：{exception.Message}");
            return false;
        }
    }
}
