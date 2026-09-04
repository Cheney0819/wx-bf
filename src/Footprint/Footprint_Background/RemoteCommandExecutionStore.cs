using System.Text.Json;

namespace Footprint.Background;

public sealed record CommandExecutionClaim(bool ShouldExecute, BackgroundCommandResult? Result);

public sealed class RemoteCommandExecutionStore
{
    private const string ExecutingState = "executing";
    private const string CompletedState = "completed";
    private const int RetentionDays = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    public RemoteCommandExecutionStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public BackgroundCommandResult? TryLoad(string commandId)
    {
        ValidateCommandId(commandId);
        return File.Exists(PathFor(commandId)) ? Claim(commandId).Result : null;
    }

    public CommandExecutionClaim Claim(string commandId)
    {
        ValidateCommandId(commandId);
        CleanupCompleted();
        var path = PathFor(commandId);
        if (File.Exists(path) == false)
        {
            Write(path, StoredCommandResult.Executing(commandId, DateTimeOffset.UtcNow));
            return new CommandExecutionClaim(true, null);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCommandResult>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException();
            if (string.Equals(stored.CommandId, commandId, StringComparison.Ordinal) == false)
                throw new InvalidDataException();
            if (stored.State == CompletedState && stored.CompletedAtUtc?.Offset == TimeSpan.Zero &&
                string.IsNullOrWhiteSpace(stored.ResultCode) == false &&
                string.IsNullOrWhiteSpace(stored.MessageZh) == false)
                return new CommandExecutionClaim(false, new(stored.ResultCode, stored.MessageZh));
            if (stored.State == ExecutingState && stored.StartedAtUtc.Offset == TimeSpan.Zero)
                return new CommandExecutionClaim(false, new("failed", "命令执行状态未知，已阻止重复执行。"));
            throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            Quarantine(path);
            var failed = new BackgroundCommandResult("failed", "命令结果记录损坏，错误已记录。");
            Complete(commandId, failed);
            return new CommandExecutionClaim(false, failed);
        }
    }

    public void Complete(string commandId, BackgroundCommandResult result)
    {
        ValidateCommandId(commandId);
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.ResultCode) || string.IsNullOrWhiteSpace(result.MessageZh))
            throw new InvalidDataException("远程命令结果无效。");
        Write(PathFor(commandId), StoredCommandResult.Completed(commandId, result, DateTimeOffset.UtcNow));
    }

    private string PathFor(string commandId) => Path.Combine(_root, commandId + ".json");

    private void Write(string path, StoredCommandResult value)
    {
        Directory.CreateDirectory(_root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private void CleanupCompleted()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(_root, "*.json").Take(100))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (IOException) { }
        }
    }

    private void Quarantine(string path)
    {
        if (File.Exists(path) == false) return;
        var quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        try { File.Move(path, Path.Combine(quarantine, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N"))); }
        catch (IOException) { }
    }

    private static void ValidateCommandId(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 128 ||
            char.IsAsciiLetterOrDigit(commandId[0]) == false ||
            commandId.Any(character => char.IsAsciiLetterOrDigit(character) == false && character != '_' && character != '-'))
            throw new InvalidDataException("远程命令标识无效。");
    }

    private sealed record StoredCommandResult(string CommandId, string State, string? ResultCode,
        string? MessageZh, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc)
    {
        public static StoredCommandResult Executing(string commandId, DateTimeOffset startedAtUtc) =>
            new(commandId, ExecutingState, null, null, startedAtUtc, null);
        public static StoredCommandResult Completed(string commandId, BackgroundCommandResult result,
            DateTimeOffset completedAtUtc) =>
            new(commandId, CompletedState, result.ResultCode, result.MessageZh, completedAtUtc, completedAtUtc);
    }
}
