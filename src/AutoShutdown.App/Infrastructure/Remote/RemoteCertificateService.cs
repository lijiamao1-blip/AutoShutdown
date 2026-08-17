using System.IO;
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
/// （SAN: localhost + 127.0.0.1，有效期 1 年），PFX 经 <see cref="ISecretProtector"/>（DPAPI）
/// 封装后落盘，绝不明文写私钥；内存缓存，重启后从磁盘解密恢复。</item>
/// <item>导入证书（UseImportedCertificate=true）：从 ImportedCertPath 加载 PFX；若带密码，
/// 密码须由本地 UI 经 DPAPI 保护的密码文件提供（本服务绝不读明文密码）。</item>
/// </list>
/// 任何加载/校验失败抛异常（fail-closed）：调用方（RemoteServer）在强制 TLS 时拒绝启动监听，
/// 绝不以「无证书/坏证书」状态继续服务。私钥经 <see cref="X509KeyStorageFlags.DefaultKeySet"/>
/// 加载（详见 <see cref="LoadPfx(byte[], string?)"/>：本平台 SChannel 拒绝 EphemeralKeySet，
/// 而 DefaultKeySet 实测不落 Windows 密钥库残留）。私钥唯一持久形态是 DPAPI 封装的 PFX。
/// </summary>
public sealed class RemoteCertificateService
{
    private const int SelfSignedKeyBits = 2048;

    private readonly ISecretProtector _protector;
    private readonly IClock _clock;
    private readonly string _certificateFilePath;
    private readonly string _importedPasswordFilePath;
    private readonly object _sync = new();

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
            : await GetOrCreateSelfSignedAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<X509Certificate2> GetOrCreateSelfSignedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_selfSignedCache is not null)
            {
                return _selfSignedCache;
            }
        }

        byte[] pfx;
        if (File.Exists(_certificateFilePath))
        {
            byte[] protectedBytes = await File.ReadAllBytesAsync(_certificateFilePath, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                pfx = _protector.Unprotect(protectedBytes);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The stored server certificate could not be decrypted.", exception);
            }
        }
        else
        {
            pfx = GenerateSelfSignedPfx();
            await File.WriteAllBytesAsync(_certificateFilePath, _protector.Protect(pfx), cancellationToken)
                .ConfigureAwait(false);
        }

        var certificate = LoadPfx(pfx, password: string.Empty);
        lock (_sync)
        {
            _selfSignedCache ??= certificate;
        }

        return certificate;
    }

    private byte[] GenerateSelfSignedPfx()
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
        san.AddIpAddress(System.Net.IPAddress.Loopback);
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
