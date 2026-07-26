namespace Wx411.Core;

public enum EvidenceOperationKind
{
    PreciseCapture,
}

public enum EvidenceOperationOutcome
{
    Running,
    Success,
    Failed,
    Cancelled,
}

public enum EvidenceGateStatus
{
    Pass,
    Fail,
    Incomplete,
    NotApplicable,
}

public sealed record EvidenceProcessSelection(int? Pid, string Name, bool ScanAll);

public sealed record EvidenceFileRecord(
    string Path,
    bool Exists,
    long? Length,
    string? Sha256,
    string? IntegrityCheck,
    string? Error);

public sealed record EvidenceDirectoryEntry(
    string Name,
    bool IsDirectory,
    long? Length,
    DateTimeOffset LastWriteUtc,
    bool IsTemporaryArtifact);

public sealed record EvidenceDirectorySnapshot(
    string Path,
    DateTimeOffset CapturedUtc,
    IReadOnlyList<EvidenceDirectoryEntry> Entries,
    string? Error);

public sealed record EvidenceOperationRecord(
    Guid OperationId,
    EvidenceOperationKind Kind,
    EvidenceOperationOutcome Outcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    EvidenceProcessSelection Process,
    EvidenceFileRecord SourceBefore,
    EvidenceFileRecord? SourceAfter,
    string OutputDirectory,
    EvidenceDirectorySnapshot DirectoryBefore,
    EvidenceDirectorySnapshot? DirectoryAfter,
    IReadOnlyList<EvidenceFileRecord> Outputs,
    string? ErrorType,
    string? ErrorMessage,
    bool? PendingCaptureTicketNotReused);

public sealed record EvidenceSessionSnapshot(
    string ToolVersion,
    DateTimeOffset StartedUtc,
    string OsDescription,
    bool Is64BitProcess,
    bool Is64BitOperatingSystem,
    IReadOnlyList<EvidenceOperationRecord> Operations);

public sealed record EvidenceGateResult(
    string Gate,
    EvidenceGateStatus Status,
    string Detail);

public sealed record EvidenceAssessment(
    EvidenceGateResult GateA,
    EvidenceGateResult GateB,
    EvidenceGateResult GateC,
    EvidenceGateResult GateD,
    EvidenceGateStatus Overall);

public static class EvidenceGateEvaluator
{
    public static EvidenceAssessment Assess(EvidenceSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(session.Operations);

        var gateA = NotApplicable(
            "A",
            "旧内存扫描已在 RC8 废弃，不参与当前精准捕获验收。");
        var gateB = AssessRecoveryGate(
            "B",
            EvidenceOperationKind.PreciseCapture,
            session.Operations);
        var gateC = AssessCancellationGate(session.Operations);
        var gateD = AssessSourceImmutability(session.Operations);
        var requiredGates = new[] { gateB, gateC };
        var overall = requiredGates.Any(gate => gate.Status == EvidenceGateStatus.Fail)
            ? EvidenceGateStatus.Fail
            : requiredGates.All(gate => gate.Status == EvidenceGateStatus.Pass)
                ? EvidenceGateStatus.Pass
                : EvidenceGateStatus.Incomplete;
        return new EvidenceAssessment(gateA, gateB, gateC, gateD, overall);
    }

    private static EvidenceGateResult AssessRecoveryGate(
        string gate,
        EvidenceOperationKind kind,
        IReadOnlyList<EvidenceOperationRecord> operations)
    {
        var candidates = operations.Where(operation => operation.Kind == kind).ToArray();
        if (candidates.Length == 0)
            return Incomplete(gate, $"未记录 {kind} 操作。");

        foreach (var operation in candidates.Where(item => item.Outcome == EvidenceOperationOutcome.Success))
        {
            var verifiedOutput = operation.Outputs.Any(IsVerifiedSqliteOutput);
            if (verifiedOutput)
                return Pass(gate, "已记录成功操作、输出 SHA-256 和 integrity_check。");

            if (operation.Outputs.Any(IsExplicitIntegrityFailure))
                return Fail(gate, "成功操作的输出未通过 integrity_check。");
        }

        if (candidates.Any(operation => operation.Outcome == EvidenceOperationOutcome.Failed))
            return Fail(gate, "操作已执行但失败，未形成完整成功证据。");

        return Incomplete(gate, "已有操作记录，但缺少成功输出或完整性检查证据。");
    }

    private static EvidenceGateResult AssessCancellationGate(
        IReadOnlyList<EvidenceOperationRecord> operations)
    {
        var cancellations = operations
            .Where(operation => operation.Kind == EvidenceOperationKind.PreciseCapture &&
                                operation.Outcome == EvidenceOperationOutcome.Cancelled)
            .ToArray();
        if (cancellations.Length == 0)
            return Incomplete("C", "未记录精准捕获取消操作。");

        foreach (var operation in cancellations)
        {
            if (operation.PendingCaptureTicketNotReused == false)
                return Fail("C", "检测到 DPAPI pending 票据被重复使用。");
            var after = operation.DirectoryAfter;
            if (after is null || !IsCleanCancellationDirectory(after))
                continue;
            if (after.Entries.Any(entry => entry.IsTemporaryArtifact))
                return Fail("C", "取消后输出目录存在临时或 SQLite 边车文件。");
        }

        if (cancellations.Any(operation =>
                operation.DirectoryAfter is { } after &&
                IsCleanCancellationDirectory(after) &&
                !after.Entries.Any(entry => entry.IsTemporaryArtifact) &&
                operation.PendingCaptureTicketNotReused == true))
            return Pass("C", "取消后目录清洁，且后续运行确认 DPAPI pending 票据未复用。");

        return Incomplete("C", "取消后目录已记录，但缺少 DPAPI pending 票据未复用证据。");
    }

    private static bool IsCleanCancellationDirectory(EvidenceDirectorySnapshot snapshot) =>
        snapshot.Error is null ||
        string.Equals(snapshot.Error, "directory_not_found", StringComparison.Ordinal);

    private static EvidenceGateResult AssessSourceImmutability(
        IReadOnlyList<EvidenceOperationRecord> operations)
    {
        var completed = operations
            .Where(operation => operation.Outcome != EvidenceOperationOutcome.Running)
            .ToArray();
        if (completed.Length == 0)
            return Incomplete("D", "未记录已完成操作的源文件前后 SHA-256。");

        var compared = 0;
        foreach (var operation in completed)
        {
            var before = operation.SourceBefore;
            var after = operation.SourceAfter;
            if (!IsHashAvailable(before) || after is null || !IsHashAvailable(after))
                continue;
            compared++;
            if (!string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail("D", $"源文件哈希发生变化：{before.Path}");
        }

        return compared == completed.Length
            ? Pass("D", $"{compared} 次操作的源文件前后 SHA-256 完全一致。")
            : Incomplete("D", "部分操作缺少源文件操作前或操作后 SHA-256。");
    }

    private static bool IsVerifiedSqliteOutput(EvidenceFileRecord file) =>
        IsReadableEvidenceFile(file) &&
        string.Equals(file.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitIntegrityFailure(EvidenceFileRecord file) =>
        file.IntegrityCheck is not null &&
        !string.Equals(file.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadableEvidenceFile(EvidenceFileRecord file) =>
        file.Exists && file.Error is null && !string.IsNullOrWhiteSpace(file.Sha256);

    private static bool IsHashAvailable(EvidenceFileRecord file) =>
        file.Exists && file.Error is null && !string.IsNullOrWhiteSpace(file.Sha256);

    private static EvidenceGateResult Pass(string gate, string detail) =>
        new(gate, EvidenceGateStatus.Pass, detail);

    private static EvidenceGateResult Fail(string gate, string detail) =>
        new(gate, EvidenceGateStatus.Fail, detail);

    private static EvidenceGateResult Incomplete(string gate, string detail) =>
        new(gate, EvidenceGateStatus.Incomplete, detail);

    private static EvidenceGateResult NotApplicable(string gate, string detail) =>
        new(gate, EvidenceGateStatus.NotApplicable, detail);
}
