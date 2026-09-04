namespace Footprint.Core.Contracts;

public sealed record FootprintEvent(
    string EventId,
    string RunId,
    long DeviceSequence,
    long RunSequence,
    string Component,
    FootprintStage Stage,
    string Step,
    string Status,
    string StageNameZh,
    string StepNameZh,
    string StatusNameZh,
    double Progress,
    string MessageCode,
    string MessageZh,
    DateTimeOffset OccurredAtUtc)
{
    public static FootprintEvent CreateRunning(
        string runId, long deviceSequence, long runSequence, FootprintStage stage,
        string stageNameZh, string stepNameZh, string messageZh) =>
        new($"Footprint_Event_{Guid.NewGuid():N}", runId, deviceSequence, runSequence,
            "Footprint_Background", stage, "running", "running", stageNameZh,
            stepNameZh, "运行中", 0, "background_running", messageZh,
            DateTimeOffset.UtcNow);
}
