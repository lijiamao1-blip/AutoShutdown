using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;

namespace AutoShutdown.App.Infrastructure.Remote;

/// <summary>
/// S23 TLS 服务器证书服务。两种模式（remote-settings.json）：
/// <list type="bullet">
/// <item>自签名自动生成（UseImportedCertificate=false）：首次生成 RSA-2048 自签名证书
/// （有效期 1 年），PFX 经 <see cref="ISecretProtector"/>（DPAPI）封装后落盘，绝不明文写私钥；
/// 内存缓存，重启后从磁盘解密恢复。</item>
/// <item>导入证书（UseImportedCertificate=true）：从 ImportedCertPath 加载 PFX；若带密码，
/// 密码须由本地 UI 经 DPAPI 保护的密码文件提供（本服务绝不读明文密码）。</item>
/// </list>
/// 任何加载/校验失败抛异常（fail-closed）：调用方（RemoteServer）在强制 TLS 时拒绝启动监听，
/// 绝不以「无证书/坏证书」状态继续服务。私钥经 <see cref="X509KeyStorageFlags.DefaultKeySet"/>
/// 加载（详见 <see cref="LoadPfx(byte[], string?)"/>：本平台 SChannel 拒绝 EphemeralKeySet，
/// 而 DefaultKeySet 实测不落 Windows 密钥库残留）。私钥唯一持久形态是 DPAPI 封装的 PFX。
///
/// 自签名 SAN 覆盖（S-REMOTE-D1）：SAN 必须覆盖客户端实际连入的端点，否则任何按规范校验
/// 主机名的 TLS 客户端都会握手失败——而「让客户端跳过证书校验」等于放弃 TLS 的身份保证。
/// 因此 SAN 按 <see cref="RemoteSettingsDocument.ListenAddress"/> 推导：
/// <list type="bullet">
/// <item>监听具体地址 → 覆盖 localhost / 回环 / 该地址；</item>
/// <item>监听 0.0.0.0（或地址不可解析）→ 覆盖 localhost / 回环 / 本机主机名 / 当前全部活动
/// IPv4 地址（无法预知客户端会用哪个本机地址连入）。</item>
/// </list>
/// 每次取证书都复核已有证书的 SAN 是否覆盖当前所需端点；不覆盖（监听地址被改、DHCP 换址等）
/// 即重新生成并覆盖落盘文件——这会更换服务器证书指纹，已固定旧指纹的客户端需要重新信任。
/// </summary>
public sealed class RemoteCertificateService
{
    private const int SelfSignedKeyBits = 2048;

    /// <summary>subjectAltName 扩展 OID（RFC 5280）。</summary>
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    private readonly ISecretProtector _protector;
    private readonly IClock _clock;
    private readonly string _certificateFilePath;
    private readonly string _importedPasswordFilePath;
    private readonly object _sync = new();

    // 生成/落盘临界区。原实现用 lock 只保护缓存字段，两个并发调用可能各自生成一份不同的
    // 自签名证书并互相覆盖落盘文件（后到者的证书与磁盘不一致）。改为信号量后，
    // 「读盘 → 校验覆盖 → 生成 → 落盘 → 发布缓存」整体串行化。
    private readonly SemaphoreSlim _selfSignedGate = new(1, 1);

    private X509Certificate2? _selfSignedCache;

    public RemoteCertificateService(
        ISecretProtector protector,
        IClock clock,
        string certificateFilePath,
        string importedPasswordFilePath)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateFilePath);

        _protector = protector;
        _clock = clock;
        _certificateFilePath = certificateFilePath;
        _importedPasswordFilePath = importedPasswordFilePath;
    }

    public string CertificateFilePath => _certificateFilePath;

    /// <summary>
    /// 返回用于 TLS 服务器认证的证书（含私钥）。按设置选择导入或自签名；
    /// 证书过期/无私钥/解密失败/导入文件缺失一律抛异常（fail-closed）。
    /// </summary>
    public async Task<X509Certificate2> GetServerCertificateAsync(
        RemoteSettingsDocument settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.UseImportedCertificate
            ? await LoadImportedAsync(settings.ImportedCertPath, cancellationToken).ConfigureAwait(false)
            : await GetOrCreateSelfSignedAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<X509Certificate2> LoadImportedAsync(
        string? importedPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(importedPath) || !File.Exists(importedPath))
        {
            throw new InvalidOperationException(
                "The imported certificate file does not exist at '" + importedPath + "'.");
        }

        byte[] pfx = await File.ReadAllBytesAsync(importedPath, cancellationToken).ConfigureAwait(false);

        try
        {
            return LoadPfx(pfx, password: null);
        }
        catch (CryptographicException)
        {
            // PFX 带密码：需要本地 UI 预先写入的 DPAPI 保护密码。
        }

        var password = await ReadImportedPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "The imported certificate requires a password that is not available; set it in the local UI first.");
        }

        return LoadPfx(pfx, password);
    }

    private async Task<X509Certificate2> GetOrCreateSelfSignedAsync(
        RemoteSettingsDocument settings,
        CancellationToken cancellationToken)
    {
        var required = RequiredSanAddresses(settings);

        // 快路径：已缓存且 SAN 覆盖当前所需端点，直接复用（每条 TLS 连接都会走到这里）。
        X509Certificate2? cached;
        lock (_sync)
        {
            cached = _selfSignedCache;
        }

        if (cached is not null && CoversAddresses(cached, required))
        {
            return cached;
        }

        await _selfSignedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                cached = _selfSignedCache;
            }

            if (cached is not null && CoversAddresses(cached, required))
            {
                return cached;
            }

            X509Certificate2? certificate = null;

            if (File.Exists(_certificateFilePath))
            {
                byte[] protectedBytes = await File.ReadAllBytesAsync(_certificateFilePath, cancellationToken)
                    .ConfigureAwait(false);

                byte[] storedPfx;
                try
                {
                    storedPfx = _protector.Unprotect(protectedBytes);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "The stored server certificate could not be decrypted.", exception);
                }

                certificate = LoadPfx(storedPfx, password: string.Empty);

                if (!CoversAddresses(certificate, required))
                {
                    // 已落盘证书的 SAN 覆盖不到当前监听端点（监听地址被改、DHCP 换址等）：
                    // 继续使用只会让客户端主机名校验必然失败，因此丢弃并重新生成。
                    certificate.Dispose();
                    certificate = null;
                }
            }

            if (certificate is null)
            {
                var generatedPfx = GenerateSelfSignedPfx(required);
                await File.WriteAllBytesAsync(
                        _certificateFilePath,
                        _protector.Protect(generatedPfx),
                        cancellationToken)
                    .ConfigureAwait(false);
                certificate = LoadPfx(generatedPfx, password: string.Empty);
            }

            lock (_sync)
            {
                // 旧缓存证书不在此处释放：可能仍被在途 TLS 连接使用，释放会直接打断这些连接。
                _selfSignedCache = certificate;
            }

            return certificate;
        }
        finally
        {
            _selfSignedGate.Release();
        }
    }

    /// <summary>
    /// 当前配置下 SAN 必须覆盖的 IP 集合。监听具体地址时只需覆盖该地址与回环；
    /// 监听 0.0.0.0 / 地址不可解析时，客户端可能从任一本机地址连入，故全部活动 IPv4 都必须覆盖。
    /// </summary>
    private static IReadOnlyCollection<IPAddress> RequiredSanAddresses(RemoteSettingsDocument settings)
    {
        var required = new HashSet<IPAddress> { IPAddress.Loopback };

        if (IPAddress.TryParse(settings.ListenAddress, out var listenAddress)
            && !listenAddress.Equals(IPAddress.Any)
            && !listenAddress.Equals(IPAddress.IPv6Any))
        {
            required.Add(listenAddress);
            return required;
        }

        foreach (var address in EnumerateLocalIpv4())
        {
            required.Add(address);
        }

        return required;
    }

    /// <summary>
    /// 本机当前活动的非回环 IPv4 地址。仅读取本机网络配置（托管
    /// <c>System.Net.NetworkInformation</c>），不扫描、不自动发现、不访问公网，无 P/Invoke。
    /// 枚举失败返回空清单（只保证回环覆盖，绝不假装覆盖了未知地址）。
    /// </summary>
    private static IReadOnlyList<IPAddress> EnumerateLocalIpv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>本机主机名（用于 DNS SAN）。读取本地配置，不做名称解析查询。</summary>
    private static IReadOnlyList<string> EnumerateLocalDnsNames()
    {
        try
        {
            var hostName = Dns.GetHostName();
            if (string.IsNullOrWhiteSpace(hostName)
                || string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            return new[] { hostName };
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>证书 SAN 是否覆盖全部所需 IP。SAN 缺失/无法解析一律判为不覆盖（fail-closed → 重新生成）。</summary>
    private static bool CoversAddresses(
        X509Certificate2 certificate,
        IReadOnlyCollection<IPAddress> required)
    {
        if (required.Count == 0)
        {
            return true;
        }

        var present = ReadSanIpAddresses(certificate);
        return required.All(present.Contains);
    }

    private static HashSet<IPAddress> ReadSanIpAddresses(X509Certificate2 certificate)
    {
        var present = new HashSet<IPAddress>();

        foreach (var extension in certificate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, SubjectAlternativeNameOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                foreach (var address in san.EnumerateIPAddresses())
                {
                    present.Add(address);
                }
            }
            catch (CryptographicException)
            {
                // SAN 编码无法解析：不计入已覆盖集合（fail-closed）。
            }
        }

        return present;
    }

    private byte[] GenerateSelfSignedPfx(IReadOnlyCollection<IPAddress> requiredAddresses)
    {
        var now = _clock.UtcNow;
        using var rsa = RSA.Create(SelfSignedKeyBits);
        var request = new CertificateRequest(
            "CN=AutoShutdown Remote Server",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");

        foreach (var hostName in EnumerateLocalDnsNames())
        {
            try
            {
                san.AddDnsName(hostName);
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                // 主机名不适合作为 DNS SAN（中文计算机名等非 ASCII 名称在此会被拒绝）：
                // 跳过该 DNS 条目即可，局域网客户端按 IP 连入，IP SAN 覆盖不受影响。
            }
        }

        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        foreach (var address in requiredAddresses)
        {
            addresses.Add(address);
        }

        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());

        using var selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        // 空密码 PFX 本身即含私钥材料，因此整段 PFX 必须经 DPAPI 封装后才可落盘。
        return selfSigned.Export(X509ContentType.Pfx, string.Empty);
    }

    private X509Certificate2 LoadPfx(byte[] pfx, string? password)
    {
        // 私钥加载标志选择（本平台实测，见 S23 结果记录「证书私钥存储」）：
        // - X509KeyStorageFlags.EphemeralKeySet（含叠加 Exportable）会被本平台 SChannel 以
        //   "platform does not support ephemeral keys" 拒绝，无法用于 TLS 服务端认证（多进程实测）。
        // - DefaultKeySet（默认）可正常用于 TLS 服务端认证，且实测不向 Windows 用户密钥库
        //   （%AppData%\Microsoft\Crypto\RSA|\Keys）写入任何持久化私钥残留。
        // 因此选择 DefaultKeySet：私钥唯一持久形态是经 DPAPI 封装的 PFX 落盘（绝非明文），
        // 内存中的私钥在证书对象释放后由系统回收。
        var certificate = new X509Certificate2(
            pfx,
            password,
            X509KeyStorageFlags.DefaultKeySet);

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The server certificate has no private key.");
        }

        var now = _clock.UtcNow;
        if (certificate.NotAfter <= now)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The server certificate is expired.");
        }

        if (certificate.NotBefore > now)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The server certificate is not yet valid.");
        }

        return certificate;
    }

    private async Task<string?> ReadImportedPasswordAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_importedPasswordFilePath))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(_importedPasswordFilePath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var password = _protector.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(password);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
