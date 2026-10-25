using System.Net;
using System.Net.Sockets;

namespace TasiaAddons.WebScripts;

public sealed class WebScriptDownloader : IDisposable
{
    private readonly WebScriptSecurity policy;
    private readonly HttpClient client;

    public WebScriptDownloader(WebScriptSecurity policy) : this(policy, CreateHandler(policy)) { }

    internal WebScriptDownloader(WebScriptSecurity policy, HttpMessageHandler handler)
    {
        this.policy = policy;
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static SocketsHttpHandler CreateHandler(WebScriptSecurity policy)
    {
        var handler = new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 2, MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        handler.ConnectCallback = async (context, cancellation) => {
            policy.ValidateUrl("https://" + context.DnsEndPoint.Host + "/");
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
            if (addresses.Length == 0 || addresses.Any(a => !WebScriptSecurity.IsPublicAddress(a)))
                throw new InvalidOperationException("Publisher DNS must resolve exclusively to public Internet addresses.");
            // Connect to the validated address, never resolve again. TLS still validates the original hostname.
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        return handler;
    }

    public async Task<string> DownloadAsync(string url, CancellationToken cancellation)
    {
        Uri uri = policy.ValidateUrl(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentLength > WebScriptSecurity.MaxEnvelopeBytes ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("Publisher must return an uncompressed, bounded JSON envelope with HTTP 200.");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await body.ReadAsync(chunk, timeout.Token)) > 0) {
            if (buffer.Length + count > WebScriptSecurity.MaxEnvelopeBytes) throw new InvalidOperationException("Envelope too large.");
            buffer.Write(chunk, 0, count);
        }
        return policy.VerifyEnvelope(buffer.ToArray(), uri, DateTimeOffset.UtcNow);
    }

    public void Dispose() => client.Dispose();
}
