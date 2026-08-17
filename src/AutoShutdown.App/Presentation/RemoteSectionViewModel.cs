using System.Net;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure.Remote;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 局域网远程控制分区（S23 CP5 设置页）。集中管理：
/// <list type="bullet">
/// <item>远程开关/监听地址/端口/TLS 要求/客户端证书/证书模式/远程命令白名单（独立于 S19
/// 本地白名单，默认仅只读查询）；修改采用「编辑 → 保存并应用」显式确认，绝不即时生效。</item>
/// <item>配对 PIN 展示与轮换、失败锁定状态与本地解锁、已配对设备清单与移除。</item>
/// <item>服务器运行状态与保存后重启（地址/端口/TLS 变更需重启监听才生效）。</item>
/// </list>
/// 读取 remote-settings.json 严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion：损坏/非法
/// 一律保持关闭并上报（fail-closed，绝不静默启用监听）。保存前做字段校验，失败不落盘不重启。
/// 所有远程命令仍只经本地调度引擎唯一接入/仲裁路径；本分区不提供任何第二个电源出口。
/// </summary>

/// <summary>已配对设备展示行（绝不暴露受保护 secret 字段）。</summary>
public sealed record RemoteDeviceRow(string DeviceId, string DeviceName, string PairedAtText);

public sealed class RemoteSectionViewModel : ObservableObject
{
    private readonly RemoteSettingsStore _settingsStore;
    private readonly PairingService _pairing;
    private readonly IRemoteServerControl _server;
    private readonly IClock _clock;
    private readonly Action<string>? _log;

    // 配置（编辑暂存，保存才落盘生效）。
    private bool _enabled;
    private string _listenAddress = RemoteSettingsDocumentDefaults.ListenAddress;
    private string _listenPortText = RemoteSettingsDocumentDefaults.ListenPort.ToString();
    private bool _requireTls = true;
    private bool _requireClientCertificate;
    private bool _useImportedCertificate;
    private string _importedCertPath = string.Empty;
    private bool _whitelistQueryStatus = true;
    private bool _whitelistListTasks = true;
    private bool _whitelistTriggerShutdown;
    private bool _whitelistCancelShutdown;

    // 配对/锁定/设备。
    private string _pinDisplay = "未生成（点“轮换 PIN”生成）";
    private string _pinExpiryText = string.Empty;
    private string _lockStatusText = "未锁定";
    private bool _isLocked;
    private IReadOnlyList<RemoteDeviceRow> _devices = [];

    // 状态。
    private string _serverStatusText = "未监听";
    private string _statusText = "已关闭";
    private string _detailText = string.Empty;
    private string _errorText = string.Empty;
    private bool _isBusy;
    private bool _isSaving;

    public RemoteSectionViewModel(
        RemoteSettingsStore settingsStore,
        PairingService pairing,
        IRemoteServerControl server,
        IClock clock,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(clock);

        _settingsStore = settingsStore;
        _pairing = pairing;
        _server = server;
        _clock = clock;
        _log = log;

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, () => CanOperate);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            () => !_isBusy);
        RotatePinCommand = new RelayCommand(ExecuteRotatePin, () => CanOperate);
        UnlockCommand = new AsyncRelayCommand(ExecuteUnlockAsync, () => CanOperate && _isLocked);
        RemoveDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is RemoteDeviceRow device)
            {
                _ = ExecuteRemoveDeviceAsync(device);
            }
        });
        _ = RefreshAsync(CancellationToken.None);
    }

    // ===== 配置编辑（暂存） =====

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string ListenAddress
    {
        get => _listenAddress;
        set => SetProperty(ref _listenAddress, value);
    }

    public string ListenPortText
    {
        get => _listenPortText;
        set => SetProperty(ref _listenPortText, value);
    }

    public bool RequireTls
    {
        get => _requireTls;
        set => SetProperty(ref _requireTls, value);
    }

    public bool RequireClientCertificate
    {
        get => _requireClientCertificate;
        set => SetProperty(ref _requireClientCertificate, value);
    }

    public bool UseImportedCertificate
    {
        get => _useImportedCertificate;
        set => SetProperty(ref _useImportedCertificate, value);
    }

    public string ImportedCertPath
    {
        get => _importedCertPath;
        set => SetProperty(ref _importedCertPath, value);
    }

    public bool WhitelistQueryStatus
    {
        get => _whitelistQueryStatus;
        set => SetProperty(ref _whitelistQueryStatus, value);
    }

    public bool WhitelistListTasks
    {
        get => _whitelistListTasks;
        set => SetProperty(ref _whitelistListTasks, value);
    }

    public bool WhitelistTriggerShutdown
    {
        get => _whitelistTriggerShutdown;
        set => SetProperty(ref _whitelistTriggerShutdown, value);
    }

    public bool WhitelistCancelShutdown
    {
        get => _whitelistCancelShutdown;
        set => SetProperty(ref _whitelistCancelShutdown, value);
    }

    // ===== 配对/锁定/设备 =====

    public string PinDisplay
    {
        get => _pinDisplay;
        private set => SetProperty(ref _pinDisplay, value);
    }

    public string PinExpiryText
    {
        get => _pinExpiryText;
        private set => SetProperty(ref _pinExpiryText, value);
    }

    public string LockStatusText
    {
        get => _lockStatusText;
        private set => SetProperty(ref _lockStatusText, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (SetProperty(ref _isLocked, value))
            {
                UnlockCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>已配对设备清单（展示行；不含任何 secret 字段）。</summary>
    public IReadOnlyList<RemoteDeviceRow> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    public string DeviceCountText
    {
        get
        {
            var count = _devices.Count;
            return count == 0 ? "暂无已配对设备" : $"已配对设备 {count} 台";
        }
    }

    // ===== 状态 =====

    public string ServerStatusText
    {
        get => _serverStatusText;
        private set => SetProperty(ref _serverStatusText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanOperate));
                SaveCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOperate => !_isBusy && !_isSaving;

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand RotatePinCommand { get; }

    public AsyncRelayCommand UnlockCommand { get; }

    public RelayCommand RemoveDeviceCommand { get; }

    /// <summary>
    /// 从 remote-settings.json 恢复分区状态。NotFound 视为首次使用（关闭）；损坏/非法/版本
    /// 过高一律保持关闭并上报（fail-closed，绝不静默启用监听）。随后刷新配对/锁定/设备与
    /// 服务器运行状态；所有分区内错误在分区内捕获，绝不抛出。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorText = string.Empty;
        StatusText = string.Empty;
        DetailText = string.Empty;
        try
        {
            var load = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            switch (load.Status)
            {
                case RemoteSettingsLoadStatus.Success when load.Document is not null:
                    ApplyDocument(load.Document);
                    StatusText = load.Document.Enabled ? "远程控制已启用" : "远程控制已关闭";
                    break;

                case RemoteSettingsLoadStatus.NotFound:
                    ResetToDefaults();
                    StatusText = "远程控制已关闭（首次使用，默认安全）";
                    break;

                default:
                    ResetToDefaults();
                    ErrorText = "远程控制配置不可用（" + load.Status + "）。已保持关闭，绝不自动启用。请修正配置或覆盖保存。";
                    break;
            }
        }
        catch (Exception exception)
        {
            ResetToDefaults();
            ErrorText = "读取远程控制设置失败：" + exception.Message;
        }

        await RefreshPairingAndServerAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 保存并应用：字段校验 → 持久化 remote-settings.json → 重启监听（地址/端口/TLS 变更
    /// 需重启才生效）。任一失败不落盘不重启；启用但启动失败如实显示（fail-closed 不假装成功）。
    /// </summary>
    private async Task ExecuteSaveAsync()
    {
        if (!CanOperate)
        {
            return;
        }

        IsBusy = true;
        _isSaving = true;
        ErrorText = string.Empty;
        try
        {
            if (!TryBuildDocument(out var document, out var fieldError))
            {
                ErrorText = fieldError;
                StatusText = string.Empty;
                return;
            }

            var save = await _settingsStore.SaveAsync(document!, CancellationToken.None).ConfigureAwait(true);
            if (!save.Succeeded)
            {
                ErrorText = "保存远程控制设置失败：" + (save.Error ?? string.Empty);
                StatusText = string.Empty;
                return;
            }

            _log?.Invoke("远程控制设置已保存。" + (document!.Enabled ? "正在重启监听以应用。" : "已停止监听。"));

            // 重启监听：地址/端口/TLS 在启动时固定，必须 Stop→Start 才生效。
            await _server.StopAsync().ConfigureAwait(true);
            if (document!.Enabled)
            {
                await _server.StartAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await RefreshPairingAndServerAsync(CancellationToken.None).ConfigureAwait(true);

            if (document.Enabled)
            {
                StatusText = _server.IsRunning
                    ? "远程控制已启用并监听 " + document.ListenAddress + ":" + document.ListenPort
                    : "已启用，但监听未启动（端口被占用或配置无效，fail-closed 不监听）。";
                DetailText = BuildDetailText(document);
            }
            else
            {
                StatusText = "远程控制已关闭，监听已停止。";
                DetailText = string.Empty;
            }
        }
        catch (Exception exception)
        {
            ErrorText = "应用远程控制设置失败：" + exception.Message;
            StatusText = string.Empty;
        }
        finally
        {
            _isSaving = false;
            IsBusy = false;
        }
    }

    private void ExecuteRotatePin()
    {
        if (!CanOperate)
        {
            return;
        }

        try
        {
            var state = _pairing.RotatePin();
            UpdatePinDisplay(state);
            ErrorText = string.Empty;
            StatusText = "已生成新 PIN（有效期 10 分钟，一 PIN 一配，配对成功即失效）。";
            _log?.Invoke("已轮换远程配对 PIN。");
        }
        catch (Exception exception)
        {
            ErrorText = "轮换 PIN 失败：" + exception.Message;
        }
    }

    private async Task ExecuteUnlockAsync()
    {
        if (!CanOperate || !_isLocked)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var succeeded = await _pairing.UnlockAsync(CancellationToken.None).ConfigureAwait(true);
            if (succeeded)
            {
                await RefreshPairingAndServerAsync(CancellationToken.None).ConfigureAwait(true);
                ErrorText = string.Empty;
                StatusText = "配对锁定已解除。";
                _log?.Invoke("本地用户解除远程配对锁定。");
            }
            else
            {
                ErrorText = "解除锁定失败（写入锁定状态失败）。";
            }
        }
        catch (Exception exception)
        {
            ErrorText = "解除锁定异常：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteRemoveDeviceAsync(RemoteDeviceRow device)
    {
        if (!CanOperate)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var succeeded = await _pairing.RemoveDeviceAsync(device.DeviceId, CancellationToken.None).ConfigureAwait(true);
            if (succeeded)
            {
                await RefreshPairingAndServerAsync(CancellationToken.None).ConfigureAwait(true);
                ErrorText = string.Empty;
                StatusText = "已移除设备「" + (string.IsNullOrEmpty(device.DeviceName) ? device.DeviceId : device.DeviceName) + "」。";
                _log?.Invoke("已移除远程配对设备 " + device.DeviceId + "。");
            }
            else
            {
                ErrorText = "移除设备失败（设备存储不可用）。";
            }
        }
        catch (Exception exception)
        {
            ErrorText = "移除设备异常：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===== 内部 =====

    private void ApplyDocument(RemoteSettingsDocument document)
    {
        _enabled = document.Enabled;
        _listenAddress = document.ListenAddress;
        _listenPortText = document.ListenPort.ToString();
        _requireTls = document.RequireTls;
        _requireClientCertificate = document.RequireClientCertificate;
        _useImportedCertificate = document.UseImportedCertificate;
        _importedCertPath = document.ImportedCertPath ?? string.Empty;
        _whitelistQueryStatus = document.WhiteList.QueryStatus;
        _whitelistListTasks = document.WhiteList.ListTasks;
        _whitelistTriggerShutdown = document.WhiteList.TriggerShutdown;
        _whitelistCancelShutdown = document.WhiteList.CancelShutdown;

        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ListenAddress));
        OnPropertyChanged(nameof(ListenPortText));
        OnPropertyChanged(nameof(RequireTls));
        OnPropertyChanged(nameof(RequireClientCertificate));
        OnPropertyChanged(nameof(UseImportedCertificate));
        OnPropertyChanged(nameof(ImportedCertPath));
        OnPropertyChanged(nameof(WhitelistQueryStatus));
        OnPropertyChanged(nameof(WhitelistListTasks));
        OnPropertyChanged(nameof(WhitelistTriggerShutdown));
        OnPropertyChanged(nameof(WhitelistCancelShutdown));
        OnPropertyChanged(nameof(CanOperate));
    }

    private void ResetToDefaults()
    {
        ApplyDocument(new RemoteSettingsDocument());
    }

    private bool TryBuildDocument(out RemoteSettingsDocument? document, out string fieldError)
    {
        document = null;
        fieldError = string.Empty;

        var address = (ListenAddress ?? string.Empty).Trim();
        if (!IPAddress.TryParse(address, out _))
        {
            fieldError = "监听地址必须是合法的 IP 地址（如 127.0.0.1 或 0.0.0.0）。";
            return false;
        }

        if (!int.TryParse(ListenPortText?.Trim(), out var port) || port is < 1 or > 65535)
        {
            fieldError = "监听端口必须是 1~65535 之间的整数。";
            return false;
        }

        if (UseImportedCertificate && string.IsNullOrWhiteSpace(ImportedCertPath))
        {
            fieldError = "启用导入证书时须填写 PFX 证书路径。";
            return false;
        }

        document = new RemoteSettingsDocument
        {
            Enabled = Enabled,
            ListenAddress = address,
            ListenPort = port,
            RequireTls = RequireTls,
            RequireClientCertificate = RequireClientCertificate,
            UseImportedCertificate = UseImportedCertificate,
            ImportedCertPath = UseImportedCertificate ? ImportedCertPath?.Trim() : null,
            WhiteList = new RemoteCommandWhiteList
            {
                QueryStatus = WhitelistQueryStatus,
                ListTasks = WhitelistListTasks,
                TriggerShutdown = WhitelistTriggerShutdown,
                CancelShutdown = WhitelistCancelShutdown
            }
        };

        var validation = RemoteSettingsStore.Validate(document);
        if (validation.Count > 0)
        {
            fieldError = string.Join(" ", validation);
            return false;
        }

        return true;
    }

    private async Task RefreshPairingAndServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = _pairing.CurrentPin;
            UpdatePinDisplay(state);

            var lockState = await _pairing.GetLockStateAsync(cancellationToken).ConfigureAwait(true);
            IsLocked = lockState.IsLocked;
            LockStatusText = BuildLockText(lockState);

            var devices = await _pairing.GetDevicesAsync(cancellationToken).ConfigureAwait(true);
            Devices = devices.Select(ToRow).ToArray();
            OnPropertyChanged(nameof(DeviceCountText));
        }
        catch (Exception exception)
        {
            IsLocked = false;
            LockStatusText = "配对状态不可用";
            ErrorText = ErrorText.Length == 0
                ? "读取配对状态失败：" + exception.Message
                : ErrorText;
        }

        ServerStatusText = _server.IsRunning ? "正在监听" : "未监听";
    }

    private void UpdatePinDisplay(RemotePinState state)
    {
        if (string.IsNullOrEmpty(state.Pin))
        {
            PinDisplay = "未生成（点“轮换 PIN”生成）";
            PinExpiryText = string.Empty;
            return;
        }

        PinDisplay = state.Pin;
        PinExpiryText = state.ExpiresAtUtc is { } expires
            ? "有效至 " + TimeZoneInfo.ConvertTime(expires, _clock.LocalTimeZone).ToString("HH:mm:ss")
            : string.Empty;
    }

    private static RemoteDeviceRow ToRow(PairedDevice device)
    {
        var pairedText = device.PairedAtUtc == default
            ? string.Empty
            : "配对于 " + TimeZoneInfo.ConvertTime(device.PairedAtUtc, TimeZoneInfo.Local)
                .ToString("yyyy-MM-dd HH:mm");
        return new RemoteDeviceRow(device.DeviceId, device.DeviceName, pairedText);
    }

    private static string BuildLockText(RemotePairingLockState lockState)
    {
        if (!lockState.IsLocked)
        {
            return lockState.FailedAttempts > 0
                ? $"未锁定（失败 {lockState.FailedAttempts}/{RemoteProtocol.PairingMaxFailedAttempts} 次）"
                : "未锁定";
        }

        var remaining = lockState.LockRemaining is { } time
            ? Math.Ceiling(time.TotalMinutes) + " 分钟"
            : string.Empty;
        return "已锁定（仅本地 UI 可解锁）" + (remaining.Length > 0 ? "，剩余 " + remaining : string.Empty);
    }

    private static string BuildDetailText(RemoteSettingsDocument document)
    {
        var parts = new List<string> { "TLS=" + (document.RequireTls ? "强制" : "可选") };
        if (document.RequireClientCertificate)
        {
            parts.Add("要求客户端证书");
        }

        parts.Add("远程白名单：查询=" + (document.WhiteList.QueryStatus ? "开" : "关")
            + " 清单=" + (document.WhiteList.ListTasks ? "开" : "关")
            + " 触发=" + (document.WhiteList.TriggerShutdown ? "开" : "关")
            + " 取消=" + (document.WhiteList.CancelShutdown ? "开" : "关"));
        return string.Join("；", parts);
    }

    /// <summary>RemoteSettingsDocument 出厂默认值（与 Core 一致，供编辑暂存初始化）。</summary>
    private static class RemoteSettingsDocumentDefaults
    {
        public const string ListenAddress = "127.0.0.1";
        public const int ListenPort = 48620;
    }
}
