using Footprint.Core.Contracts;

namespace Footprint.Core.State;

public sealed record FootprintCheckpoint(
    string RunId,
    FootprintStage Stage,
    string Step,
    long Version,
    DateTimeOffset UpdatedAtUtc);
