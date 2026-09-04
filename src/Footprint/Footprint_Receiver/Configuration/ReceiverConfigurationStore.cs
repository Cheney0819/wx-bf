using System.Text.Json;
using Footprint.Receiver.Internal;

namespace Footprint.Receiver.Configuration;

public interface IReceiverConfigurationStore
{
    ValueTask<ReceiverOptions?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(ReceiverOptions options, CancellationToken cancellationToken = default);
}

public sealed class ReceiverConfigurationStore(string path) : IReceiverConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Deskmate Footprint", "receiver.json");

    public async ValueTask<ReceiverOptions?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return null;
        await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<StoredOptions>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("接收端配置为空。");
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.ServerUri) || string.IsNullOrWhiteSpace(document.DeviceId) || string.IsNullOrWhiteSpace(document.DisplayName))
            throw new InvalidDataException("接收端配置不完整。");
        return new ReceiverOptions(document.SchemaVersion, new Uri(document.ServerUri, UriKind.Absolute), document.DeviceId, document.DisplayName, TimeSpan.FromSeconds(document.PollIntervalSeconds));
    }

    public async ValueTask SaveAsync(ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("配置路径缺少父目录。");
        UnixDurability.SecureDirectory(directory);
        var partial = Path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                UnixDurability.SecureFile(partial);
                var stored = new StoredOptions(1, options.ServerUri.AbsoluteUri.TrimEnd('/'), options.DeviceId, options.DisplayName, checked((int)options.PollInterval.TotalSeconds));
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(partial, Path, true);
            UnixDurability.SecureFile(Path);
            UnixDurability.FlushDirectory(directory);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private sealed record StoredOptions(int SchemaVersion, string ServerUri, string DeviceId, string DisplayName, int PollIntervalSeconds);
}
