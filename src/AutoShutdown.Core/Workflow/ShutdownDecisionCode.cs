namespace AutoShutdown.Core.Workflow;

public enum ShutdownDecisionCode
{
    Unknown = 0,
    Allowed = 1,
    ConfigurationUnavailable = 2,
    TestModeRequired = 3,
    InvalidState = 4,
    ExecutionFlagMissing = 5,
    InvalidIdentity = 6,
    InvalidAction = 7,
    ActionNotAllowed = 8,
    InvalidTiming = 9,
    PowerServiceRejected = 10,
    PowerServiceFailed = 11,
    PowerServiceException = 12,
    RealPowerNotEnabled = 13,
    RealPowerConfirmationMissing = 14,

    /// <summary>Pre-Pipeline 中 block 动作失败，取消电源意图（S16）。</summary>
    PrePipelineBlocked = 15,

    /// <summary>无人值守等效确认未获授权（S20）：授权失效/动作不匹配/实例终结，fail-closed 拒绝。</summary>
    UnattendedNotAuthorized = 16
}
