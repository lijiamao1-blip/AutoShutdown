using System.Runtime.InteropServices;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace AutoShutdown.App.Infrastructure.TaskScheduler;

/// <summary>
/// Windows 任务计划程序适配器（S22 CP4 真机实现）：把 Core 纯 outbound 同步引擎接上本机
/// Task Scheduler（TaskScheduler 2.12.2，纯托管，MIT）。只创建/更新/删除/查询自家专属目录
/// <see cref="TaskSyncNaming.DedicatedFolderPath"/> 下、名字可解析为「应用标识 + 稳定本地 id」
/// 的任务；外部动作永远是「回调本地应用」的 ExecAction（--trigger-task &lt;id&gt;），绝不携带
/// 电源命令。权限/COM/IO 异常映射为 PermissionDenied/Invalid/IoFailure，UI 据此给出修复建议。
/// 防御性二次校验：任何入参名字无法解析为自家任务一律拒绝（Invalid），绝不触碰他应用任务。
/// </summary>
public sealed class WinTaskSchedulerAdapter : ITaskSchedulerAdapter
{
    public Task<ExternalTaskQueryResult> QueryOwnedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var service = Connect();
            var folder = GetOrCreateFolder(service);
            var states = new List<ExternalTaskState>();
            foreach (Microsoft.Win32.TaskScheduler.Task task in folder.Tasks)
            {
                // 只有「专属目录 + 应用标识 + 稳定本地 id」三重条件匹配的任务才视为自家。
                if (!TaskSyncNaming.TryParseOwnedTaskName(task.Name, out _))
                {
                    continue;
                }

                states.Add(ToState(task));
            }

            return Task.FromResult(new ExternalTaskQueryResult
            {
                Status = ExternalTaskQueryStatus.Success,
                Tasks = states
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPermission(exception))
        {
            return Task.FromResult(QueryFailure(
                ExternalTaskQueryStatus.PermissionDenied,
                exception));
        }
        catch (Exception exception)
        {
            return Task.FromResult(QueryFailure(
                ExternalTaskQueryStatus.IoFailure,
                exception));
        }
    }

    public Task<ExternalTaskMutationResult> CreateAsync(
        ExternalTaskSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOwnedSpec(spec))
        {
            return Task.FromResult(Failure(
                ExternalTaskMutationStatus.Invalid,
                "The external task name is not owned by this application."));
        }

        try
        {
            using var service = Connect();
            Register(service, spec, TaskCreation.CreateOrUpdate);
            return Task.FromResult(Success("Created."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPermission(exception))
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.PermissionDenied, exception));
        }
        catch (Exception exception) when (IsInvalid(exception))
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.Invalid, exception));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.IoFailure, exception));
        }
    }

    public Task<ExternalTaskMutationResult> UpdateAsync(
        ExternalTaskSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOwnedSpec(spec))
        {
            return Task.FromResult(Failure(
                ExternalTaskMutationStatus.Invalid,
                "The external task name is not owned by this application."));
        }

        try
        {
            using var service = Connect();
            Register(service, spec, TaskCreation.CreateOrUpdate);
            return Task.FromResult(Success("Updated."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPermission(exception))
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.PermissionDenied, exception));
        }
        catch (Exception exception) when (IsInvalid(exception))
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.Invalid, exception));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.IoFailure, exception));
        }
    }

    public Task<ExternalTaskMutationResult> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 防御性二次校验：只删除名字可解析为自家稳定 id 的任务；他应用任务绝不触碰。
        if (!TaskSyncNaming.TryParseOwnedTaskName(taskName, out _))
        {
            return Task.FromResult(Failure(
                ExternalTaskMutationStatus.Invalid,
                "Refusing to delete a task that is not owned by this application."));
        }

        try
        {
            using var service = Connect();
            var folder = GetOrCreateFolder(service);
            folder.DeleteTask(taskName, false);
            return Task.FromResult(Success("Deleted."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.IO.FileNotFoundException)
        {
            // 目录已不存在：目标任务本就全部消失，视作已清理成功。
            return Task.FromResult(Success("Already gone."));
        }
        catch (Exception exception) when (IsPermission(exception))
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.PermissionDenied, exception));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Failure(ExternalTaskMutationStatus.IoFailure, exception));
        }
    }

    public void Dispose()
    {
        // 每个操作都使用 using 作用域的 TaskService；此处无需额外清理。
    }

    // ---- 连接与目录 ----

    private static TaskService Connect() => new();

    /// <summary>取专属目录；不存在则创建（目录是应用专用命名空间，创建无副作用）。</summary>
    private static TaskFolder GetOrCreateFolder(TaskService service)
    {
        try
        {
            return service.GetFolder(TaskSyncNaming.DedicatedFolderPath);
        }
        catch (System.IO.FileNotFoundException)
        {
            return service.RootFolder.CreateFolder(TaskSyncNaming.DedicatedFolderPath, null, false);
        }
    }

    // ---- 写侧：规格 → TaskDefinition 并注册 ----

    private static void Register(TaskService service, ExternalTaskSpec spec, TaskCreation creation)
    {
        var folder = GetOrCreateFolder(service);
        var task = folder.RegisterTaskDefinition(
            spec.Name,
            BuildDefinition(service, spec),
            creation,
            userId: null,
            password: null,
            logonType: TaskLogonType.InteractiveToken,
            sddl: null);
        // 注册后按规格设定启停：禁用任务仍注册（同步引擎幂等可读），但绝不触发。
        task.Enabled = spec.Enabled;
    }

    private static TaskDefinition BuildDefinition(TaskService service, ExternalTaskSpec spec)
    {
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = spec.Description;
        // 冗余候选：错过的触发尽量补跑；动作只是快速回调本地应用，禁止 3 天默认时限。
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.Enabled = true;

        var trigger = BuildTrigger(spec.Trigger);
        if (trigger is not null)
        {
            definition.Triggers.Add(trigger);
        }

        if (spec.Trigger.Kind == ExternalTriggerKind.Idle
            && spec.Trigger.IdleDuration is { } idleDuration)
        {
            definition.Settings.IdleSettings.IdleDuration = idleDuration;
        }

        // 外部动作永远只回调本地应用，绝不携带电源命令。
        definition.Actions.Add(new ExecAction(spec.AppExePath, spec.TriggerArgument, null));
        return definition;
    }

    private static Trigger? BuildTrigger(ExternalTriggerSpec trigger)
    {
        return trigger.Kind switch
        {
            ExternalTriggerKind.OneTime when trigger.StartBoundary is { } boundary
                => new TimeTrigger(boundary),

            ExternalTriggerKind.Daily when trigger.StartTimeOfDay is { } dailyTime
                => new DailyTrigger(1) { StartBoundary = TodayAt(dailyTime) },

            ExternalTriggerKind.Weekly when trigger.StartTimeOfDay is { } weeklyTime
                && trigger.DaysOfWeek is { Count: > 0 } days
                => new WeeklyTrigger(DaysFromFlags(days), 1) { StartBoundary = TodayAt(weeklyTime) },

            ExternalTriggerKind.MonthlyOnWeekdays when trigger.StartTimeOfDay is { } monthTime
                && trigger.NthWeek is { } nthWeek
                => new MonthlyDOWTrigger(
                    DaysFromFlags(trigger.DaysOfWeek ?? []),
                    MonthsOfTheYear.AllMonths,
                    WeekFromNth(nthWeek))
                { StartBoundary = TodayAt(monthTime) },

            ExternalTriggerKind.Idle
                => new IdleTrigger(),

            _ => null
        };
    }

    // ---- 读侧：系统任务 → 可观察状态 ----

    private static ExternalTaskState ToState(Microsoft.Win32.TaskScheduler.Task task)
    {
        var definition = task.Definition;
        var observedTrigger = MapObservedTrigger(
            definition.Triggers.FirstOrDefault(),
            definition.Settings);
        var action = definition.Actions.OfType<ExecAction>().FirstOrDefault();

        return new ExternalTaskState
        {
            Name = task.Name,
            Enabled = task.Enabled,
            TriggerSignature = observedTrigger is null
                ? string.Empty
                : ExternalTriggerSignature.Build(observedTrigger),
            ActionPath = action?.Path ?? string.Empty,
            Arguments = action?.Arguments ?? string.Empty,
            LastRunTime = task.LastRunTime == DateTime.MinValue ? null : task.LastRunTime
        };
    }

    /// <summary>
    /// 系统真实触发器 → 平台无关 <see cref="ExternalTriggerSpec"/>，与 mapper 同构，
    /// 从而经 <see cref="ExternalTriggerSignature.Build"/> 生成与期望侧可比的规范签名
    /// （周期触发不含绝对开始日期，避免每日假更新）。
    /// </summary>
    private static ExternalTriggerSpec? MapObservedTrigger(Trigger? trigger, TaskSettings settings)
    {
        switch (trigger)
        {
            case TimeTrigger time when time.StartBoundary != default:
                return new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.OneTime,
                    StartBoundary = time.StartBoundary
                };

            case DailyTrigger daily:
                return new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Daily,
                    StartTimeOfDay = TimeOfDay(daily.StartBoundary)
                };

            case WeeklyTrigger weekly:
                return new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Weekly,
                    StartTimeOfDay = TimeOfDay(weekly.StartBoundary),
                    DaysOfWeek = DaysOfWeekFromFlags(weekly.DaysOfWeek)
                };

            case MonthlyDOWTrigger monthly when NthWeekFromFlag(monthly.WeeksOfMonth) is { } nthWeek:
                return new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.MonthlyOnWeekdays,
                    StartTimeOfDay = TimeOfDay(monthly.StartBoundary),
                    DaysOfWeek =
                    [
                        DayOfWeek.Monday,
                        DayOfWeek.Tuesday,
                        DayOfWeek.Wednesday,
                        DayOfWeek.Thursday,
                        DayOfWeek.Friday
                    ],
                    NthWeek = nthWeek
                };

            case IdleTrigger when settings.IdleSettings.IdleDuration is { } idle && idle > TimeSpan.Zero:
                return new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Idle,
                    IdleDuration = idle
                };

            default:
                return null;
        }
    }

    // ---- 异常与结果映射 ----

    private static bool IsPermission(Exception exception)
        => exception is UnauthorizedAccessException or System.Security.SecurityException
            || (exception is COMException com && (uint)com.HResult == 0x80070005); // E_ACCESSDENIED

    private static bool IsInvalid(Exception exception)
        => exception is ArgumentException or InvalidOperationException;

    private static bool IsOwnedSpec(ExternalTaskSpec spec)
        => !string.IsNullOrWhiteSpace(spec.Name)
            && TaskSyncNaming.TryParseOwnedTaskName(spec.Name, out _);

    private static ExternalTaskQueryResult QueryFailure(
        ExternalTaskQueryStatus status,
        Exception exception) => new()
        {
            Status = status,
            Message = exception.Message
        };

    private static ExternalTaskMutationResult Success(string message) => new()
    {
        Status = ExternalTaskMutationStatus.Success,
        Message = message
    };

    private static ExternalTaskMutationResult Failure(
        ExternalTaskMutationStatus status,
        Exception exception) => new()
        {
            Status = status,
            Message = exception.Message
        };

    private static ExternalTaskMutationResult Failure(
        ExternalTaskMutationStatus status,
        string message) => new()
        {
            Status = status,
            Message = message
        };

    // ---- 小工具 ----

    private static TimeOnly? TimeOfDay(DateTime value)
        => value == default ? null : new TimeOnly(value.Hour, value.Minute, value.Second);

    private static DateTime TodayAt(TimeOnly time)
    {
        var today = DateTime.Today;
        return new DateTime(today.Year, today.Month, today.Day, time.Hour, time.Minute, time.Second);
    }

    private static DaysOfTheWeek DaysFromFlags(IEnumerable<DayOfWeek> days)
    {
        var flags = (DaysOfTheWeek)0;
        foreach (var day in days)
        {
            flags |= (DaysOfTheWeek)(1 << (int)day);
        }

        return flags;
    }

    private static IReadOnlyList<DayOfWeek> DaysOfWeekFromFlags(DaysOfTheWeek flags)
    {
        var days = new List<DayOfWeek>();
        for (var i = 0; i < 7; i++)
        {
            if (flags.HasFlag((DaysOfTheWeek)(1 << i)))
            {
                days.Add((DayOfWeek)i);
            }
        }

        return days;
    }

    private static WhichWeek WeekFromNth(int nthWeek)
        => nthWeek == 5 ? WhichWeek.LastWeek : (WhichWeek)(1 << (nthWeek - 1));

    private static int? NthWeekFromFlag(WhichWeek weeks)
        => weeks switch
        {
            WhichWeek.FirstWeek => 1,
            WhichWeek.SecondWeek => 2,
            WhichWeek.ThirdWeek => 3,
            WhichWeek.FourthWeek => 4,
            WhichWeek.LastWeek => 5,
            _ => null
        };
}
