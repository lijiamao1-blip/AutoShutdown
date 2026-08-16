namespace AutoShutdown.App.Presentation;

/// <summary>
/// CloseApps 设置页的单个目标行（S18-D1）。承载可编辑的逐目标强杀授权（默认关闭），
/// 并在授权时经确认回调（测试可注入替身，真机为 MessageBox 风险提示）显式 opt-in；
/// 拒绝授权时回滚勾选，保持 fail-closed。授权后 <see cref="RiskVisible"/> 为真，供 UI 显示风险。
/// </summary>
public sealed class CloseAppsTargetRow : ObservableObject
{
    private readonly Func<bool> _forceKillConfirmation;
    private bool _forceKillAllowed;

    public CloseAppsTargetRow(
        string identifier,
        string? executablePath,
        int? processId,
        int? gracefulTimeoutSeconds,
        bool forceKillAllowed,
        Func<bool> forceKillConfirmation)
    {
        ArgumentNullException.ThrowIfNull(forceKillConfirmation);

        Identifier = identifier;
        ExecutablePath = executablePath;
        ProcessId = processId;
        GracefulTimeoutSeconds = gracefulTimeoutSeconds;
        _forceKillAllowed = forceKillAllowed;
        _forceKillConfirmation = forceKillConfirmation;
    }

    /// <summary>脱敏标签（文件名或 pid:N）。</summary>
    public string Identifier { get; }

    public string? ExecutablePath { get; }

    public int? ProcessId { get; }

    public int? GracefulTimeoutSeconds { get; }

    public bool ForceKillAllowed
    {
        get => _forceKillAllowed;
        set
        {
            if (value && !_forceKillAllowed && !_forceKillConfirmation())
            {
                // 拒绝授权：回滚勾选，保持默认关闭（fail-closed）。
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _forceKillAllowed, value))
            {
                OnPropertyChanged(nameof(RiskVisible));
            }
        }
    }

    /// <summary>已授权强杀时显示风险提示。</summary>
    public bool RiskVisible => ForceKillAllowed;
}
