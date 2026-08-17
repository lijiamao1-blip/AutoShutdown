using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Unattended;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S20_UnattendedPolicyServiceTests
{
    private const string FileName = "unattended.json";
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Evaluate_WhenNotFound_IsNotAuthorized()
    {
        var service = CreateService(new InMemoryStorage());

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.NotFound, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenDisabled_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 1, "Enabled": false, "AuthorizationVersion": 1 }""");
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Disabled, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenRevoked_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": false,
              "AuthorizationVersion": 2,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "RevokedAtUtc": "2024-01-15T11:00:00Z"
            }
            """);
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Revoked, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenExpired_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
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
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Expired, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenActionMismatch_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "AuthorizationVersion": 1,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "AuthorizedAction": 1
            }
            """);
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Hibernate, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.ActionMismatch, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenUnknownAction_IsNotAuthorized()
    {
        var service = CreateService(new InMemoryStorage());

        var decision = await service.EvaluateAsync(PowerAction.Unknown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.ActionMismatch, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenCorrupt_IsNotAuthorized()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Corrupt, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenUnsupportedVersion_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 999 }""");
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.UnsupportedVersion, decision.Status);
    }

    [Fact]
    public async Task Evaluate_WhenAuthorized_IsAuthorized()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "AuthorizationVersion": 3,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "AuthorizedAction": 1,
              "TriggerReason": "nightly"
            }
            """);
        var service = CreateService(storage);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);

        Assert.True(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Authorized, decision.Status);
        Assert.Equal(3, decision.Policy!.AuthorizationVersion);
    }

    [Fact]
    public async Task Enable_WithoutSecondConfirmation_IsRejected()
    {
        var service = CreateService(new InMemoryStorage());

        var result = await service.EnableAsync(
            new UnattendedEnableRequest { Action = PowerAction.Shutdown, SecondConfirmationCompleted = false },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedEnableStatus.MissingSecondConfirmation, result.Status);
    }

    [Fact]
    public async Task Enable_WithUnknownAction_IsRejected()
    {
        var service = CreateService(new InMemoryStorage());

        var result = await service.EnableAsync(
            new UnattendedEnableRequest { Action = PowerAction.Unknown, SecondConfirmationCompleted = true },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedEnableStatus.InvalidAction, result.Status);
    }

    [Fact]
    public async Task Enable_WithPastExpiry_IsRejected()
    {
        var service = CreateService(new InMemoryStorage());

        var result = await service.EnableAsync(
            new UnattendedEnableRequest
            {
                Action = PowerAction.Shutdown,
                SecondConfirmationCompleted = true,
                ExpiresAtUtc = Now.AddMinutes(-1)
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedEnableStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task Enable_ThenEvaluate_IsAuthorized_AndVersionIncrementsOnReenable()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var first = await service.EnableAsync(
            new UnattendedEnableRequest
            {
                Action = PowerAction.Shutdown,
                SecondConfirmationCompleted = true,
                TriggerReason = "first enable"
            },
            CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Policy!.AuthorizationVersion);

        var second = await service.EnableAsync(
            new UnattendedEnableRequest
            {
                Action = PowerAction.Restart,
                SecondConfirmationCompleted = true,
                TriggerReason = "re-enable"
            },
            CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Policy!.AuthorizationVersion);

        var decision = await service.EvaluateAsync(PowerAction.Restart, CancellationToken.None);
        Assert.True(decision.IsAuthorized);
        Assert.Equal(2, decision.Policy!.AuthorizationVersion);
    }

    [Fact]
    public async Task Revoke_WhenNotFound_SucceedsWithoutWrite()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var result = await service.RevokeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Revoke_ThenEvaluate_IsNotAuthorized()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var enabled = await service.EnableAsync(
            new UnattendedEnableRequest { Action = PowerAction.Shutdown, SecondConfirmationCompleted = true },
            CancellationToken.None);
        Assert.True(enabled.Succeeded);

        var revoked = await service.RevokeAsync(CancellationToken.None);
        Assert.True(revoked.Succeeded);
        Assert.False(revoked.Policy!.Enabled);
        Assert.NotNull(revoked.Policy!.RevokedAtUtc);

        var decision = await service.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);
        Assert.False(decision.IsAuthorized);
        Assert.Equal(UnattendedPolicyStatus.Revoked, decision.Status);
    }

    [Fact]
    public async Task Revoke_WhenCorrupt_FailsWithoutOverwritingEvidence()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var service = CreateService(storage);

        var result = await service.RevokeAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedRevokeStatus.IoFailure, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    private static UnattendedPolicyService CreateService(InMemoryStorage storage)
        => new(new UnattendedAuthorizationStore(storage), new FakeClock(Now));

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _utcNow;

        public FakeClock(DateTimeOffset utcNow) => _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();
        private readonly List<string> _writePaths = new();

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public StorageWriteStatus WriteStatus { get; init; } = StorageWriteStatus.Success;

        public int WriteCount => _writePaths.Count;

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

            _writePaths.Add(relativePath);
            _documents[relativePath] = JsonSerializer.SerializeToElement(value);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }
}
