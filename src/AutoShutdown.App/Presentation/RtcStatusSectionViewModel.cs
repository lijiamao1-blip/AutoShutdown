using System.Windows.Input;
using AutoShutdown.Core.Rtc;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 一次性 RTC 唤醒能力状态（S21 设置页，只读展示）。能力/设置/清除结果诚实上报：
/// 不支持、权限不足、固件拒绝、取消一律明确显示失败原因，绝不伪装成功。
/// 查询仅调用能力探测，不设置任何真实唤醒定时器。
/// </summary>
public sealed class RtcStatusSectionViewModel : ObservableObject
{
    private readonly IRtcWakeService _rtcWakeService;
    private readonly Action<string>? _log;

    private string _statusText = "未查询";
    private string _detailText = string.Empty;
    private string _errorText = string.Empty;
    private bool _isSupported;
    private bool _isBusy;

    public RtcStatusSectionViewModel(
        IRtcWakeService rtcWakeService,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(rtcWakeService);
        _rtcWakeService = rtcWakeService;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            () => !_isBusy);
    }

    public AsyncRelayCommand RefreshCommand { get; }

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

    public bool IsSupported
    {
        get => _isSupported;
        private set => SetProperty(ref _isSupported, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>查询平台 RTC 唤醒能力并诚实展示。仅能力探测，不设置任何真实定时器。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorText = string.Empty;
        IsBusy = true;
        try
        {
            var capability = await _rtcWakeService.GetCapabilityAsync(cancellationToken).ConfigureAwait(true);
            switch (capability.Status)
            {
                case RtcWakeCapabilityStatus.Supported:
                    IsSupported = true;
                    StatusText = capability.WakeScope == RtcWakeScope.SuspendAndPowerOff
                        ? "支持（含完全关机后自动唤醒）"
                        : "支持（仅睡眠/休眠后自动唤醒）";
                    DetailText = capability.Reason;
                    break;

                case RtcWakeCapabilityStatus.NotSupported:
                    IsSupported = false;
                    StatusText = "不支持";
                    DetailText = capability.Reason;
                    break;

                default:
                    IsSupported = false;
                    StatusText = "状态未知";
                    DetailText = capability.Reason;
                    ErrorText = "无法确定 RTC 唤醒能力（fail-closed，不会尝试设置唤醒）。";
                    break;
            }

            _log?.Invoke("RTC 唤醒能力：" + StatusText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            IsSupported = false;
            StatusText = "查询失败";
            DetailText = string.Empty;
            ErrorText = "RTC 唤醒能力查询失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
