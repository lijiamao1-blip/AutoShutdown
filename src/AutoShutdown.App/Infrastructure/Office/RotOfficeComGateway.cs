using System.Runtime.InteropServices;
using AutoShutdown.Core.Office;
using Microsoft.VisualBasic;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// 通过 Running Object Table（ROT）附加已运行 Office 实例的网关（S17 独立验收修复）。
/// 绝不调用 Activator.CreateInstance / new Application / 任何会启动 Office 的路径；
/// 无活动对象时返回 null。附加经 <see cref="Interaction.GetObject(string, string)"/>
/// 完成（空路径 + ProgID = 纯 ROT 附加，不创建、不启动），避免在 App 层引入任何原生导入
/// （遵守「原生导入仅存在于电源网关」的冻结不变量）。所有 Office 原生 COM
/// 调用（dynamic + ReleaseComObject）收敛于此，不散落到其他文件。真机路径，S17 自动化测试不调用它。
/// </summary>
public sealed class RotOfficeComGateway : IOfficeComGateway
{
    public IOfficeComApplication? TryAttach(OfficeApplicationKind application)
    {
        var progId = ResolveProgId(application);
        if (progId is null)
        {
            return null;
        }

        object? comObject;
        try
        {
            // 空路径 + ProgID：GetObject 仅查询 ROT 中已运行实例；无运行实例时抛异常（MK_E_UNAVAILABLE），
            // 绝不创建新实例。进程探测与附加之间的退出竞态在此安全失败为 null。
            comObject = Interaction.GetObject(string.Empty, progId);
        }
        catch
        {
            return null;
        }

        return comObject is null ? null : new RotOfficeComApplication(application, comObject);
    }

    private static string? ResolveProgId(OfficeApplicationKind application) => application switch
    {
        OfficeApplicationKind.Word => "Word.Application",
        OfficeApplicationKind.Excel => "Excel.Application",
        OfficeApplicationKind.PowerPoint => "PowerPoint.Application",
        _ => null
    };
}

/// <summary>ROT 附加得到的应用会话封装；仅枚举文档并释放引用，绝不 Quit。</summary>
internal sealed class RotOfficeComApplication : IOfficeComApplication
{
    private readonly OfficeApplicationKind _kind;
    private readonly object _application;
    private bool _disposed;

    public RotOfficeComApplication(OfficeApplicationKind kind, object application)
    {
        _kind = kind;
        _application = application;
    }

    public IReadOnlyList<IOfficeComDocument> GetOpenDocuments()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        dynamic app = _application;
        dynamic documents = _kind switch
        {
            OfficeApplicationKind.Word => app.Documents,
            OfficeApplicationKind.Excel => app.Workbooks,
            OfficeApplicationKind.PowerPoint => app.Presentations,
            _ => throw new InvalidOperationException($"Unknown Office application: {_kind}.")
        };

        var result = new List<IOfficeComDocument>();
        try
        {
            var count = (int)documents.Count;
            for (var index = 1; index <= count; index++)
            {
                object document = documents.Item(index);
                result.Add(new RotOfficeComDocument(document));
            }

            return result;
        }
        catch
        {
            // 枚举失败时释放已取得的文档包装，避免遗留 COM 引用。
            foreach (var document in result)
            {
                document.Dispose();
            }

            throw;
        }
        finally
        {
            ComRelease.Release(documents);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComRelease.Release(_application);
    }
}

/// <summary>单个文档的 COM 封装；仅提供 HasPath/Save 与释放，绝不 Quit、绝不另存为。</summary>
internal sealed class RotOfficeComDocument : IOfficeComDocument
{
    private readonly object _document;
    private bool _disposed;

    public RotOfficeComDocument(object document)
    {
        _document = document;
    }

    public bool HasPath
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            dynamic document = _document;
            return !string.IsNullOrEmpty(document.Path as string);
        }
    }

    public void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        dynamic document = _document;
        document.Save();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComRelease.Release(_document);
    }
}

/// <summary>防御式 COM 引用释放；释放失败不拖垮保存流程。</summary>
internal static class ComRelease
{
    public static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // 释放失败不拖垮保存；真机路径记录限制。
        }
    }
}
