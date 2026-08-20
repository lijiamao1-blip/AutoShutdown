using AutoShutdown.App.Infrastructure.ProcessSelection;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 进程选择窗口的确定结果（S-CLOSEUI1）。<see cref="ConfirmedProcesses"/> 为点击「确定」后
/// 再次确认仍存在且路径一致的进程；<see cref="Warnings"/> 列出被排除项（已退出/路径变化/
/// 不再满足安全校验），供窗口提示。取消窗口返回 null，绝不修改目标集合。
/// </summary>
public sealed record ProcessPickerResult
{
    public IReadOnlyList<RunningProcessInfo> ConfirmedProcesses { get; init; } = [];

    public string Warnings { get; init; } = string.Empty;
}
