# Internal synchronous script RPC: implementation gap and safe design

## Status

Implemented in `Addons/TasiaAddons.InternalScriptRpc` as a Robust connector and
region module. The addon builds and its six focused security tests pass. The
host-level Robust-to-region TLS integration test has not been run; this is source
code, not a verified live deployment. See the addon's README for configuration
and the `maInternalScriptRpc` call syntax.

The region plugin resolves the script item and host from scene state and checks
the host owner's configured UUID allowlist. Robust repeats that allowlist check,
routes only to the requested registered grid region, and the destination invokes
only a specifically registered typed operation. SmartNPC teleports use the
built-in `npc.transfer` operation when RPC is enabled at the source; otherwise
they retain the existing simulation-service transport. HTTPS listeners are required;
the client rejects non-HTTPS URLs, redirects are disabled, and normal certificate
validation remains enabled. Region modules bind to the explicitly configured
`InternalScriptRpc.HttpsPort`, require an already-started TLS listener, and remove
their route when the region unloads. Robust's `ServiceList` entry must use the
numeric port prefix of an already-configured native TLS listener; the connector
rejects plaintext listeners, including external TLS-termination backends. Grid
`ServerURI` values must advertise the regions' trusted HTTPS hostname and port.
RPC uses each OpenSim HTTP listener's certificate settings; QUIC certificate
settings are separate. Firestorm's viewer-facing HTTP listener can remain HTTP.

## Protocol and verification limits

The addon uses a fixed JSON request envelope and HMAC-SHA256 over the exact UTF-8
body plus timestamp and nonce headers. It enforces a short clock-skew window,
atomically consumes request nonces, caps payloads/concurrency/timeouts, signs
responses, disables redirects, and rejects plaintext URLs/listeners. The region
operator must configure the same high-entropy secret and owner UUID allowlist on
each participating region and Robust. No secret is committed or included in the
README.

Before deployment, configure the addon and run a real Robust-to-region request
through the intended TLS listeners. Confirm discovery loads both plugins and
that destination region `ServerURI` values point at their TLS-enabled listener.
The six tests cover HMAC/body binding, time-window rejection, concurrent nonce
replay, strict request-envelope fields, and fail-closed allowlist/secret parsing;
they do not replace that host-level integration test. A synchronous registered
general handler that exceeds its two-second response deadline (ten seconds for
the SmartNPC transfer handler) is not forcibly cancelled and may complete after
a timeout is returned, so handler authors must bound work and use idempotent
operations where practical.
