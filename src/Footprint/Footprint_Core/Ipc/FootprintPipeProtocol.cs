namespace Footprint.Core.Ipc;

public sealed record FootprintPipeRequest(string Type);
public sealed record FootprintPipeResponse(string Status, string MessageZh, int ProtocolVersion = 1);
