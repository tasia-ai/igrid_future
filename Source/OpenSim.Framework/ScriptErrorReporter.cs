using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Net;

namespace OpenSim.Framework;

/// <summary>Best-effort, bounded asynchronous publisher for sanitized runtime script errors.</summary>
public static class ScriptErrorReporter
{
    private const int QueueCapacity = 1024;
    private static readonly Channel<Report> Queue = Channel.CreateBounded<Report>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static ScriptErrorReporter() => _ = Task.Run(DrainAsync);

    /// <summary>Queues a runtime error for signed delivery. Empty URL/key disables reporting.</summary>
    public static bool TryReport(string endpoint, string signingKey, string region, string engine, string script, string message)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || Encoding.UTF8.GetByteCount(signingKey ?? string.Empty) < 32) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri)) return false;
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp &&
            (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(uri.Host, out IPAddress address) && IPAddress.IsLoopback(address)));
        if (!https && !loopbackHttp) return false;
        var row = new Payload(Clean(region, 128), Clean(engine, 32), Clean(script, 128), Clean(message, 1024));
        return Queue.Writer.TryWrite(new Report(uri, signingKey, row));
    }

    private static async Task DrainAsync()
    {
        await foreach (Report report in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                string body = JsonSerializer.Serialize(report.Payload, JsonOptions);
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                byte[] signed = Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n" + body);
                byte[] signature;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(report.Key))) signature = hmac.ComputeHash(signed);
                using var request = new HttpRequestMessage(HttpMethod.Post, report.Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Script-Errors-Timestamp", timestamp);
                request.Headers.Add("X-Script-Errors-Nonce", nonce);
                request.Headers.Add("X-Script-Errors-Signature", Convert.ToHexString(signature).ToLowerInvariant());
                request.Headers.Add("X-Script-Errors-Client", Environment.MachineName);
                using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(false);
            }
            catch { /* telemetry must never affect script execution */ }
        }
    }

    private static string Clean(string value, int max) => new((value ?? string.Empty).Where(c => !char.IsControl(c) || c == '\t').Take(max).ToArray());
    private sealed record Payload(string Region, string Engine, string Script, string Message);
    private sealed record Report(Uri Endpoint, string Key, Payload Payload);
}
