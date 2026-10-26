using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.InternalScriptRpc;

/// <summary>Robust-side authenticated relay to explicitly registered destination-region operations.</summary>
public sealed class InternalScriptRpcConnector : ServiceConnector
{
    private const string Path = "/internal-script-rpc/v1";
    private readonly string key;
    private readonly HashSet<UUID> owners;
    private readonly IConfigSource config;
    private readonly ConcurrentDictionary<string, long> nonces = new();
    private readonly IGridService grid;
    // Use ordinary PKI validation (including hostname checks), with no
    // certificate-bypass callback. RPC forwarding is HTTPS-only.
    private readonly HttpClient client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public InternalScriptRpcConnector(IConfigSource config, IHttpServer server, string configName) : base(config, server, configName)
    {
        this.config = config;
        IConfig cfg = config?.Configs["InternalScriptRpc"];
        if (cfg?.GetBoolean("Enabled", false) != true) return;
        if (!server.UseSSL) throw new InvalidOperationException("InternalScriptRpc requires the Robust listener.UseSSL listener; refusing to register on plaintext HTTP.");
        key = cfg.GetString("SigningKey", ""); RpcSecurity.ValidateSecret(key);
        owners = RpcSecurity.ParseOwners(cfg.GetString("AllowedOwners", ""));
        if (owners.Count == 0) throw new InvalidOperationException("InternalScriptRpc requires a non-empty AllowedOwners UUID allowlist.");
        grid = LoadGrid(config);
        if (grid == null) throw new InvalidOperationException("InternalScriptRpc requires a configured GridService LocalServiceModule.");
        if (!server.AddHTTPHandler(Path, Handle))
            throw new InvalidOperationException("InternalScriptRpc Robust endpoint is already registered.");
    }

    private Hashtable Handle(Hashtable input)
    {
        try
        {
            if (!string.Equals(input?["http-method"]?.ToString(), "POST", StringComparison.OrdinalIgnoreCase)) return Respond(405, "rejected");
            string body = input["body"] as string ?? input["requestbody"] as string ?? "";
            if (Encoding.UTF8.GetByteCount(body) is 0 or > RpcSecurity.MaxBody) return Respond(413, "rejected");
            Hashtable headers = input["headers"] as Hashtable ?? new();
            string ts = Header(headers, "x-isrpc-timestamp"), nonce = Header(headers, "x-isrpc-nonce"), mac = Header(headers, "x-isrpc-signature");
            if (!long.TryParse(ts, out long timestamp) || !RpcSecurity.TimestampValid(timestamp) || nonce.Length is < 32 or > 96 || !RpcSecurity.Verify(key, ts, nonce, body, mac) || !RpcSecurity.ConsumeNonce(nonces, nonce)) return Respond(401, "rejected");
            if (!RpcSecurity.TryParseRequest(body, out RpcRequest request)
                || request.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) != ts
                || request.Nonce != nonce
                || !owners.Contains(new UUID(request.Owner))) return Respond(403, "rejected");
            JsonElement destination = request.Payload.GetProperty("targetRegion");
            if (destination.ValueKind != JsonValueKind.String || !UUID.TryParse(destination.GetString(), out UUID targetId) || targetId == UUID.Zero) return Respond(400, "rejected");
            OpenSim.Services.Interfaces.GridRegion target = grid.GetRegionByUUID(UUID.Zero, targetId);
            // The target is intentionally taken from a dedicated payload member, not operation data.
            if (target?.ServerURI == null || !Uri.TryCreate(target.ServerURI.TrimEnd('/') + "/internal-script-rpc/execute/" + targetId, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return Respond(502, "unavailable");
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            message.Headers.Add("x-isrpc-timestamp", ts); message.Headers.Add("x-isrpc-nonce", nonce); message.Headers.Add("x-isrpc-signature", mac);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using HttpResponseMessage result = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
            byte[] bytes = RpcSecurity.ReadBoundedAsync(result.Content, RpcSecurity.MaxBody, timeout.Token).GetAwaiter().GetResult();
            if (bytes.Length > RpcSecurity.MaxBody || result.StatusCode != HttpStatusCode.OK) return Respond(502, "unavailable");
            string response = Encoding.UTF8.GetString(bytes);
            string rts = result.Headers.TryGetValues("x-isrpc-timestamp", out var tv) ? tv.SingleOrDefault() : null;
            string rn = result.Headers.TryGetValues("x-isrpc-nonce", out var nv) ? nv.SingleOrDefault() : null;
            string rm = result.Headers.TryGetValues("x-isrpc-signature", out var mv) ? mv.SingleOrDefault() : null;
            if (!long.TryParse(rts, out long rt) || !RpcSecurity.TimestampValid(rt)
                || rn?.Length is < 32 or > 96 || !RpcSecurity.Verify(key, rts, rn, response, rm)
                || !RpcSecurity.ConsumeNonce(nonces, rn)) return Respond(502, "unavailable");
            using JsonDocument parsed = JsonDocument.Parse(response);
            if (parsed.RootElement.GetProperty("requestId").GetString() != request.RequestId) return Respond(502, "unavailable");
            Hashtable outputHeaders = new() { ["Cache-Control"] = "no-store", ["x-isrpc-timestamp"] = rts, ["x-isrpc-nonce"] = rn, ["x-isrpc-signature"] = rm };
            return new Hashtable { ["int_response_code"] = 200, ["content_type"] = "application/json", ["str_response_string"] = response, ["headers"] = outputHeaders };
        }
        catch { return Respond(400, "rejected"); }
    }

    private static IGridService LoadGrid(IConfigSource config)
    {
        IConfig section = config?.Configs["GridService"];
        string module = section?.GetString("LocalServiceModule", "");
        return string.IsNullOrWhiteSpace(module) ? null : ServerUtils.LoadPlugin<IGridService>(module, new object[] { config });
    }
    private static string Header(Hashtable h, string key) { foreach (DictionaryEntry e in h) if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return e.Value?.ToString() ?? ""; return ""; }
    private static Hashtable Respond(int status, string body) => new() { ["int_response_code"] = status, ["content_type"] = "application/json", ["str_response_string"] = body, ["headers"] = new Hashtable { ["Cache-Control"] = "no-store" } };
}
