using System.Windows;
using AutoShutdown.App.Presentation;

namespace AutoShutdown.App;

/// <summary>
/// 从运行中的进程选择关闭目标窗口（S-CLOSEUI1）。纯只读：仅枚举/搜索/勾选进程信息，
/// 绝不在本窗口内启动、关闭或结束任何进程，也不发送任何窗口消息。
/// </summary>
public partial class ProcessPickerWindow : Window
{
    private readonly ProcessPickerViewModel _viewModel;

    public ProcessPickerWindow(ProcessPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    /// <summary>确定时返回复核通过的确认结果；取消时为 null。</summary>
    public ProcessPickerResult? ConfirmedResult { get; private set; }

    /// <summary>
    /// 勾选写回（S-CLOSEUI1）：只读 DataGrid 下 TwoWay 绑定不写回源，故由 Click 显式同步
    /// IsSelected（含键盘空格触发）。仅写视图模型内存标志，绝不触碰任何进程。
    /// </summary>
    private void PickerCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box
            && box.DataContext is RunningProcessRow row)
        {
            row.IsSelected = box.IsChecked == true;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var result = _viewModel.ConfirmSelection();
        if (result is null)
        {
            DialogResult = false;
            return;
        }

        if (result.ConfirmedProcesses.Count > 0)
        {
            ConfirmedResult = result;
            DialogResult = true;
            return;
        }

        if (_viewModel.HasWarnings)
        {
            // 勾选进程全部复核失败（已退出/路径变化/不再满足安全校验）：停留在窗口，提示后
            // 用户可刷新重选，绝不静默添加任何未经复核的路径。
            return;
        }

        // 未勾选任何进程：视为无改动关闭。
        ConfirmedResult = result;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Cancel();
        ConfirmedResult = null;
        DialogResult = false;
    }
}
