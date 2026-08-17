using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20 检查点 4：R3 fail-safe 与端到端安全不变量。
/// 覆盖 Pipeline block、默认关闭（NotFound）、损坏记录、撤销，以及唯一电源出口。
/// </summary>
public sealed class S20_FailSafeTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnattendedAuthorized_ButPrePipelineBlocked_BlocksPower()
    {
        // 无人值守授权有效，但 Pre-Pipeline 的 Block 动作失败 → 唯一电源出口前阻断。
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            new PrePipelineRunner([new BlockingAction()]),
            new FixedUnattendedPolicyService(action => Authorized(action)),
            new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, outcome.DecisionCode);
        Assert.Empty(power.Requests);
        // 审计字段仍保留等效确认，便于追溯。
        Assert.NotNull(outcome.UnattendedConfirmation);
    }

    [Fact]
    public async Task UnattendedNotFound_DefaultDisabled_FailClosedNoPower_R3()
    {
        // R3 fail-safe：无授权记录（默认关闭）→ 等效确认拒绝，绝不调用电源。
        var policy = CreatePolicyService(new InMemoryStorage());
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task UnattendedCorrupt_FailClosedNoPower_R3()
    {
        var policy = CreatePolicyService(new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt });
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task UnattendedEnabledThenRevoked_FailClosedNoPower()
    {
        var storage = new InMemoryStorage();
        var policy = CreatePolicyService(storage);

        var enabled = await policy.EnableAsync(
            new UnattendedEnableRequest { Action = PowerAction.Shutdown, SecondConfirmationCompleted = true },
            CancellationToken.None);
        Assert.True(enabled.Succeeded);

        var revoked = await policy.RevokeAsync(CancellationToken.None);
        Assert.True(revoked.Succeeded);

        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task UnattendedExpired_FailClosedNoPower()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            UnattendedAuthorizationStore.FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "AuthorizationVersion": 1,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "AuthorizedAction": 1,
              "ExpiresAtUtc": "2024-01-15T11:00:00Z"
            }
            """);
        var policy = CreatePolicyService(storage);
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task UnattendedAuthorized_UniquePowerExit_ExactlyOneCall()
    {
        var policy = CreatePolicyService(AuthorizedStorage());
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: new UnattendedConfirmationEvaluator());

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Single(power.Requests); // 唯一电源出口：无重复、无第二调用方。
    }

    private static UnattendedPolicyService CreatePolicyService(InMemoryStorage storage)
        => new(new UnattendedAuthorizationStore(storage), new FakeClock(Now));

    private static InMemoryStorage AuthorizedStorage()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            UnattendedAuthorizationStore.FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "AuthorizationVersion": 5,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "AuthorizedAction": 1,
              "TriggerReason": "nightly"
            }
            """);
        return storage;
    }

    private static AppConfig RealPowerConfig() => ValidConfig() with { TestMode = false, RealPowerEnabled = true };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        UseUnattended = true
    };

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 1,
            AuthorizedAtUtc = Now,
            AuthorizedAction = action
        });

    private static PowerResult AcceptedResult() => new()
    {
        Outcome = PowerOutcome.Accepted,
        WasSimulated = false,
        Message = "accepted"
    };

    private sealed class BlockingAction : IPreShutdownAction
    {
        public string Name => "BlockAction";

        public FailurePolicy FailurePolicy => FailurePolicy.Block;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(new PrePipelineActionResult
            {
                Succeeded = false,
                FailurePolicy = FailurePolicy.Block,
                ErrorMessage = "blocked by test"
            });
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FixedConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedUnattendedPolicyService : IUnattendedPolicyService
    {
        private readonly Func<PowerAction, UnattendedAuthorizationDecision> _evaluate;

        public FixedUnattendedPolicyService(Func<PowerAction, UnattendedAuthorizationDecision> evaluate)
            => _evaluate = evaluate;

        public Task<UnattendedAuthorizationDecision> EvaluateAsync(
            PowerAction action,
            CancellationToken cancellationToken) => Task.FromResult(_evaluate(action));

        public Task<UnattendedEnableResult> EnableAsync(
            UnattendedEnableRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult _result;

        public RecordingPowerService(PowerResult result) => _result = result;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public StorageWriteStatus WriteStatus { get; init; } = StorageWriteStatus.Success;

        public void Seed(string relativePath, string json)
        {
            using var document = JsonDocument.Parse(json);
            _documents[relativePath] = document.RootElement.Clone();
        }

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (ReadStatus != StorageReadStatus.Success)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = ReadStatus,
                    Error = "Simulated read failure."
                });
            }

            if (!_documents.TryGetValue(relativePath, out var element))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            return Task.FromResult(new StorageReadResult<T>
            {
                Status = StorageReadStatus.Success,
                Value = element.Deserialize<T>()
            });
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            if (WriteStatus != StorageWriteStatus.Success)
            {
                return Task.FromResult(new StorageWriteResult
                {
                    Status = WriteStatus,
                    Error = "Simulated write failure."
                });
            }

            _documents[relativePath] = JsonSerializer.SerializeToElement(value);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }
}
