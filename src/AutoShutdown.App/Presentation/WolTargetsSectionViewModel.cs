using System.Collections.ObjectModel;
using System.Windows.Input;
using AutoShutdown.Core.WakeOnLan;

namespace AutoShutdown.App.Presentation;

/// <summary>WoL 目标机器行（供设置页列表绑定）。</summary>
public sealed record WolTargetRow(
    Guid Id,
    string Name,
    string Mac,
    string? BroadcastText,
    int? Port,
    string SummaryText);

/// <summary>
/// Wake-on-LAN 目标机器管理（S21 设置页）。只向用户显式配置的局域网目标发送；
/// 增删改立即原子持久化（备份证据由 TargetMachineStore 保留），加载严格区分
/// NotFound/Corrupt/Invalid/UnsupportedVersion，损坏一律阻止变更（fail-closed）。
/// 默认安全：初始清单为空，绝不自动发送；测试发送仅由用户逐目标点击触发。
/// </summary>
public sealed class WolTargetsSectionViewModel : ObservableObject
{
    /// <summary>MAC 格式提示（UI 直接展示）。</summary>
    public const string MacFormatHint = "MAC 格式：AA:BB:CC:DD:EE:FF（必填，大写十六进制）。广播地址与端口可选；默认向 255.255.255.255:9 发送。";

    private readonly TargetMachineManager _manager;
    private readonly IWakeOnLanService _wolService;
    private readonly Func<Guid> _idGenerator;
    private readonly Action<string>? _log;

    private string _nameInput = string.Empty;
    private string _macInput = string.Empty;
    private string _broadcastInput = string.Empty;
    private string _portInput = string.Empty;
    private string _errorText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;

    public WolTargetsSectionViewModel(
        TargetMachineManager manager,
        IWakeOnLanService wolService,
        Func<Guid>? idGenerator = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(wolService);

        _manager = manager;
        _wolService = wolService;
        _idGenerator = idGenerator ?? Guid.NewGuid;
        _log = log;

        AddTargetCommand = new AsyncRelayCommand(ExecuteAddTargetAsync, () => CanEdit);
        RemoveTargetCommand = new RelayCommand(parameter =>
        {
            if (parameter is WolTargetRow row)
            {
                _ = ExecuteRemoveTargetAsync(row);
            }
        });
        TestSendTargetCommand = new RelayCommand(parameter =>
        {
            if (parameter is WolTargetRow row)
            {
                _ = ExecuteTestSendAsync(row);
            }
        });
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            () => !_isBusy);
    }

    public ObservableCollection<WolTargetRow> Targets { get; } = [];

    public string NameInput
    {
        get => _nameInput;
        set => SetProperty(ref _nameInput, value);
    }

    public string MacInput
    {
        get => _macInput;
        set => SetProperty(ref _macInput, value);
    }

    public string BroadcastInput
    {
        get => _broadcastInput;
        set => SetProperty(ref _broadcastInput, value);
    }

    public string PortInput
    {
        get => _portInput;
        set => SetProperty(ref _portInput, value);
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

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanEdit => !_isBusy;

    public AsyncRelayCommand AddTargetCommand { get; }

    public ICommand RemoveTargetCommand { get; }

    public ICommand TestSendTargetCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// 从 store 加载目标机器。NotFound 视为空清单（首次使用）；Corrupt/Invalid/
    /// UnsupportedVersion/IoFailure 一律 fail-closed 清空并上报，绝不静默回退。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorText = string.Empty;
        StatusText = string.Empty;

        try
        {
            var load = await _manager.LoadAsync(cancellationToken).ConfigureAwait(true);
            Targets.Clear();
            switch (load.Status)
            {
                case TargetMachinesLoadStatus.Success when load.Document is not null:
                    foreach (var machine in load.Document.Machines)
                    {
                        Targets.Add(ToRow(machine));
                    }

                    break;

                case TargetMachinesLoadStatus.NotFound:
                    break;

                default:
                    ErrorText = "目标机器配置不可用（" + load.Status + "）。已阻止任何变更，避免覆盖损坏证据。";
                    return;
            }

            if (Targets.Count > 0)
            {
                StatusText = $"已加载 {Targets.Count} 台目标机器。";
            }
        }
        catch (Exception exception)
        {
            ErrorText = "目标机器配置加载失败：" + exception.Message;
        }
    }

    private async Task ExecuteAddTargetAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        var name = (NameInput ?? string.Empty).Trim();
        var mac = (MacInput ?? string.Empty).Trim();
        var broadcast = (BroadcastInput ?? string.Empty).Trim();
        var portText = (PortInput ?? string.Empty).Trim();
        int? port = null;
        if (portText.Length > 0 && int.TryParse(portText, out var parsedPort))
        {
            port = parsedPort;
        }

        var target = new WakeOnLanTarget
        {
            Id = _idGenerator(),
            Name = name,
            Mac = mac,
            Ipv4BroadcastAddress = broadcast.Length == 0 ? null : broadcast,
            Port = port
        };

        var structuralError = WakeOnLanTarget.GetStructuralError(target);
        if (structuralError is not null)
        {
            ErrorText = MapStructuralError(structuralError);
            StatusText = string.Empty;
            return;
        }

        if (Targets.Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText = "已存在同名目标机器";
            StatusText = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _manager.AddAsync(target, CancellationToken.None).ConfigureAwait(true);
            if (result.Status == TargetMachineMutationStatus.Success)
            {
                Targets.Add(ToRow(target));
                NameInput = string.Empty;
                MacInput = string.Empty;
                BroadcastInput = string.Empty;
                PortInput = string.Empty;
                ErrorText = string.Empty;
                StatusText = $"已添加目标「{name}」。";
                _log?.Invoke($"已添加 WoL 目标「{name}」");
            }
            else
            {
                ErrorText = "添加失败：" + (string.IsNullOrEmpty(result.Error) ? result.Status.ToString() : result.Error);
                StatusText = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorText = "添加目标失败：" + exception.Message;
            StatusText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteRemoveTargetAsync(WolTargetRow row)
    {
        if (!CanEdit)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _manager.RemoveAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            if (result.Status == TargetMachineMutationStatus.Success)
            {
                Targets.Remove(row);
                ErrorText = string.Empty;
                StatusText = $"已移除目标「{row.Name}」。";
                _log?.Invoke($"已移除 WoL 目标「{row.Name}」");
            }
            else
            {
                ErrorText = "移除失败：" + (string.IsNullOrEmpty(result.Error) ? result.Status.ToString() : result.Error);
                StatusText = string.Empty;
            }
        }
        catch (Exception exception)
        {
            ErrorText = "移除目标失败：" + exception.Message;
            StatusText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>测试发送：仅由用户逐目标点击触发；结果明确显示（成功/失败原因）。</summary>
    private async Task ExecuteTestSendAsync(WolTargetRow row)
    {
        if (!CanEdit)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ErrorText = string.Empty;
            StatusText = string.Empty;
            var send = await _wolService.SendAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            if (send.Succeeded)
            {
                StatusText = $"已向「{row.Name}」发送 Magic Packet（{row.Mac}）。";
                _log?.Invoke($"WoL 测试发送成功：「{row.Name}」");
            }
            else
            {
                ErrorText = $"发送失败：「{row.Name}」→ {send.Message}";
                StatusText = string.Empty;
                _log?.Invoke($"WoL 测试发送失败：「{row.Name}」→ {send.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorText = "测试发送异常：" + exception.Message;
            StatusText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static WolTargetRow ToRow(WakeOnLanTarget target)
    {
        var summary = target.Ipv4BroadcastAddress is { } broadcast
            ? broadcast + ":" + (target.Port ?? WakeOnLanTarget.DefaultPort)
            : "默认广播 :" + (target.Port ?? WakeOnLanTarget.DefaultPort);
        return new WolTargetRow(
            target.Id,
            target.Name,
            target.Mac,
            target.Ipv4BroadcastAddress,
            target.Port,
            summary);
    }

    private static string MapStructuralError(string error)
        => error switch
        {
            "target.Name must not be empty." => "名称不能为空",
            "target.Mac is not a valid MAC address." => "MAC 格式无效，请输入 AA:BB:CC:DD:EE:FF",
            _ when error.Contains("Ipv4BroadcastAddress", StringComparison.Ordinal)
                => "广播地址必须是合法的 IPv4 地址（如 192.168.1.255）",
            _ when error.Contains("Port", StringComparison.Ordinal)
                => "端口必须是 1~65535 之间的整数",
            _ => error
        };
}
