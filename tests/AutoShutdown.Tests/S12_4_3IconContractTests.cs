using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Windows.Media.Imaging;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.4.3 图标生成、接入与验收契约测试。
/// 只读静态检查：不构建、不发布、不写注册表、不启动应用、不触碰电源。
/// 覆盖：csproj 配置、ICO 尺寸与兼容性、PNG 透明度、窗口/托盘接入、
///      已构建 EXE 图标资源、嵌入资源、FakePowerService 与无真实电源 API。
/// </summary>
public sealed class S12_4_3IconContractTests
{
    // ---- 1. csproj 已配置 ApplicationIcon 与资源嵌入 ----

    [Fact]
    public void AppProject_ConfiguresApplicationIcon()
    {
        var csproj = ReadProjectFile("src", "AutoShutdown.App", "AutoShutdown.App.csproj");

        Assert.Contains("ApplicationIcon", csproj);
        Assert.Contains("..\\..\\assets\\icon.ico", csproj);
    }

    [Fact]
    public void AppProject_EmbedsIconAsWpfResource()
    {
        var csproj = ReadProjectFile("src", "AutoShutdown.App", "AutoShutdown.App.csproj");

        Assert.Contains("<Resource Include=\"..\\..\\assets\\icon.ico\">", csproj);
        Assert.Contains("<LogicalName>assets/icon.ico</LogicalName>", csproj);
    }

    // ---- 2. ICO 存在且包含规定尺寸 ----

    [Fact]
    public void IconIco_ExistsAndContainsRequiredSizes()
    {
        var bytes = File.ReadAllBytes(GetAssetPath("icon.ico"));

        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + i * 16;
            var width = bytes[offset];
            var height = bytes[offset + 1];
            var bpp = BitConverter.ToUInt16(bytes, offset + 6);
            Assert.Equal(1, BitConverter.ToUInt16(bytes, offset + 4)); // planes
            Assert.Equal(32, bpp);
            sizes.Add(width == 0 ? 256 : width);
            Assert.Equal(height == 0 ? 256 : height, sizes[^1]);
        }

        Assert.Equal(
            new[] { 16, 24, 32, 48, 64, 128, 256 },
            sizes.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void IconIco_LoadableBySystemDrawing()
    {
        var path = GetAssetPath("icon.ico");

        using var icon = new Icon(path);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);

        using var small = new Icon(path, 16, 16);
        using var bitmap = small.ToBitmap();
        Assert.True(
            (bitmap.PixelFormat & PixelFormat.Alpha) != 0,
            "16px 帧应带 Alpha 通道");
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
    }

    [Fact]
    public void IconIco_ReadableByWpfIconBitmapDecoder()
    {
        using var stream = File.OpenRead(GetAssetPath("icon.ico"));
        var decoder = new IconBitmapDecoder(
            stream,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var sizes = decoder.Frames
            .Select(f => f.PixelWidth)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
    }

    // ---- 3. PNG 均有 Alpha 通道、透明背景、内容非空白 ----

    [Theory]
    [InlineData("icon.png", 512)]
    [InlineData("icon_16x16.png", 16)]
    [InlineData("icon_24x24.png", 24)]
    [InlineData("icon_32x32.png", 32)]
    [InlineData("icon_48x48.png", 48)]
    [InlineData("icon_64x64.png", 64)]
    [InlineData("icon_128x128.png", 128)]
    [InlineData("icon_256x256.png", 256)]
    public void PngAssets_HaveAlphaTransparentBackground_AndNonBlankCenter(
        string fileName,
        int expectedSize)
    {
        using var bitmap = new Bitmap(GetAssetPath(fileName));

        Assert.Equal(expectedSize, bitmap.Width);
        Assert.Equal(expectedSize, bitmap.Height);
        Assert.True(
            (bitmap.PixelFormat & PixelFormat.Alpha) != 0,
            $"{fileName} 应带 Alpha 通道");

        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(expectedSize - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, expectedSize - 1).A);
        Assert.Equal(0, bitmap.GetPixel(expectedSize - 1, expectedSize - 1).A);

        // 中心应有内容（电源竖线区域），排除空白图标
        var center = bitmap.GetPixel(expectedSize / 2, expectedSize / 2);
        Assert.True(center.A > 128, $"{fileName} 中心应为不透明内容");
    }

    // ---- 4. 主窗口与提醒窗口接入同一图标资源 ----

    [Fact]
    public void MainAndReminderWindows_UseSameIconResource()
    {
        var main = ReadAppFile("MainWindow.xaml");
        var reminder = ReadAppFile("ReminderWindow.xaml");

        Assert.Contains("Icon=\"/assets/icon.ico\"", main);
        Assert.Contains("Icon=\"/assets/icon.ico\"", reminder);
    }

    // ---- 5. 托盘使用正式图标而非系统占位图，且生命周期有效 ----

    [Fact]
    public void TrayIconService_UsesEmbeddedIcon_NotSystemPlaceholder()
    {
        var source = ReadAppFile("Infrastructure", "TrayIconService.cs");

        Assert.Contains("pack://application:,,,/assets/icon.ico", source);
        Assert.Contains("new Icon(stream)", source);
        Assert.DoesNotContain("Icon = SystemIcons.Application", source);
    }

    [Fact]
    public void TrayIconService_KeepsAndDisposesIconLifecycle()
    {
        var source = ReadAppFile("Infrastructure", "TrayIconService.cs");

        Assert.Contains("Icon? _icon", source);
        Assert.Contains("_icon = icon", source);
        Assert.Contains("icon?.Dispose()", source);
        Assert.Contains("notifyIcon.Dispose()", source);
    }

    // ---- 6. 已构建 App.exe 内嵌图标资源（RT_GROUP_ICON） ----

    [Fact]
    public void BuiltAppExe_EmbedsGroupIconResource()
    {
        var exePath = GetBuiltAppPath("AutoShutdown.App.exe");
        Assert.True(File.Exists(exePath), $"未找到构建产物，请先构建：{exePath}");

        var (groupIconCount, frameCount) = ReadGroupIconInfo(exePath);

        Assert.True(groupIconCount > 0, "EXE 应包含 RT_GROUP_ICON 资源");
        Assert.Equal(7, frameCount);
    }

    // ---- 7. 已构建 App.dll 的 .g.resources 包含图标资源 ----

    [Fact]
    public void BuiltAppDll_GResourcesContainIcon()
    {
        var assembly = typeof(AutoShutdown.App.App).Assembly;
        using var stream = assembly.GetManifestResourceStream("AutoShutdown.App.g.resources");
        Assert.NotNull(stream);

        var names = new List<string>();
        using (var reader = new ResourceReader(stream))
        {
            foreach (System.Collections.DictionaryEntry entry in reader)
            {
                names.Add((string)entry.Key);
            }
        }

        Assert.Contains("assets/icon.ico", names);
    }

    // ---- 8. IPowerService 通过 GuardedPowerService 注册；无真实电源 API ----

    [Fact]
    public void IPowerService_RegisteredAsGuardedPowerService()
    {
        var source = ReadAppFile("AppHost", "ServiceRegistration.cs");

        Assert.Contains("AddSingleton<FakePowerService>", source);
        Assert.Contains("new GuardedPowerService(", source);
        Assert.DoesNotContain("AddSingleton<IPowerService, FakePowerService>", source);
    }

    [Fact]
    public void WholeSource_PowerDllImportsOnlyInWin32PowerNativeApi()
    {
        foreach (var file in EnumerateWholeSourceFiles())
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);

            if (name == "Win32PowerNativeApi.cs")
            {
                Assert.Contains("DllImport", content);
            }
            else
            {
                Assert.DoesNotContain("DllImport", content);
                Assert.DoesNotContain("ExitWindowsEx", content);
                Assert.DoesNotContain("SetSuspendState", content);
            }

            Assert.DoesNotContain("shutdown.exe", content);
            if (name is "OfficeSaveHelperLauncher.cs" or "ShellOpenService.cs")
            {
                Assert.Contains("Process.Start", content);
            }
            else
            {
                Assert.DoesNotContain("Process.Start", content);
            }
        }
    }

    // ---- Helpers ----

    private static string GetAssetPath(string fileName)
        => Path.Combine(FindProjectRoot(), "assets", fileName);

    private static string ReadProjectFile(params string[] relativeParts)
    {
        var parts = new[] { FindProjectRoot() }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    private static string GetBuiltAppPath(string fileName)
    {
        var config = FindTestConfiguration();
        return Path.Combine(
            FindProjectRoot(),
            "src",
            "AutoShutdown.App",
            "bin",
            config,
            "net8.0-windows",
            fileName);
    }

    private static string FindTestConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Parent is not null
                && current.Parent.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                return current.Name;
            }
        }

        throw new DirectoryNotFoundException("未能从测试输出目录定位构建配置。");
    }

    private static IEnumerable<string> EnumerateWholeSourceFiles()
    {
        var appRoot = FindAppSourceRoot();
        var coreRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "AutoShutdown.Core"));
        return Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(
                Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                && !file.Contains(
                    Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown project root was not found.");
    }

    private static string FindAppSourceRoot()
        => Path.Combine(FindProjectRoot(), "src", "AutoShutdown.App");

    /// <summary>
    /// 解析 PE 资源目录，返回 (RT_GROUP_ICON 资源数, 组内图标帧数)。
    /// 仅读取字节，不加载映像。
    /// </summary>
    private static (int GroupIconCount, int FrameCount) ReadGroupIconInfo(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidOperationException("不是有效的 PE 文件：" + exePath);
        }

        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        if (peOffset + 4 >= bytes.Length
            || bytes[peOffset] != (byte)'P'
            || bytes[peOffset + 1] != (byte)'E')
        {
            throw new InvalidOperationException("PE 签名无效：" + exePath);
        }

        var coff = peOffset + 4;
        var sectionCount = BitConverter.ToUInt16(bytes, coff + 2);
        var optionalSize = BitConverter.ToUInt16(bytes, coff + 16);
        var optional = coff + 20;
        var magic = BitConverter.ToUInt16(bytes, optional);

        // 数据目录起点：PE32+ 为 optional+112，PE32 为 optional+96
        var dataDirectories = magic == 0x20B ? optional + 112 : optional + 96;
        var resourceRva = BitConverter.ToUInt32(bytes, dataDirectories + 2 * 8);
        if (resourceRva == 0)
        {
            return (0, 0);
        }

        var sectionTable = optional + optionalSize;
        uint RvaToFileOffset(uint rva)
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var section = sectionTable + i * 40;
                var virtualAddress = BitConverter.ToUInt32(bytes, section + 12);
                var virtualSize = BitConverter.ToUInt32(bytes, section + 8);
                var rawSize = BitConverter.ToUInt32(bytes, section + 16);
                var rawPointer = BitConverter.ToUInt32(bytes, section + 20);
                var end = Math.Max(virtualSize, rawSize);
                if (rva >= virtualAddress && rva < virtualAddress + end)
                {
                    return rawPointer + (rva - virtualAddress);
                }
            }

            throw new InvalidOperationException("RVA 无法映射到文件偏移：" + rva);
        }

        // 资源目录条目：Name/Id(4) + OffsetToData(4，高位为子目录标志，子目录偏移相对资源节)
        uint FindEntry(uint directoryOffset, uint targetId)
        {
            var namedCount = BitConverter.ToUInt16(bytes, (int)directoryOffset + 12);
            var idCount = BitConverter.ToUInt16(bytes, (int)directoryOffset + 14);
            for (var i = namedCount; i < namedCount + idCount; i++)
            {
                var entry = directoryOffset + 16u + (uint)(i * 8);
                var id = BitConverter.ToUInt32(bytes, (int)entry);
                if ((id & 0x80000000) != 0)
                {
                    continue; // 跳过命名条目
                }

                if (id == targetId)
                {
                    return entry;
                }
            }

            return 0;
        }

        // 取目录中第一个条目（忽略名称/语言），返回其 OffsetToData
        uint FirstEntryData(uint directoryOffset)
        {
            var namedCount = BitConverter.ToUInt16(bytes, (int)directoryOffset + 12);
            var idCount = BitConverter.ToUInt16(bytes, (int)directoryOffset + 14);
            if (namedCount + idCount == 0)
            {
                return 0;
            }

            return BitConverter.ToUInt32(bytes, (int)directoryOffset + 16 + 4);
        }

        var root = RvaToFileOffset(resourceRva);

        // 类型 14 = RT_GROUP_ICON
        var typeEntry = FindEntry(root, 14);
        if (typeEntry == 0)
        {
            return (0, 0);
        }

        var typeData = BitConverter.ToUInt32(bytes, (int)typeEntry + 4);
        var groupDir = (typeData & 0x80000000) != 0
            ? RvaToFileOffset(resourceRva + (typeData & 0x7FFFFFFF))
            : 0;
        if (groupDir == 0)
        {
            return (0, 0);
        }

        // 组名子目录 → 语言子目录 → 数据条目
        var nameData = FirstEntryData(groupDir);
        if ((nameData & 0x80000000) == 0)
        {
            return (0, 0);
        }

        var languageDir = RvaToFileOffset(resourceRva + (nameData & 0x7FFFFFFF));
        var leafData = FirstEntryData(languageDir);
        if ((leafData & 0x80000000) != 0)
        {
            return (0, 0);
        }

        // 本构建链（alink）将叶子数据条目的 OffsetToData 编码为相对资源节基址的偏移
        var dataEntryOffset = RvaToFileOffset(resourceRva + leafData);
        var blobRva = BitConverter.ToUInt32(bytes, (int)dataEntryOffset);

        // ICONDIR 校验：reserved=0, type=1
        var blobOffset = (int)RvaToFileOffset(blobRva);
        var reserved = BitConverter.ToUInt16(bytes, blobOffset);
        var type = BitConverter.ToUInt16(bytes, blobOffset + 2);
        if (reserved != 0 || type != 1)
        {
            return (0, 0);
        }

        var frameCount = BitConverter.ToUInt16(bytes, blobOffset + 4);
        return (1, frameCount);
    }
}
