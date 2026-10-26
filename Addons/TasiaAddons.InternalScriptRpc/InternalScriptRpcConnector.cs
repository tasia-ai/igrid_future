using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
    private readonly IUserAccountService userAccounts;
    private readonly int regionHttpsPortOffset;
    private readonly int minimumAdminLevel;
    private HttpClient client;
    private readonly bool loopbackOnly;

    public InternalScriptRpcConnector(IConfigSource config, IHttpServer server, string configName) : base(config, server, configName)
    {
        this.config = config;
        IConfig cfg = config?.Configs["InternalScriptRpc"];
        if (cfg?.GetBoolean("Enabled", false) != true) return;
        if (!server.UseSSL) throw new InvalidOperationException("InternalScriptRpc requires the Robust listener.UseSSL listener; refusing to register on plaintext HTTP.");
        loopbackOnly = cfg.GetBoolean("LoopbackOnly", false);
        if (loopbackOnly && !IPAddress.IsLoopback(server.ListenIPAddress))
            throw new InvalidOperationException("InternalScriptRpc LoopbackOnly is enabled but the Robust HTTPS listener is not bound to loopback.");
        client = RpcHttpClientFactory.Create(loopbackOnly);
        key = cfg.GetString("SigningKey", ""); RpcSecurity.ValidateSecret(key);
        owners = RpcSecurity.ParseOwners(cfg.GetString("AllowedOwners", ""));
        minimumAdminLevel = cfg.GetInt("MinimumAdminUserLevel", 150);
        if (minimumAdminLevel is < 1 or > 255)
            throw new InvalidOperationException("InternalScriptRpc MinimumAdminUserLevel must be 1-255.");
        regionHttpsPortOffset = cfg.GetInt("RegionHttpsPortOffset", 0);
        if (regionHttpsPortOffset is < 1 or > 60000)
            throw new InvalidOperationException("InternalScriptRpc RegionHttpsPortOffset must be configured (1-60000) when region ServerURI values use HTTP.");
        grid = LoadGrid(config);
        if (grid == null) throw new InvalidOperationException("InternalScriptRpc requires a configured GridService LocalServiceModule.");
        userAccounts = LoadUserAccounts(config);
        if (owners.Count == 0 && userAccounts is null)
            throw new InvalidOperationException("InternalScriptRpc requires AllowedOwners or a UserAccountService for admin-level authorization.");
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
                || !IsAuthorizedOwner(new UUID(request.Owner))) return Respond(403, "rejected");
            JsonElement destination = request.Payload.GetProperty("targetRegion");
            if (destination.ValueKind != JsonValueKind.String || !UUID.TryParse(destination.GetString(), out UUID targetId) || targetId == UUID.Zero) return Respond(400, "rejected");
            OpenSim.Services.Interfaces.GridRegion target = grid.GetRegionByUUID(UUID.Zero, targetId);
            // Keep grid/capability ServerURI untouched (it may be viewer-facing
            // HTTP). The RPC-only TLS listener uses a configured port offset.
            if (target?.ServerURI == null || !TryGetRegionRpcUri(target.ServerURI, targetId,
                    regionHttpsPortOffset, out Uri uri)) return Respond(502, "unavailable");
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
    private bool IsAuthorizedOwner(UUID owner)
    {
        int? userLevel = null;
        if (owner != UUID.Zero && userAccounts is not null)
        {
            try
            {
                UserAccount account = userAccounts.GetUserAccount(UUID.Zero, owner);
                if (account is not null) userLevel = account.UserLevel;
            }
            catch { /* A failed account lookup never grants admin-level access. */ }
        }
        return RpcSecurity.IsAuthorizedOwner(owner, owners, userLevel, minimumAdminLevel);
    }

    private static IUserAccountService LoadUserAccounts(IConfigSource config)
    {
        IConfig section = config?.Configs["UserAccountService"];
        string module = section?.GetString("LocalServiceModule", "");
        return string.IsNullOrWhiteSpace(module)
            ? null
            : ServerUtils.LoadPlugin<IUserAccountService>(module, new object[] { config });
    }
    internal static bool TryGetRegionRpcUri(string serverUri, UUID targetId, int httpsPortOffset,
        out Uri rpcUri)
    {
        rpcUri = null;
        if (targetId == UUID.Zero || httpsPortOffset is < 1 or > 60000
            || !Uri.TryCreate(serverUri, UriKind.Absolute, out Uri baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            return false;

        try
        {
            var builder = new UriBuilder(baseUri)
            {
                Scheme = Uri.UriSchemeHttps,
                Path = baseUri.AbsolutePath.TrimEnd('/') + "/internal-script-rpc/execute/" + targetId
            };
            if (baseUri.Scheme == Uri.UriSchemeHttp)
            {
                long tlsPort = (long)baseUri.Port + httpsPortOffset;
                if (tlsPort is < 1 or > 65535) return false;
                builder.Port = (int)tlsPort;
            }
            rpcUri = builder.Uri;
            return true;
        }
        catch { return false; }
    }
    private static string Header(Hashtable h, string key) { foreach (DictionaryEntry e in h) if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return e.Value?.ToString() ?? ""; return ""; }
    private static Hashtable Respond(int status, string body) => new() { ["int_response_code"] = status, ["content_type"] = "application/json", ["str_response_string"] = body, ["headers"] = new Hashtable { ["Cache-Control"] = "no-store" } };
}
