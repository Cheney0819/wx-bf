using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery.Tests;

public sealed class RecoveryStateMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-state-machine-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0, false, false, false, RecoveryActionKind.RestartAndCapture)]
    [InlineData(1, false, false, false, RecoveryActionKind.RestartAndCapture)]
    [InlineData(2, false, false, false, RecoveryActionKind.WaitPassively)]
    [InlineData(0, true, false, true, RecoveryActionKind.PublishOutputs)]
    [InlineData(0, true, false, false, RecoveryActionKind.WaitPassively)]
    [InlineData(0, false, true, false, RecoveryActionKind.WaitPassively)]
    public async Task ChoosesExpectedAction(
        int restarts,
        bool hasKey,
        bool hasPending,
        bool hasOutputs,
        RecoveryActionKind expected)
    {
        await using var fixture = await CreateFixtureAsync(restarts);
        IReadOnlyList<string> outputs = hasOutputs ? ["/generation/message_0.db"] : [];

        var action = await fixture.Machine.ObserveAsync(
            fixture.Epoch.Id,
            new CaptureObservation(
                hasKey,
                hasPending,
                outputs,
                hasKey ? null : "zero_key"),
            default);

        Assert.Equal(expected, action.Kind);
    }

    [Fact]
    public async Task ExhaustedZeroKeyObservationPersistsCircuit()
    {
        await using var fixture = await CreateFixtureAsync(restarts: 2);

        await fixture.Machine.ObserveAsync(
            fixture.Epoch.Id,
            new CaptureObservation(false, false, [], "zero_key"),
            default);

        var epoch = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, epoch!.Mode);
        Assert.True(epoch.ActiveRestartSuppressed);
        Assert.Equal("zero_key", epoch.FailureCode);
    }

    [Fact]
    public async Task PendingAfterFirstRestartPreventsSecondRestart()
    {
        await using var fixture = await CreateFixtureAsync(restarts: 1);

        var action = await fixture.Machine.ObserveAsync(
            fixture.Epoch.Id,
            new CaptureObservation(false, true, [], null),
            default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.False(await fixture.Repository.TryConsumeRestartAsync(fixture.Epoch.Id, default));
        Assert.Equal(
            1,
            (await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default))!.RestartCount);
    }

    [Fact]
    public async Task UnsupportedModuleDoesNotConsumeRestartBudget()
    {
        await using var fixture = await CreateFixtureAsync(restarts: 0);

        var action = await fixture.Machine.ObserveAsync(
            fixture.Epoch.Id,
            new CaptureObservation(false, false, [], "unsupported_module"),
            default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("unsupported_module", action.Reason);
        Assert.Equal(0, (await fixture.Repository.GetEpochAsync(
            fixture.Epoch.Id, default))!.RestartCount);
    }

    [Fact]
    public async Task IncompleteReadableOutputDoesNotSuppressLaterRestart()
    {
        await using var fixture = await CreateFixtureAsync(restarts: 0);

        var action = await fixture.Machine.ObserveAsync(
            fixture.Epoch.Id,
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: ["/generation/message_0.db"],
                FailureCode: "partial_success",
                RequiredDatabasesComplete: false),
            default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.False((await fixture.Repository.GetEpochAsync(
            fixture.Epoch.Id,
            default))!.ActiveRestartSuppressed);
        Assert.True(await fixture.Repository.TryConsumeRestartAsync(
            fixture.Epoch.Id,
            default));
    }

    [Fact]
    public async Task BeginAllowsPassiveCaptureWithoutRestoringRestartBudget()
    {
        await using var fixture = await CreateFixtureAsync(restarts: 0);
        await fixture.Repository.MarkKeyAvailableAsync(fixture.Epoch.Id, default);
        var epoch = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);

        var action = fixture.Machine.Begin(epoch!);

        Assert.Equal(RecoveryActionKind.CaptureCurrent, action.Kind);
        Assert.False(await fixture.Repository.TryConsumeRestartAsync(fixture.Epoch.Id, default));
    }

    private async Task<StateMachineFixture> CreateFixtureAsync(int restarts)
    {
        var database = Path.Combine(_root, Guid.NewGuid().ToString("N"), "recovery.db");
        var repository = new RecoveryRepository(database, TimeProvider.System);
        await repository.InitializeAsync(default);
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);
        for (var index = 0; index < restarts; index++)
            Assert.True(await repository.TryConsumeRestartAsync(epoch.Id, default));
        return new StateMachineFixture(
            repository,
            new RecoveryStateMachine(repository),
            epoch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record StateMachineFixture(
        RecoveryRepository Repository,
        RecoveryStateMachine Machine,
        RecoveryEpoch Epoch) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Repository.DisposeAsync();
    }
}
