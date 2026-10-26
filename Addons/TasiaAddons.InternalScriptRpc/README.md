# Internal Script RPC addon

This addon provides a synchronous MOD function, `maInternalScriptRpc(targetRegion, operation, payloadJson)`, which sends a bounded JSON operation through Robust and forwards it to the selected region. The function derives the caller owner from the live scene host; script arguments cannot provide an owner identity. Both the caller region and destination region, and Robust, must use the same 32-byte-minimum secret and matching non-empty owner UUID allowlists.

Enable the region module with the normal region addon discovery mechanism and configure:

```ini
[InternalScriptRpc]
    Enabled = true
    SigningKey = <shared-secret-of-at-least-32-UTF8-bytes>
    AllowedOwners = <owner-uuid>,<another-owner-uuid>
    RobustURL = https://robust.example.net
    HttpsPort = <already-configured-region-HTTPS-listener-port>
```

Load the connector in Robust:

```ini
[ServiceList]
    InternalScriptRpc = <RobustTlsPort>/TasiaAddons.InternalScriptRpc.dll:InternalScriptRpcConnector

[InternalScriptRpc]
    Enabled = true
    SigningKey = <same-shared-secret>
    AllowedOwners = <same-owner-uuid-list>
```

`RobustTlsPort` must be the port of an already configured, native TLS listener in Robust (for example, `[Network] https_listener=true`, `https_external=false`, `https_port=<port>`, and valid `cert_path`/`cert_pass`). The connector rejects a plaintext listener at startup. The `<port>/` prefix in `ServiceList` selects that listener; omitting it selects the default listener, which may be plaintext. External TLS termination alone is not accepted by this connector because OpenSim sees that backend listener as plaintext. Configure each region's `HttpsPort` to an already started, native OpenSim HTTPS listener; the addon selects that listener explicitly, so the Firestorm-facing HTTP listener can remain unchanged. The region HTTPS listener must be advertised in the grid as the region's HTTPS `ServerURI` and its certificate must be trusted and match that hostname. QUIC's certificate options configure QUIC transport only; they do not automatically configure OpenSim's HTTP/HTTPS listener. You may use the same certificate/key files for both listeners if valid for the HTTPS hostname, but configure them independently through each listener's supported settings. Both outbound hops use TLS 1.2 or TLS 1.3, standard platform trust-chain and hostname validation plus certificate revocation checking, and do not follow redirects or accept invalid certificates. Each hop has a 15-second transport timeout.

Operations are not discovered by reflection. A trusted region addon obtains `IInternalScriptRpcRegistry` from `scene.RequestModuleInterface<IInternalScriptRpcRegistry>()` and registers each destination operation with `RegisterOperation<TRequest,TResponse>(name, handler)` during region initialization. The target receives the authenticated owner UUID separately from typed JSON arguments. Unregistered names are rejected. The built-in `npc.transfer` operation is registered by the RPC module itself and is used by `osTeleportSmartNPC` and `osTeleportSmartNPCToRegion` whenever RPC is enabled; if RPC is not enabled in the source region, those functions retain the existing simulation-service transport. The maximum authenticated request/response body is 512 KiB. General handlers default to a two-second reply deadline; `npc.transfer` is allowed up to ten seconds. At most four handlers run concurrently. A synchronous handler that exceeds its deadline cannot be forcibly stopped and may finish after the caller receives a timeout; handlers must therefore be bounded and idempotent where practical. No generic method invocation, source evaluation, or CLR type-name deserialization is available.

Example script (MOD functions must be enabled by the region's normal OSSL policy):

```lsl
string result = maInternalScriptRpc("<region-uuid>", "inventory.lookup", "{\"item\":\"example\"}");
```

Build with `dotnet build Addons/TasiaAddons.InternalScriptRpc/TasiaAddons.InternalScriptRpc.csproj`. The source build does not establish that a particular host loads the addon or that live Robust/region TLS routing works; run a host-level integration test before deployment.
