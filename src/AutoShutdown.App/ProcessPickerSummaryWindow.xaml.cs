using System.Windows;
using AutoShutdown.App.Presentation;

namespace AutoShutdown.App;

/// <summary>
/// 确定前路径摘要窗口（S-CLOSEUI1-D1）。在「确定」之后、返回设置页之前展示复核通过的去重目标，
/// 让用户最后确认「任务执行时只按以下完整路径匹配」。确认摘要绝不启动、关闭或终止任何进程。
/// 「返回修改」保留仍有效的勾选并回到选择窗口；「取消」关闭整个选择流程且不修改目标集合；
/// 只有「确认添加/确认替换」后才返回结果。
/// </summary>
public partial class ProcessPickerSummaryWindow : Window
{
    public ProcessPickerSummaryWindow(ProcessPickerPreview preview)
    {
        InitializeComponent();
        DataContext = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    /// <summary>用户点击「确认添加/确认替换」。</summary>
    public bool WasConfirmed { get; private set; }

    /// <summary>用户点击「返回修改」（保留勾选，回到选择窗口）。</summary>
    public bool WasBackRequested { get; private set; }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        WasConfirmed = true;
        DialogResult = true;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        WasBackRequested = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        WasConfirmed = false;
        WasBackRequested = false;
        DialogResult = false;
    }
}
