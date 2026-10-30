# QUIC: register simulator circuits before the viewer arrives

## Failure signature

The Tasia viewer establishes QUIC to ROBUST (UDP/9001), then ROBUST
closes the connection with application error 0 a few seconds later.
In the native proxy a missing circuit route is given only a two-second
registration grace period before it closes the viewer stream.

Previously the simulator issued its POST /admin/quic/circuit/register
only from OnQuicCircuitCreated, which occurs after UseCircuitCode
arrives. The proxy cannot forward that first packet without a route.

## Changes

- Scene.NewUserConnection triggers an event after authorization and
  circuit installation, before returning to the caller.
- QuicServerModule subscribes once its native listener is active and
  POSTs its actual simulator QUIC port (and LLUDP fallback) to ROBUST
  at the new event.
- **Teleport handoff:** `AgentCircuitData.child` is true at the destination
  during `CreateAgent`, just like a neighbouring child. Instead of treating
  every `child=true` registration as a neighbour, forward the scene's
  `teleportFlags`. A destination with non-default teleport flags is
  registered as the new `root` route candidate. Ordinary neighbours with
  Default flags remain `child` and cannot overwrite the active route.
  Without this distinction ROBUST's child-route-flip guard silently keeps
  the **source** simulator route and QUIC can fail during teleport.
- LLLoginService pre-registers a local login circuit's LLUDP route
  directly in ROBUST's in-process registry after LaunchAgentAtGrid
  succeeds, but only if [QuicProxy] Enabled and the local destination
  advertises QUIC. This is a fallback if the region POST is delayed.
- The existing late OnQuicCircuitCreated registration still runs.

No HTTP/HTTPS listener changes, no inventory Export changes, and no
new ports. The native frontend QUIC port stays UDP/9001; each region
keeps its configured native QUIC port, e.g. 9002+.

## Build / test (run in deployment environment)

    dotnet test Tests/OpenSim.Server.Handlers.Tests/OpenSim.Server.Handlers.Tests.csproj --filter QuicLoginPreRegistrationTests
    dotnet test Tests/OpenSim.Region.Framework.Tests/OpenSim.Region.Framework.Tests.csproj --filter AuthorizedAgentCircuitTests

Rebuild and deploy the corresponding ROBUST and **all** simulator binaries
from the same source revision; a partial deployment cannot test the
early simulator event. Restart ROBUST and simulators in the standard
controlled maintenance window.

At a successful login, look for:

    [QuicServer] Pre-registering authorized root circuit
    [QuicServer] Circuit ... registered with proxy
    [QuicProxy] Circuit ... registered -> QUIC
    [QuicServer] Pre-registering authorized root circuit ... (child=True, teleportFlags=nonzero)
    [QuicProxy] Bridging circuit ... to sim QUIC
    [LLUDPSERVER] Sent quicready on native QUIC circuit ...

If the proxy instead reports "No QUIC route ... after wait", compare
the region's ProxyRegistrationURL to ROBUST's internal HTTP endpoint,
and check the region's HTTP POST result. The expected local handler
probe is HTTP 400 "Missing circuitCode" for body '{}' at
http://127.0.0.1:8003/admin/quic/circuit/register.

## Limits / safety

The local LLUDP route is registered even if a non-QUIC client uses a
QUIC-enabled destination; the existing registry lifecycle should
therefore be monitored for lingering circuits. Root/child route-flip
protections remain in place and teleport handoffs require a live
multi-region test. The simulator's registered hostname must be
resolvable and reachable by ROBUST on the native simulator QUIC/UDP port.

The changes are unverified until compiled and tested with a live
viewer. Do not publish the internal admin HTTP endpoint to the Internet.

## Teleport regression checks

1. Log in via QUIC to region A. Verify the A route is registered as root.
2. Create a neighbour child in region B with Default teleport flags: B
   must not steal A's registered QUIC route.
3. Teleport A -> B: B initially receives circuit.child=true, but the
   teleport flags are non-default. Its pre-registration **must** be
   root and overwrite the A endpoint before viewer UseCircuitCode.
4. The new connection must reach the B native QUIC endpoint and
   complete its quicready handshake. Old A bridge cleanup must not
   unregister B's route.
5. Repeat B -> A, non-neighbour teleport, a failed/cancelled teleport,
   and cross-region movement. Check for stale routes after cancellation.

The observed post-teleport LLUDP fallback is not itself a fix.
Do not alter the viewer transport preference or force LLUDP to mask
a stale route. Use destination and ROBUST logs to verify the handoff.
