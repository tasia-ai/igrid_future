using System.Collections;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace TasiaAddons.InternalScriptRpc;

/// <summary>Region endpoint and MOD-callable synchronous RPC client.</summary>
public sealed class InternalScriptRpcModule : INonSharedRegionModule, IInternalScriptRpcRegistry, IInternalScriptRpcClient
{
    private readonly ConcurrentDictionary<string, long> nonces = new();
    private sealed record RegisteredOperation(Func<UUID, JsonElement, JsonElement> Handler, TimeSpan Timeout);

    private readonly ConcurrentDictionary<string, RegisteredOperation> handlers = new(StringComparer.Ordinal);
    // Keep normal OS certificate-chain and hostname validation. Never install a
    // callback that accepts arbitrary/self-signed certificates for this channel.
    private readonly HttpClient client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private HashSet<UUID> owners = new();
    private Scene scene;
    private IScriptModuleComms comms;
    private string key, robustUrl;
    private uint httpsPort;
    private IHttpServer endpointServer;
    private string endpointPath;
    private bool enabled;
    private readonly object gate = new();
    private readonly SemaphoreSlim handlerSlots = new(4, 4);
    public string Name => "InternalScriptRpcModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig cfg = source.Configs["InternalScriptRpc"];
        if (cfg?.GetBoolean("Enabled", false) != true) return;
        key = cfg.GetString("SigningKey", ""); RpcSecurity.ValidateSecret(key);
        owners = RpcSecurity.ParseOwners(cfg.GetString("AllowedOwners", ""));
        if (owners.Count == 0) throw new InvalidOperationException("InternalScriptRpc requires a non-empty AllowedOwners UUID allowlist.");
        robustUrl = cfg.GetString("RobustURL", "");
        if (!Uri.TryCreate(robustUrl, UriKind.Absolute, out Uri robust) || robust.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(robust.UserInfo) || !string.IsNullOrEmpty(robust.Fragment))
            throw new InvalidOperationException("InternalScriptRpc RobustURL must be an absolute HTTPS URL without userinfo or fragment.");
        int configuredPort = cfg.GetInt("HttpsPort", 0);
        if (configuredPort is < 1 or > 65535)
            throw new InvalidOperationException("InternalScriptRpc HttpsPort must identify an already configured HTTPS listener port.");
        httpsPort = (uint)configuredPort;
        enabled = true;
    }

    public void AddRegion(Scene value) { if (enabled) scene = value; }
    public void RegionLoaded(Scene value)
    {
        if (!enabled) return;
        // Firestorm-facing HTTP can remain plaintext. Bind this endpoint to the
        // separately configured HTTPS listener instead of assuming DefaultServer
        // is TLS (it often is not when the viewer listener is HTTP).
        IMainServer mainServer = MainServer.Instance;
        if (mainServer is null || !mainServer.ContainsHttpServer(httpsPort))
            throw new InvalidOperationException($"InternalScriptRpc HTTPS listener on port {httpsPort} is not configured and started.");
        IHttpServer server = mainServer.GetHttpServer(httpsPort)
            ?? throw new InvalidOperationException("InternalScriptRpc could not resolve the configured HTTPS listener.");
        if (!server.UseSSL) throw new InvalidOperationException("InternalScriptRpc requires the region listener.UseSSL listener; refusing to register on plaintext HTTP.");
        string path = $"/internal-script-rpc/execute/{value.RegionInfo.RegionID}";
        if (!server.AddHTTPHandler(path, Handle))
            throw new InvalidOperationException("InternalScriptRpc region endpoint is already registered for this region.");

        try
        {
            IScriptModuleComms scriptComms = value.RequestModuleInterface<IScriptModuleComms>()
                ?? throw new InvalidOperationException("InternalScriptRpc requires ScriptModuleComms.");
            RegisterOperation<NpcTransferRpcRequest, NpcTransferRpcResponse>("npc.transfer",
                (rpcOwner, request) => ReceiveNpcTransfer(value, rpcOwner, request), TimeSpan.FromSeconds(10));
            value.RegisterModuleInterface<IInternalScriptRpcRegistry>(this);
            value.RegisterModuleInterface<IInternalScriptRpcClient>(this);
            scriptComms.RegisterScriptInvocations(this);
            lock (gate)
            {
                endpointServer = server;
                endpointPath = path;
                comms = scriptComms;
            }
        }
        catch
        {
            value.UnregisterModuleInterface<IInternalScriptRpcRegistry>(this);
            value.UnregisterModuleInterface<IInternalScriptRpcClient>(this);
            server.RemoveHTTPHandler(string.Empty, path);
            handlers.TryRemove("npc.transfer", out _);
            throw;
        }
    }
    public void RemoveRegion(Scene value)
    {
        value.UnregisterModuleInterface<IInternalScriptRpcRegistry>(this);
        value.UnregisterModuleInterface<IInternalScriptRpcClient>(this);
        lock (gate)
        {
            endpointServer?.RemoveHTTPHandler(string.Empty, endpointPath);
            endpointServer = null;
            endpointPath = null;
            handlers.Clear();
            comms = null;
            if (ReferenceEquals(scene, value)) scene = null;
        }
    }
    public void Close()
    {
        lock (gate)
        {
            enabled = false;
            endpointServer?.RemoveHTTPHandler(string.Empty, endpointPath);
            endpointServer = null;
            endpointPath = null;
            handlers.Clear();
            scene = null;
            comms = null;
        }
        client.Dispose();
    }

    /// <summary>Expose only explicitly registered typed operations; no reflection or dynamic code execution.</summary>
    public void RegisterOperation<TRequest, TResponse>(string name, Func<UUID, TRequest, TResponse> handler,
        TimeSpan? executionTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) throw new ArgumentException("Invalid RPC operation name.", nameof(name));
        ArgumentNullException.ThrowIfNull(handler);
        TimeSpan timeout = executionTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(executionTimeout), "RPC handler timeout must be within 1-10 seconds.");
        var registered = new RegisteredOperation(
            (owner, payload) => JsonSerializer.SerializeToElement(handler(owner, payload.Deserialize<TRequest>())), timeout);
        if (!handlers.TryAdd(name, registered)) throw new InvalidOperationException("RPC operation already registered: " + name);
    }

    private static NpcTransferRpcResponse ReceiveNpcTransfer(Scene destination, UUID owner,
        NpcTransferRpcRequest request)
    {
        if (destination is null || owner.IsZero() || request is null || string.IsNullOrEmpty(request.PackedNpcJson))
            return new NpcTransferRpcResponse { Success = false, Reason = "Invalid NPC transfer request" };

        try
        {
            if (OSDParser.DeserializeJson(request.PackedNpcJson) is not OSDMap packed)
                return new NpcTransferRpcResponse { Success = false, Reason = "Invalid NPC state payload" };
            var npc = new NpcAgentData();
            if (!npc.Unpack(packed))
                return new NpcTransferRpcResponse { Success = false, Reason = "NPC state payload could not be decoded" };
            if (!npc.OwnerID.IsZero() && npc.OwnerID != owner && npc.AgentID != owner)
                return new NpcTransferRpcResponse { Success = false, Reason = "RPC owner cannot transfer this NPC" };

            bool success = destination.IncomingCreateNpcAgent(npc, out string reason);
            return new NpcTransferRpcResponse { Success = success, Reason = reason };
        }
        catch (Exception e)
        {
            return new NpcTransferRpcResponse { Success = false, Reason = "NPC transfer handler failed: " + e.Message };
        }
    }

    [ScriptInvocation]
    public string maInternalScriptRpc(UUID hostID, UUID scriptID, string targetRegion, string operation, string payloadJson)
    {
        Scene current; UUID owner;
        lock (gate) { if (!enabled || scene == null) throw new InvalidOperationException("InternalScriptRpc is disabled."); current = scene; }
        SceneObjectPart host = current.GetSceneObjectPart(hostID);
        TaskInventoryItem script = host?.Inventory.GetInventoryItem(scriptID);
        if (host?.ParentGroup == null || host.ParentGroup.IsDeleted || host.ParentGroup.inTransit || script == null || script.Type != (int)AssetType.LSLText || script.OwnerID != host.OwnerID)
            throw new InvalidOperationException("InternalScriptRpc caller could not be resolved.");
        owner = host.OwnerID;
        if (owner == UUID.Zero || !owners.Contains(owner)) throw new InvalidOperationException("InternalScriptRpc caller owner is not allowed.");
        if (!UUID.TryParse(targetRegion, out UUID region) || region == UUID.Zero) throw new ArgumentException("targetRegion must be a nonzero region UUID.");
        return InvokeForOwner(owner, region, operation, payloadJson);
    }

    public string InvokeForOwner(UUID owner, UUID region, string operation, string payloadJson)
    {
        if (!enabled || owner == UUID.Zero || !owners.Contains(owner))
            throw new InvalidOperationException("InternalScriptRpc owner is not allowed.");
        if (region == UUID.Zero) throw new ArgumentException("targetRegion must be a nonzero region UUID.");
        if (operation?.Length is < 1 or > 64) throw new ArgumentException("Invalid operation name.");
        if (Encoding.UTF8.GetByteCount(payloadJson ?? "null") > RpcSecurity.MaxBody) throw new ArgumentException("RPC request is too large.");
        JsonElement args;
        try { args = JsonDocument.Parse(payloadJson ?? "null", new JsonDocumentOptions { MaxDepth = 12 }).RootElement.Clone(); }
        catch { throw new ArgumentException("payloadJson must be valid bounded JSON."); }
        JsonElement payload = JsonSerializer.SerializeToElement(new { targetRegion = region.ToString(), arguments = args });
        string body = RpcSecurity.CreateRequest(operation, owner, payload, out string timestamp, out string nonce, out string requestId);
        if (Encoding.UTF8.GetByteCount(body) > RpcSecurity.MaxBody) throw new ArgumentException("RPC request is too large.");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, robustUrl.TrimEnd('/') + "/internal-script-rpc/v1") { Content = content };
        request.Headers.Add("x-isrpc-timestamp", timestamp); request.Headers.Add("x-isrpc-nonce", nonce); request.Headers.Add("x-isrpc-signature", RpcSecurity.Sign(key, timestamp, nonce, body));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
        byte[] bytes = RpcSecurity.ReadBoundedAsync(response.Content, RpcSecurity.MaxBody, timeout.Token).GetAwaiter().GetResult();
        if (bytes.Length > RpcSecurity.MaxBody || !response.IsSuccessStatusCode) throw new InvalidOperationException("InternalScriptRpc transport rejected the request.");
        string responseBody = Encoding.UTF8.GetString(bytes);
        string rt = Header(response.Headers, "x-isrpc-timestamp"), rn = Header(response.Headers, "x-isrpc-nonce"), mac = Header(response.Headers, "x-isrpc-signature");
        if (!long.TryParse(rt, out long timestampValue) || !RpcSecurity.TimestampValid(timestampValue)
            || rn?.Length is < 32 or > 96 || !RpcSecurity.Verify(key, rt, rn, responseBody, mac)
            || !RpcSecurity.ConsumeNonce(nonces, rn)) throw new InvalidOperationException("InternalScriptRpc response authentication failed.");
        using JsonDocument result = JsonDocument.Parse(responseBody);
        if (result.RootElement.GetProperty("requestId").GetString() != requestId) throw new InvalidOperationException("InternalScriptRpc response request ID mismatch.");
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("InternalScriptRpc remote operation failed: " + result.RootElement.GetProperty("code").GetString());
        return result.RootElement.GetProperty("payload").GetRawText();
    }

    private Hashtable Handle(Hashtable input)
    {
        string body = input?["body"] as string ?? input?["requestbody"] as string ?? "";
        try
        {
            if (!string.Equals(input?["http-method"]?.ToString(), "POST", StringComparison.OrdinalIgnoreCase) || Encoding.UTF8.GetByteCount(body) is 0 or > RpcSecurity.MaxBody) return Respond(400, "rejected");
            Hashtable headers = input["headers"] as Hashtable ?? new();
            string ts = Header(headers, "x-isrpc-timestamp"), nonce = Header(headers, "x-isrpc-nonce"), mac = Header(headers, "x-isrpc-signature");
            if (!long.TryParse(ts, out long timestamp) || !RpcSecurity.TimestampValid(timestamp) || nonce.Length is < 32 or > 96 || !RpcSecurity.Verify(key, ts, nonce, body, mac) || !RpcSecurity.ConsumeNonce(nonces, nonce)) return Respond(401, "rejected");
            if (!RpcSecurity.TryParseRequest(body, out RpcRequest rpc)
                || rpc.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) != ts
                || rpc.Nonce != nonce
                || !UUID.TryParse(rpc.Owner, out UUID owner)
                || !owners.Contains(owner)) return Respond(403, "rejected");
            JsonElement target = rpc.Payload.GetProperty("targetRegion");
            if (!UUID.TryParse(target.GetString(), out UUID region) || scene?.RegionInfo.RegionID != region) return Respond(404, "rejected");
            if (!handlers.TryGetValue(rpc.Operation, out RegisteredOperation registered)) return SignedResult(400, rpc.RequestId, false, "unknown_operation", RpcSecurity.Json(""));
            JsonElement args = rpc.Payload.GetProperty("arguments");
            if (!handlerSlots.Wait(0)) return SignedResult(429, rpc.RequestId, false, "busy", RpcSecurity.Json(""));
            Task<JsonElement> execution = Task.Run(() => { try { return registered.Handler(owner, args); } finally { handlerSlots.Release(); } });
            if (!execution.Wait(registered.Timeout)) return SignedResult(504, rpc.RequestId, false, "handler_timeout", RpcSecurity.Json(""));
            try { return SignedResult(200, rpc.RequestId, true, "ok", execution.GetAwaiter().GetResult()); }
            catch { return SignedResult(500, rpc.RequestId, false, "handler_error", RpcSecurity.Json("")); }
        }
        catch { return Respond(400, "rejected"); }
    }

    private Hashtable SignedResult(int status, string requestId, bool ok, string code, JsonElement payload)
    {
        string body = JsonSerializer.Serialize(new RpcResponse(requestId, ok, code, payload));
        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        return new Hashtable { ["int_response_code"] = status, ["content_type"] = "application/json", ["str_response_string"] = body,
            ["headers"] = new Hashtable { ["x-isrpc-timestamp"] = ts, ["x-isrpc-nonce"] = nonce, ["x-isrpc-signature"] = RpcSecurity.Sign(key, ts, nonce, body), ["Cache-Control"] = "no-store" } };
    }
    private static Hashtable Respond(int status, string body) => new() { ["int_response_code"] = status, ["content_type"] = "text/plain", ["str_response_string"] = body };
    private static string Header(Hashtable headers, string name) { foreach (DictionaryEntry entry in headers) if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return entry.Value?.ToString() ?? ""; return ""; }
    private static string Header(HttpResponseHeaders headers, string name) => headers.TryGetValues(name, out IEnumerable<string> values) ? values.SingleOrDefault() ?? "" : "";
}
