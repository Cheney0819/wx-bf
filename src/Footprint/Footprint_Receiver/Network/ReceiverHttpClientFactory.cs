namespace Footprint.Receiver.Network;

public static class ReceiverHttpClientFactory
{
    public static HttpClient Create(ReceiverNetworkTimeouts? timeouts = null) => Configure(new HttpClient(), timeouts);

    public static HttpClient Create(HttpMessageHandler handler, ReceiverNetworkTimeouts? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Configure(new HttpClient(handler, disposeHandler: true), timeouts);
    }

    private static HttpClient Configure(HttpClient client, ReceiverNetworkTimeouts? timeouts)
    {
        client.Timeout = (timeouts ?? ReceiverNetworkTimeouts.FromEnvironment()).RequestTimeout;
        return client;
    }
}
