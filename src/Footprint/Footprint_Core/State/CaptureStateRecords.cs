using Footprint.Core.Capture;
using Footprint.Core.Contracts;

namespace Footprint.Core.State;

public enum CaptureStageStatus
{
    Pending,
    Waiting,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Retrying,
    Skipped
}

public sealed record CaptureRunState(
    string RunId,
    CaptureGenerationId Generation,
    FootprintStage Stage,
    string Step,
    CaptureStageStatus Status,
    long CheckpointVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record RestartBudgetRecord(
    string BudgetKey,
    string Reason,
    string? CommandId,
    DateTimeOffset ConsumedAtUtc,
    DateTimeOffset CooldownUntilUtc);
