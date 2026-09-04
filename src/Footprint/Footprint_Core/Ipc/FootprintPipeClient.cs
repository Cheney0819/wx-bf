using System.IO.Pipes;
using System.Text.Json;

namespace Footprint.Core.Ipc;

public sealed class FootprintPipeClient(string pipeName)
{
    public Task<FootprintPipeResponse> PingAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync("ping", timeout, cancellationToken);

    public async Task ShutdownForTestAsync(CancellationToken cancellationToken) =>
        _ = await SendAsync("test_shutdown", TimeSpan.FromSeconds(3), cancellationToken);

    private async Task<FootprintPipeResponse> SendAsync(string type, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        IOException? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await SendOnceAsync(type, linked.Token);
            }
            catch (IOException error) when (attempt < 2)
            {
                lastError = error;
                await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token);
            }
        }

        throw lastError ?? new IOException("命名管道请求失败。");
    }

    private async Task<FootprintPipeResponse> SendOnceAsync(string type, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new FootprintPipeRequest(type)));
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
        return JsonSerializer.Deserialize<FootprintPipeResponse>(line) ?? throw new InvalidDataException();
    }
}
