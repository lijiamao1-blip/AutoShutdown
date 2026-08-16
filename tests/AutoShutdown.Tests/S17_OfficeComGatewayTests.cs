using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.Core.Office;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S17 独立验收缺陷一修复的结构契约与编排测试。验证 ComOfficeAutomation 仅通过
/// IOfficeComGateway「附加」Running Object Table 中已运行实例，绝不创建/启动 Office、
/// 绝不 Quit，并在成功 / 异常 / 取消路径全部释放取得的包装。全程注入替身网关，
/// 绝不触碰真实 Office。
/// </summary>
public sealed class S17_OfficeComGatewayTests
{
    [Fact]
    public void GatewayContract_ExposesOnlyTryAttach_NoCreatePath()
    {
        // 结构契约：网关只暴露「附加」入口，不存在任何创建 Office 的方法。
        var methods = typeof(IOfficeComGateway).GetMethods();

        Assert.Single(methods);
        Assert.Equal(nameof(IOfficeComGateway.TryAttach), methods[0].Name);
    }

    [Fact]
    public void ApplicationContract_HasNoQuitOrCreate()
    {
        // 结构契约：应用会话无 Quit、无创建入口，仅枚举文档与释放。
        var methods = typeof(IOfficeComApplication).GetMethods();

        Assert.DoesNotContain(methods, method => method.Name is "Quit" or "Create" or "CreateInstance");
        Assert.Contains(methods, method => method.Name == nameof(IOfficeComApplication.GetOpenDocuments));
    }

    [Fact]
    public void ActiveObject_AttachesAndSaves_NoQuit()
    {
        var document = new FakeDocument(hasPath: true);
        var application = new FakeApplication(new[] { document });
        var gateway = new FakeGateway(application);
        var automation = new ComOfficeAutomation(gateway);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.Success, result.Status);
        Assert.Equal(1, result.SavedCount);
        Assert.Equal(1, gateway.AttachCalls);
        Assert.True(document.SaveCalled);
        Assert.True(document.Disposed);
        Assert.True(application.Disposed);
    }

    [Fact]
    public void NoActiveObject_DoesNotCreate_ReportsNotDetected()
    {
        var gateway = new FakeGateway(null);
        var automation = new ComOfficeAutomation(gateway);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.NotDetected, result.Status);
        Assert.Equal(1, gateway.AttachCalls);
        // 网关无创建路径（结构契约已断言），无活动对象时绝不创建替代实例。
    }

    [Fact]
    public void EnumerateThrows_ReportsNotDetected_ReleasesApplication()
    {
        var application = new FakeApplication(null!) { ThrowOnGetDocuments = true };
        var gateway = new FakeGateway(application);
        var automation = new ComOfficeAutomation(gateway);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.NotDetected, result.Status);
        Assert.True(application.Disposed);
    }

    [Fact]
    public void NoPathDocument_CountsNoPath_DoesNotSave()
    {
        var document = new FakeDocument(hasPath: false);
        var application = new FakeApplication(new[] { document });
        var gateway = new FakeGateway(application);
        var automation = new ComOfficeAutomation(gateway);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.PartialFailure, result.Status);
        Assert.Equal(0, result.SavedCount);
        Assert.Equal(1, result.NoPathCount);
        Assert.False(document.SaveCalled);
        Assert.True(document.Disposed);
        Assert.True(application.Disposed);
    }

    [Fact]
    public void SaveThrows_ReportsPartialFailure_AndReleasesAllDocuments()
    {
        var doc1 = new FakeDocument(hasPath: true);
        var doc2 = new FakeDocument(hasPath: true) { OnSave = () => throw new InvalidOperationException("boom") };
        var doc3 = new FakeDocument(hasPath: true);
        var application = new FakeApplication(new[] { doc1, doc2, doc3 });
        var gateway = new FakeGateway(application);
        var automation = new ComOfficeAutomation(gateway);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.PartialFailure, result.Status);
        Assert.Equal(2, result.SavedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(doc1.Disposed);
        Assert.True(doc2.Disposed);
        Assert.True(doc3.Disposed);
        Assert.True(application.Disposed);
    }

    [Fact]
    public void Cancel_Propagates_AndReleasesApplicationAndDocuments()
    {
        var document = new FakeDocument(hasPath: true);
        var application = new FakeApplication(new[] { document });
        var gateway = new FakeGateway(application);
        var automation = new ComOfficeAutomation(gateway);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        Assert.False(document.SaveCalled);
        Assert.True(document.Disposed);
        Assert.True(application.Disposed);
    }

    private sealed class FakeGateway : IOfficeComGateway
    {
        private readonly IOfficeComApplication? _application;

        public FakeGateway(IOfficeComApplication? application) => _application = application;

        public int AttachCalls { get; private set; }

        public IOfficeComApplication? TryAttach(OfficeApplicationKind application)
        {
            AttachCalls++;
            return _application;
        }
    }

    private sealed class FakeApplication : IOfficeComApplication
    {
        private readonly IReadOnlyList<IOfficeComDocument> _documents;

        public FakeApplication(IReadOnlyList<IOfficeComDocument> documents) => _documents = documents;

        public bool ThrowOnGetDocuments { get; init; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<IOfficeComDocument> GetOpenDocuments()
        {
            if (ThrowOnGetDocuments)
            {
                throw new InvalidOperationException("enumerate boom");
            }

            return _documents;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDocument : IOfficeComDocument
    {
        private readonly bool _hasPath;

        public FakeDocument(bool hasPath) => _hasPath = hasPath;

        public Action? OnSave { get; init; }

        public bool SaveCalled { get; private set; }

        public bool Disposed { get; private set; }

        public bool HasPath => _hasPath;

        public void Save()
        {
            SaveCalled = true;
            OnSave?.Invoke();
        }

        public void Dispose() => Disposed = true;
    }
}
