using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Footprint.Core.Runtime;

namespace Footprint.Background;

public sealed class SourceEventForwarder(
    HttpClient http,
    Uri server,
    string token,
    SourceEventOutbox outbox,
    TimeSpan interval)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Uri _server = server ?? throw new ArgumentNullException(nameof(server));
    private readonly string _token = token ?? throw new ArgumentNullException(nameof(token));
    private readonly SourceEventOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly TimeSpan _interval = interval;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await FlushOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { }
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var pending in _outbox.ReadPending(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_server, "api/footprint/source-events"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(pending.Event);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                _outbox.Acknowledge(pending);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                _outbox.Quarantine(pending);
                continue;
            }

            return;
        }
    }
}
