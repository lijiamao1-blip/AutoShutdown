namespace AutoShutdown.App.Presentation;

/// <summary>
/// CloseApps 设置页的单个目标行（S18-D1 + S-CLOSEUI1）。承载可编辑的逐目标强杀授权（默认关闭），
/// 并在授权时经确认回调（测试可注入替身，真机为 MessageBox 风险提示）显式 opt-in；
/// 拒绝授权时回滚勾选，保持 fail-closed。授权后 <see cref="RiskVisible"/> 为真，供 UI 显示风险。
/// S-CLOSEUI1 增加只读识别信息（进程名/产品/公司/添加时窗口标题/添加时间），仅供展示，
/// 绝不参与执行匹配；<see cref="IsPathInvalid"/> 表示已保存路径失效，提示用户重新选择运行中的程序。
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
        Func<bool> forceKillConfirmation,
        string? processName = null,
        string? productName = null,
        string? companyName = null,
        string? windowTitleAtAdd = null,
        DateTimeOffset? addedAtUtc = null,
        bool isPathInvalid = false)
    {
        ArgumentNullException.ThrowIfNull(forceKillConfirmation);

        Identifier = identifier;
        ExecutablePath = executablePath;
        ProcessId = processId;
        GracefulTimeoutSeconds = gracefulTimeoutSeconds;
        _forceKillAllowed = forceKillAllowed;
        _forceKillConfirmation = forceKillConfirmation;
        ProcessName = processName;
        ProductName = productName;
        CompanyName = companyName;
        WindowTitleAtAdd = windowTitleAtAdd;
        AddedAtUtc = addedAtUtc;
        IsPathInvalid = isPathInvalid;
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

    // ---- S-CLOSEUI1：只读识别信息（仅展示，不参与匹配） ----

    public string? ProcessName { get; }

    public string? ProductName { get; }

    public string? CompanyName { get; }

    /// <summary>添加时主窗口标题。</summary>
    public string? WindowTitleAtAdd { get; }

    /// <summary>添加时间（UTC）。</summary>
    public DateTimeOffset? AddedAtUtc { get; }

    /// <summary>已保存路径已失效（原程序路径已失效，需重新选择运行中的程序）。</summary>
    public bool IsPathInvalid { get; }

    /// <summary>是否有任何只读识别信息可展示。</summary>
    public bool HasIdentityInfo =>
        !string.IsNullOrWhiteSpace(ProcessName)
        || !string.IsNullOrWhiteSpace(ProductName)
        || !string.IsNullOrWhiteSpace(CompanyName)
        || !string.IsNullOrWhiteSpace(WindowTitleAtAdd);

    /// <summary>识别信息摘要（产品 · 公司 · 进程名 · 添加时窗口标题）。</summary>
    public string IdentitySummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ProductName))
            {
                parts.Add(ProductName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(CompanyName))
            {
                parts.Add(CompanyName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(ProcessName))
            {
                parts.Add("进程 " + ProcessName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(WindowTitleAtAdd))
            {
                parts.Add("添加时窗口标题：" + WindowTitleAtAdd.Trim());
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>添加时间本地显示（yyyy-MM-dd HH:mm）。</summary>
    public string AddedAtDisplay => AddedAtUtc is { } addedAt
        ? TimeZoneInfo.ConvertTime(addedAt, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    /// <summary>失效提示文案。</summary>
    public static string PathInvalidHintText => "原程序路径已失效，请重新选择运行中的程序";
}
