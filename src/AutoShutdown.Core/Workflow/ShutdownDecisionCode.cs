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
    RealPowerConfirmationMissing = 14
}
