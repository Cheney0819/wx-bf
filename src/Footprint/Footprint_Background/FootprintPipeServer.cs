using System.IO.Pipes;
using System.Text.Json;
using Footprint.Core.Ipc;

namespace Footprint.Background;

public sealed class FootprintPipeServer
{
    private static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;
    private readonly bool _allowTestShutdown;
    private readonly TimeSpan _sessionTimeout;

    public FootprintPipeServer(string pipeName, bool allowTestShutdown, TimeSpan? sessionTimeout = null)
    {
        _pipeName = pipeName;
        _allowTestShutdown = allowTestShutdown;
        _sessionTimeout = sessionTimeout ?? DefaultSessionTimeout;
        if (_sessionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sessionTimeout));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                if (await HandleConnectionAsync(pipe, cancellationToken)) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<bool> HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.CancelAfter(_sessionTimeout);
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(session.Token);
            if (line is null) return false;

            var stop = false;
            FootprintPipeResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<FootprintPipeRequest>(line);
                response = request?.Type switch
                {
                    "ping" => new FootprintPipeResponse("running", "后台正在运行"),
                    "test_shutdown" when _allowTestShutdown => StopResponse(out stop),
                    "test_shutdown" => new FootprintPipeResponse("rejected", "当前运行模式禁止测试停机。"),
                    _ => new FootprintPipeResponse("rejected", "后台收到未知请求。")
                };
            }
            catch (JsonException)
            {
                response = new FootprintPipeResponse("rejected", "后台收到无效请求。");
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response)).WaitAsync(session.Token);
            return stop;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static FootprintPipeResponse StopResponse(out bool stop)
    {
        stop = true;
        return new FootprintPipeResponse("stopping", "测试后台正在停止。");
    }
}
