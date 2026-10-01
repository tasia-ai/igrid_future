# CreateAgent / UpdateAgent — full transaction trace

**Date:** 2026-09-28 · **No code modified.**

---

## A. Exact CreateAgent / UpdateAgent call graph (outgoing, source side)

```
EntityTransferModule.cs:1088 / :1336   UpdateAgent(reg, finalDestination, agent, sp, ctx)
 └─ EntityTransferModule.cs:1724        m_scene.SimulationService.UpdateAgent(finalDestination, agent, ctx)
     └─ RemoteSimulationConnector.cs:184   if (m_localBackend.IsLocalRegion(...)) -> local
     │                                    else -> m_remoteConnector
         └─ RemoteSimulationConnector.cs:193
             └─ SimulationServiceConnector.cs:172
                 UpdateAgent(destination, (IAgentData)data, ctx, 200000)   // 200 s
                 └─ :254  uri = destination.ServerURI + "agent/" + AgentID + "/"
                     └─ :269  WebUtil.PutToServiceCompressed(uri, args, 200000)
                         └─ WebUtil.cs:276 ServiceOSDRequest(url, data, "PUT", 200000, true, false)
                             └─ :376  GetNewGlobalHttpClient(200000)
                                       HttpClient(SharedSocketsHttpHandler, disposeHandler:false)
                                       Timeout = 200 s
                             └─ :459  log "[WEB UTIL]: SvcOSD {reqnum} PUT {url} took {ticks}ms, {sendlen}/{rcvlen}bytes"
```

**Observed:** `SvcOSD 761 PUT ... took 30203ms, 29381/5bytes`

The timeout passed on this path is **200 000 ms**. The 30 203 ms is therefore the
**measured round trip**, not our ceiling. The `30000` at
`SimulationServiceConnector.cs:124` belongs to `CreateAgent` (POST), a different call.

### The 5 bytes are decoded

```csharp
// AgentHandlers.cs:674-675  (destination side)
httpResponse.StatusCode = (int)HttpStatusCode.OK;
httpResponse.RawBuffer = Util.UTF8.GetBytes(result.ToString());
```

`bool.ToString()` → `"True"` = **4 bytes**, `"False"` = **5 bytes**.
We received 5 bytes: **the destination returned HTTP 200 with `False`.**

Not a timeout. Not a refused connection. A deliberate negative result.

---

## B. Callback call graph

**There is none.** Traced every method on the path:

| File | HTTP calls | Locks | Sync waits |
|---|---|---|---|
| `SimulationServiceConnector.cs` | outbound only | none | none (no `.Result`/`.Wait()`) |
| `RemoteSimulationConnector.cs` | none | none | none |
| `AgentHandlers.cs` (receiving) | none | none | none |
| `LocalSimulationConnector.cs` | **none** | `lock (m_scenes)` L127, L144 | none |

`LocalSimulationConnector.UpdateAgent` (L207-226) is the deepest layer:

```csharp
if (m_scenes.ContainsKey(destination.RegionID))
    return m_scenes[destination.RegionID].IncomingUpdateChildAgent(cAgentData);
return false;   // region unknown to that sim
```

It makes no outbound call and holds no lock across work. **Our side contains no
circular wait and no lock inversion.**

---

## C. Synchronous circular dependency

**Not present in this tree.** The source→destination chain is a single blocking
HTTP call. The destination's handler tree performs zero outbound HTTP.

If a cycle exists, it is entirely inside the destination's deployment (its region
plus whatever sits in front of it). It cannot be observed from our logs alone.

---

## D. Locks involved

| Location | Scope | Held across work? |
|---|---|---|
| `LocalSimulationConnector.cs:127,144` | `m_scenes` dictionary | no |
| `SimulationServiceConnector.cs:194` | `m_updateAgentQueue` | coalescing only |
| `EntityTransferModule` | scene/agent locks | not on this path |

`SimulationServiceConnector.cs:181-209` coalesces position updates per URI. Not a
blocker for a single avatar.

---

## E. Stale-presence mechanism — **confirmed**

This explains the alternating error messages.

Upstream `GatekeeperService.cs`:

```csharp
if (!m_allowDuplicatePresences)
{
    if (guinfo.Online && !guinfo.LastRegionID.IsZero())
    {
        if (SendAgentGodKillToRegion(UUID.Zero, agentID, uui, guinfo))
        {
            reason = "You appear to be already logged in on the destination grid " +
                     "Please wait a a minute or two and retry. ...";
            return false;
        }
    }
}
```

**Sequence producing the alternating messages:**

1. Attempt #1: our `CreateAgent` is refused (see G) → the destination's
   `GridUserService` row for our UUID stays `Online = true` with a `LastRegionID`
2. Attempt #2: gatekeeper sees `guinfo.Online` → sends god-kill → refuses again
   with *"already logged in"*
3. Each retry re-arms the same stale state

Our log shows exactly this alternation:

```
01:45:40  refused: You appear to be already logged in
01:46:17  UpdateAgent failed
02:06:26  (retry) 02:09:01  UpdateAgent failed
```

**Retry is self-defeating.** It never clears, because the refusal path only
*reports*; the stale row persists until their presence service times it out.

`AllowDuplicatePresences` is read from `[PresenceService]` on **their** side. Ours
is unset (defaults `false`), which is the stricter default.

---

## F. Fork differences from upstream

| # | Change | File | Effect on this transaction |
|---|---|---|---|
| 1 | **`ControlPlaneAccess` injected into the agent handler** | `AgentHandlers.cs:210,213,275` | `Authorize` is called in the **POST (CreateAgent)** branch only. IP-based, synchronous, no HTTP. Cannot add latency, but can **refuse**. |
| 2 | **`ControlPlaneTrustedHosts = 127.0.0.1`** | 30/31 region INIs, `Robust.ini:83` | `IsTrustedAddress` = `IsLoopback \|\| m_trustedHosts.Contains` (`ControlPlaneAccess.cs:96`). **Any non-loopback source is refused.** |
| 3 | `RemoteAuthorizationServicesConnector` | `.../Authorization/RemoteAuthorizationServiceConnector.cs:44` | Not on the CreateAgent path. Confirmed inert here. |
| 4 | `HGEntityTransferModule` overrides | `HGEntityTransferModule.cs:339,427` | `CreateAgent` override delegates to base; `UpdateAgent` override is commented out. No behavioural change on this path. |
| 5 | QUIC/QuickG skip for foreign | `EntityTransferModule.cs:1478-1480,1594` | Confirmed not modifying the transaction. |

`ForeignAgentsAllowed`, `AllowExcept`, `DisallowExcept`, `ForeignTripsAllowed_*`,
`VerifyClient`, `VerifyAgent`, `IsLocalGrid`, `GatekeeperURIAlias` retain stock
semantics.

---

## G. Most likely root cause, ranked by evidence

### 1. `ControlPlaneTrustedHosts` — HIGH confidence

`ControlPlaneAccess.cs:96` trusts only loopback plus explicitly listed hosts.
Ours is `127.0.0.1`.

`ControlPlaneAccess.Authorize` is called at `AgentHandlers.cs:275`, inside the
**POST branch** — i.e. exactly on `CreateAgent`.

**If the destination runs this same fork patch with a loopback-only trusted list,
our `CreateAgent` arriving from `51.89.54.203` is refused with 403.** No agent is
created. Our subsequent `UpdateAgent` (PUT, not gated) then arrives at a scene
that has no such agent, and `LocalSimulationConnector.UpdateAgent` returns
`false` → **5 bytes**. That is precisely the observed signature.

This also explains why our own grid rejects **incoming** HG: the same setting
blocks every partner whose real IP is not listed.

**Cannot be confirmed from our side** — it depends on their configuration. Their
`Robust.log` equivalent would show `[CONTROL PLANE ACCESS]: Refusing POST /agent/...`.

### 2. Destination-side HTTP front takes ~30 s — MEDIUM confidence

Their handler tree is instantaneous (proved above). A 30.2 s round trip on a
29 KB payload therefore sits **outside** their region: a reverse proxy, a tunnel,
or a WAF. Their ports respond in 78–310 ms to small requests, so the delay is
payload-dependent or queue-dependent.

### 3. Stale presence — CONFIRMED as a consequence, not a cause

See E. It is the second message, and it makes retries useless.

### 4. Not the cause
- Remote authorization (not on this path)
- Quick-G (skipped for foreign)
- `GatekeeperURIAlias` (callback URL identical before/after disabling)
- Connection pool: 32 per server, ample for one avatar
- Our 30 s ceiling (this path uses 200 s)

---

## H. Files and lines

| Concern | File:line |
|---|---|
| Outgoing UpdateAgent | `OpenSim\Services\Connectors\Simulation\SimulationServiceConnector.cs:170,172,249,254,269` |
| Outgoing CreateAgent | same file `:101,114,124` |
| Local/remote dispatch | `OpenSim\Region\CoreModules\ServiceConnectorsOut\Simulation\RemoteSimulationConnector.cs:184,193` |
| Deepest layer | `...\Simulation\LocalSimulationConnector.cs:207,212,225` |
| Receiving handler | `OpenSim\Server\Handlers\Simulation\AgentHandlers.cs:260,273,275,601,653,674,680,684` |
| Control plane gate | `OpenSim\Server\Handlers\Base\ControlPlaneAccess.cs:46,57,90,96` |
| HTTP pool / timeout | `OpenSim\Framework\WebUtil.cs:114,247,251,262,276,359,376,459` |
| Pool size | `OpenSim\Server\ServerMain.cs:122` and `OpenSim\Framework\Servers\BaseOpenSimServer.cs:120` — both 32 |
| Stale-presence refusal | upstream `GatekeeperService.cs` (`m_allowDuplicatePresences` branch) |
| Trusted-host config | `Robust.ini:83`, 30 region INIs (e.g. `Dark_Secrets:59`) |

---

## I. Minimal diagnostic patch (logs only, no behaviour change)

All additions are `m_log.Info` / `m_log.Debug`. No control flow is altered, no
timeout is increased.

### I.1 Outbound, before the request

`SimulationServiceConnector.cs` — in `UpdateAgent(IAgentData…)` before line 269:

```csharp
m_log.InfoFormat(
    "[HGTRACE] OUT CreateAgent/UpdateAgent BEGIN t={0:HH:mm:ss.fff} tid={1} " +
    "agent={2} destRegion={3} destName={4} destURI={5} bytes={6} timeout={7} " +
    "outboundVersion={8} homeURI={9} callbackURI={10} newCallbackURI={11}",
    DateTime.Now, System.Threading.Thread.CurrentThread.ManagedThreadId,
    cAgentData.AgentID, destination.RegionID, destination.RegionName, uri,
    args.Count, timeout, ctx.OutboundVersion,
    ctx.Source != null ? ctx.Source.ServerURI : "(null)",
    ctx.CallbackURI, ctx.NewCallbackURI);
```

and immediately after the call returns:

```csharp
m_log.InfoFormat(
    "[HGTRACE] OUT CreateAgent/UpdateAgent END   t={0:HH:mm:ss.fff} tid={1} " +
    "agent={2} destURI={3} result={4}",
    DateTime.Now, System.Threading.Thread.CurrentThread.ManagedThreadId,
    cAgentData.AgentID, uri, result["Success"].AsBoolean());
```

### I.2 Response capture with millisecond timing

`WebUtil.cs:459`, extend the existing slow-call log:

```csharp
m_log.Info($"[WEB UTIL]: SvcOSD {reqnum} {method} {url} took {ticks}ms, {sendlen}/{rcvlen}bytes"
    + $" [HGTRACE] t={DateTime.Now:HH:mm:ss.fff} tid={System.Threading.Thread.CurrentThread.ManagedThreadId}"
    + $" rcvContentType={responseMessage?.Content?.Headers?.ContentType}"
    + $" rcvText={(rcvlen < 512 ? System.Text.Encoding.UTF8.GetString(responseBytes ?? Array.Empty<byte>()) : "(too big)")}");
```

### I.3 Destination entry — put on the partner's grid, same build

`AgentHandlers.cs`, first lines of `DoAgentPut` and of the POST branch:

```csharp
m_log.InfoFormat("[HGTRACE] IN DoAgentPut t={0:HH:mm:ss.fff} tid={1} agent={2} dest={3} msgType={4} remote={5}",
    DateTime.Now, System.Threading.Thread.CurrentThread.ManagedThreadId,
    agentID, uuid, messageType, httpRequest.RemoteIPEndPoint);

// in the POST branch, immediately before Authorize:
m_log.InfoFormat("[HGTRACE] IN CreateAgent control-plane check t={0:HH:mm:ss.fff} tid={1} remote={2}",
    DateTime.Now, System.Threading.Thread.CurrentThread.ManagedThreadId,
    httpRequest.RemoteIPEndPoint);
```

### I.4 Control plane decision — the single most useful line

`ControlPlaneAccess.cs:57-64`:

```csharp
m_log.InfoFormat(
    "[HGTRACE] CPA t={0:HH:mm:ss.fff} tid={1} method={2} path={3} remote={4} trusted={5}",
    DateTime.Now, System.Threading.Thread.CurrentThread.ManagedThreadId,
    request.HttpMethod, request.UriPath, request.RemoteIPEndPoint,
    IsTrustedAddress(request.RemoteIPEndPoint.Address));
```

If the partner runs this build, this line immediately answers cause #1.

---

## J. What the instrumentation will prove

| Log line | Outcome |
|---|---|
| `[HGTRACE] OUT … BEGIN` then `END result=False` after ~30 s | confirmed outbound negative |
| Partner `[HGTRACE] CPA … trusted=False` on POST | **cause #1 confirmed** |
| Partner `[HGTRACE] IN DoAgentPut` absent, only CPA refusal | CreateAgent blocked before reaching the region |
| Partner `CPA trusted=True` and `DoAgentPut` returns `False` | their sim lacks the destination region — different problem |
| Our `END` returns in <1 s | 30 s was queueing or pool contention after all |

Do **not** raise the 30-second timeout. The destination answers small requests in
78–310 ms; a longer timeout would only hide a refusal behind a longer wait.
