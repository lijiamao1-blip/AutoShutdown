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
        // S-CLOSEUI1-D1：确定先构建复核摘要（PID + 启动时间 + 路径 + 安全资格再次复核，按规范化
        // 路径去重）。返回 null 表示应停留在窗口（空选择/多选/全部复核失败，警告已展示）。
        var preview = _viewModel.BuildPreview();
        if (preview is null)
        {
            return;
        }

        var summary = new ProcessPickerSummaryWindow(preview) { Owner = this };
        var summaryResult = summary.ShowDialog();

        if (summaryResult == true && summary.WasConfirmed)
        {
            // 用户确认添加/确认替换：返回结果，由调用方合并/原子替换目标。
            ConfirmedResult = _viewModel.Commit(preview);
            DialogResult = true;
            return;
        }

        if (summaryResult == true && summary.WasBackRequested)
        {
            // 返回修改：保留仍有效的勾选，回到选择窗口继续修改。
            return;
        }

        // 摘要内「取消」：关闭整个选择流程且不修改目标集合。
        _viewModel.Cancel();
        ConfirmedResult = null;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Cancel();
        ConfirmedResult = null;
        DialogResult = false;
    }
}
