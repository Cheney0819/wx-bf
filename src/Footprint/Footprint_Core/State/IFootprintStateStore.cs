using Footprint.Core.Capture;

namespace Footprint.Core.State;

public interface IFootprintStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveCheckpointAsync(FootprintCheckpoint checkpoint, CancellationToken cancellationToken);
    Task<FootprintCheckpoint?> LoadCheckpointAsync(string runId, CancellationToken cancellationToken);
    Task SaveCaptureRunAsync(CaptureRunState state, CancellationToken cancellationToken);
    Task<CaptureRunState?> LoadActiveCaptureRunAsync(CancellationToken cancellationToken);
    Task<RestartPolicy> GetRestartPolicyAsync(string deviceId, CancellationToken cancellationToken);
    Task SetRestartPolicyAsync(string deviceId, RestartPolicy policy, CancellationToken cancellationToken);
    Task<bool> TryConsumeRestartBudgetAsync(RestartBudgetRecord record, CancellationToken cancellationToken);
    Task<RestartBudgetRecord?> LoadRestartBudgetAsync(string budgetKey, CancellationToken cancellationToken);
}
