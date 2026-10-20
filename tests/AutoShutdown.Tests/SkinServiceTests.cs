using AutoShutdown.App.Infrastructure.Appearance;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// 皮肤服务：appearance.json 读写与回退。用真实 FileStorage + 临时目录，
/// 资源应用器替换为记录器（不触碰 WPF Application）。
/// </summary>
public sealed class SkinServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AutoShutdown.SkinTests." + Guid.NewGuid().ToString("N"));

    public SkinServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void NoAppearanceFile_KeepsDefaultSkin_WithoutApplyingOrWarning()
    {
        var (service, applier, logger) = Create();

        service.ApplySavedSkin();

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.Empty(applier.Applied);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void CorruptAppearanceFile_FallsBackToDefault_AndLogsWarning()
    {
        File.WriteAllText(Path.Combine(_root, AppearanceSettingsStore.FileName), "{ this is not json");
        var (service, applier, logger) = Create();

        service.ApplySavedSkin();

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.Empty(applier.Applied);
        Assert.Contains(logger.Warnings, warning => warning.EventName == "SkinLoadFallback");
    }

    [Fact]
    public void UnknownSavedSkin_FallsBackToDefault_AndLogsWarning()
    {
        File.WriteAllText(Path.Combine(_root, AppearanceSettingsStore.FileName), """{ "SchemaVersion": 1, "SkinId": "no-such-skin" }""");
        var (service, applier, logger) = Create();

        service.ApplySavedSkin();

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.Empty(applier.Applied);
        Assert.Contains(logger.Warnings, warning => warning.EventName == "SkinLoadFallback" && warning.Message.Contains("no-such-skin"));
    }

    [Fact]
    public void FutureSchemaVersion_FallsBackToDefault()
    {
        File.WriteAllText(Path.Combine(_root, AppearanceSettingsStore.FileName), """{ "SchemaVersion": 99, "SkinId": "classic-glass" }""");
        var (service, _, logger) = Create();

        service.ApplySavedSkin();

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.Contains(logger.Warnings, warning => warning.EventName == "SkinLoadFallback");
    }

    [Fact]
    public async Task SelectUnknownSkin_IsRejected_AndNothingIsSaved()
    {
        var (service, applier, logger) = Create();

        Assert.False(await service.SelectAsync("no-such-skin", CancellationToken.None));

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.Empty(applier.Applied);
        Assert.False(File.Exists(Path.Combine(_root, AppearanceSettingsStore.FileName)));
        Assert.Contains(logger.Warnings, warning => warning.EventName == "SkinChangeRejected");
    }

    [Fact]
    public async Task SelectSkin_SavesSchemaVersionAndId_AndSurvivesRestart()
    {
        var skin = SkinCatalog.All[^1];
        var (service, _, _) = Create();
        Assert.True(await service.SelectAsync(skin.Id, CancellationToken.None));

        var json = File.ReadAllText(Path.Combine(_root, AppearanceSettingsStore.FileName));
        Assert.Contains("\"SchemaVersion\": 1", json);
        Assert.Contains($"\"SkinId\": \"{skin.Id}\"", json);

        // 「重启」：新的服务实例读取同一数据目录。
        var (restarted, applier, _) = Create();
        restarted.ApplySavedSkin();
        Assert.Equal(skin.Id, restarted.Current.Id);
        if (skin.Id != SkinCatalog.DefaultId)
        {
            Assert.Equal([skin.Id], applier.Applied);
        }
    }

    [Fact]
    public async Task ApplierFailure_KeepsCurrentSkin_AndReportsFailureToViewModel()
    {
        var (service, applier, logger) = Create();
        applier.ThrowOnApply = true;
        var section = new SkinSectionViewModel(service);
        var target = SkinCatalog.All.FirstOrDefault(skin => skin.Id != SkinCatalog.DefaultId);
        if (target is null)
        {
            return; // 目录中只有默认皮肤时无可切换目标。
        }

        Assert.False(await section.SelectAsync(target.Id));

        Assert.Equal(SkinCatalog.DefaultId, service.Current.Id);
        Assert.NotEmpty(section.StatusText);
        Assert.True(section.Options.Single(option => option.Id == SkinCatalog.DefaultId).IsSelected);
        Assert.Contains(logger.Warnings, warning => warning.EventName == "SkinLoadFallback");
    }

    [Fact]
    public async Task SectionViewModel_TracksSelection_AndRaisesSkinChanged()
    {
        var (service, applier, _) = Create();
        var section = new SkinSectionViewModel(service);
        var changed = 0;
        service.SkinChanged += (_, _) => changed++;

        Assert.Equal(SkinCatalog.All.Count, section.Options.Count);
        Assert.Equal(SkinCatalog.DefaultId, section.CurrentOption!.Id);

        foreach (var skin in SkinCatalog.All.Where(skin => skin.Id != SkinCatalog.DefaultId))
        {
            Assert.True(await section.SelectAsync(section.Options.Single(option => option.Id == skin.Id)));
            Assert.Equal(skin.Id, section.CurrentOption!.Id);
            Assert.Single(section.Options, option => option.IsSelected);
            Assert.Equal(skin.DisplayName, section.CurrentDisplayName);
        }

        Assert.Equal(SkinCatalog.All.Count - 1, changed);
        Assert.Equal(SkinCatalog.All.Count - 1, applier.Applied.Count);
    }

    [Fact]
    public void GalleryToggle_OpensAndCloses()
    {
        var (service, _, _) = Create();
        var section = new SkinSectionViewModel(service);

        section.ToggleGalleryCommand.Execute(null);
        Assert.True(section.IsGalleryOpen);
        section.CloseGalleryCommand.Execute(null);
        Assert.False(section.IsGalleryOpen);
    }

    [Fact]
    public void Catalog_HasUniqueIds_DefaultIsClassic_AndUrisPointAtSkinFolder()
    {
        Assert.Equal("classic-glass", SkinCatalog.DefaultId);
        Assert.Equal(SkinCatalog.All.Count, SkinCatalog.All.Select(skin => skin.Id).Distinct().Count());
        Assert.All(SkinCatalog.All, skin => Assert.Equal($"/AutoShutdown.App;component/Themes/Skins/{skin.Id}.xaml", skin.Uri.OriginalString));
        Assert.False(SkinCatalog.TryGet(null, out var fallback));
        Assert.Equal(SkinCatalog.DefaultId, fallback.Id);
    }

    private (SkinService Service, RecordingApplier Applier, RecordingLogger Logger) Create()
    {
        var applier = new RecordingApplier();
        var logger = new RecordingLogger();
        return (new SkinService(new AppearanceSettingsStore(new FileStorage(_root)), applier, logger), applier, logger);
    }

    private sealed class RecordingApplier : ISkinResourceApplier
    {
        public List<string> Applied { get; } = [];

        public bool ThrowOnApply { get; set; }

        public void Apply(SkinInfo skin)
        {
            if (ThrowOnApply)
            {
                throw new InvalidOperationException("皮肤字典损坏");
            }

            Applied.Add(skin.Id);
        }
    }

    private sealed class RecordingLogger : IApplicationLogger
    {
        public List<(string EventName, string Message)> Warnings { get; } = [];

        public string LogDirectory => string.Empty;

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(ApplicationLogLevel level, string eventName, string message, Exception? exception = null)
        {
            if (level == ApplicationLogLevel.Warning)
            {
                Warnings.Add((eventName, message));
            }
        }

        public void Dispose()
        {
        }
    }
}
