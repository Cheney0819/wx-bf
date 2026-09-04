using System.Text;
using System.Text.Json;

namespace Footprint.Core.Runtime;

public sealed class SourceEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _deviceId;
    private long _lastTicks;

    public SourceEventOutbox(string root, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        SourceOperationalEvent.ValidateIdentifier(deviceId, nameof(deviceId));
        _root = System.IO.Path.GetFullPath(root);
        _deviceId = deviceId;
        Directory.CreateDirectory(_root);
    }

    public string RootDirectory => _root;

    public StoredSourceOperationalEvent Enqueue(string componentZh, string runId, string eventZh, string resultZh)
    {
        SourceOperationalEvent.ValidateIdentifier(runId, nameof(runId));
        var occurredAtUtc = NextOccurredAtUtc();
        var eventId = $"{occurredAtUtc.UtcTicks:D19}-{Guid.NewGuid():N}";
        var item = new SourceOperationalEvent(eventId, _deviceId, runId,
            SourceOperationalEvent.CleanText(componentZh), SourceOperationalEvent.CleanText(eventZh),
            SourceOperationalEvent.CleanText(resultZh), occurredAtUtc);
        item.Validate();

        Directory.CreateDirectory(_root);
        var path = System.IO.Path.Combine(_root, eventId + ".json");
        var temporary = System.IO.Path.Combine(_root, eventId + ".partial");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path);
        return new StoredSourceOperationalEvent(item, path);
    }

    public IReadOnlyList<StoredSourceOperationalEvent> ReadPending(int maximum)
    {
        if (maximum <= 0) return [];
        Directory.CreateDirectory(_root);
        var pending = new List<StoredSourceOperationalEvent>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal))
        {
            try
            {
                var item = JsonSerializer.Deserialize<SourceOperationalEvent>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("来源事件为空。");
                item.Validate();
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), item.EventId, StringComparison.Ordinal) ||
                    !string.Equals(item.DeviceId, _deviceId, StringComparison.Ordinal))
                    throw new InvalidDataException("来源事件文件身份不一致。");
                pending.Add(new StoredSourceOperationalEvent(item, path));
                if (pending.Count == maximum) break;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Quarantine(path);
            }
        }
        return pending;
    }

    public void Acknowledge(string eventId)
    {
        SourceOperationalEvent.ValidateIdentifier(eventId, nameof(eventId));
        var path = System.IO.Path.Combine(_root, eventId + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public void Acknowledge(StoredSourceOperationalEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        stored.Event.Validate();
        var expected = Path.Combine(_root, stored.Event.EventId + ".json");
        if (!string.Equals(Path.GetFullPath(stored.Path), expected, StringComparison.Ordinal))
            throw new InvalidDataException("来源事件确认路径无效。");
        if (File.Exists(expected)) File.Delete(expected);
    }

    public void Quarantine(StoredSourceOperationalEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        stored.Event.Validate();
        var expected = Path.Combine(_root, stored.Event.EventId + ".json");
        if (!string.Equals(Path.GetFullPath(stored.Path), expected, StringComparison.Ordinal))
            throw new InvalidDataException("来源事件隔离路径无效。");
        Quarantine(expected);
    }

    private DateTimeOffset NextOccurredAtUtc()
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        while (true)
        {
            var previous = Interlocked.Read(ref _lastTicks);
            var next = Math.Max(ticks, previous + 1);
            if (Interlocked.CompareExchange(ref _lastTicks, next, previous) == previous)
                return new DateTimeOffset(next, TimeSpan.Zero);
        }
    }

    private void Quarantine(string path)
    {
        if (!File.Exists(path)) return;
        var quarantine = System.IO.Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        var name = System.IO.Path.GetFileName(path);
        var destination = System.IO.Path.Combine(quarantine, name);
        if (File.Exists(destination))
            destination = System.IO.Path.Combine(quarantine, name + "." + Guid.NewGuid().ToString("N"));
        try { File.Move(path, destination); }
        catch (FileNotFoundException) { }
    }
}
