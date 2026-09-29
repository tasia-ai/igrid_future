# Hypergrid Audit — Fresh Metaverse

**Date:** 2026-09-28
**Auditor:** Tasia (Windows)
**Scope:** all `Robust.ini`, `config-include/*.ini`, `regions/*/OpenSim.ini`, generator, templates, and C# source
**Constraint:** read-only. Nothing was modified during this audit.

---

## 0. Executive summary

Outgoing HG teleports fail after ~30 s with `UpdateAgent failed` / `UpdateAgent`.
Two distinct failure messages were observed, alternating across attempts:

```
Teleport ... was refused because You appear to be already logged in on the destination grid
UpdateAgent failed on teleport ... Keeping avatar in Dark_Secrets
```

**The 30 s figure is our own client timeout, not a slow destination:**

```csharp
// OpenSim\Services\Connectors\Simulation\SimulationServiceConnector.cs:124
OSDMap result = WebUtil.PostToServiceCompressed(uri, args, 30000);   // 30 000 ms
```

Observed in the log: `took 30203ms, 29381/5bytes` — our request hit our own ceiling.

The destination grid is **not** slow. Measured directly:

| Target | Result |
|---|---|
| `https://i.let-us.cyou:9130/` | HTTP 200, 310 ms |
| `https://i.let-us.cyou:9130/agent/<uuid>/` | HTTP 405, 78 ms |
| `https://i.let-us.cyou:9101/agent/<uuid>/` | HTTP 200, 118 ms |
| TCP 9101 / 9102 / 9103 / 9130 | all OPEN |

**Leading hypothesis (unproven): circular callback dependency.** Their `CreateAgent`
handler calls back to our `CallbackURI` and waits. We wait on their reply. Classic
HG deadlock. Requires their logs to confirm.

---

## 1. Incoming HG whitelist logic

```ini
; Robust.ini [GatekeeperService]
ForeignAgentsAllowed = false
DisallowExcept = "https://i.let-us.cyou:8002,http://i.let-us.cyou:8002,http://playground.darkheartsos.com:8002/"
```

- **Status: CORRECT — do not change.**
- `ForeignAgentsAllowed = false` with `DisallowExcept` is whitelist-only behaviour.
- No config sets `ForeignAgentsAllowed = true`. Verified across all `config-include/*.ini`
  and all 31 region INIs.
- Intended grids are present, both `http` and `https` forms of `i.let-us.cyou:8002`.

Verified against upstream `GatekeeperService.cs` logic:

```csharp
m_ForeignAgentsAllowed = serverConfig.GetBoolean("ForeignAgentsAllowed", true);
LoadDomainExceptionsFromConfig(serverConfig, "AllowExcept", m_ForeignsAllowedExceptions);
LoadDomainExceptionsFromConfig(serverConfig, "DisallowExcept", m_ForeignsDisallowedExceptions);
```

---

## 2. Outgoing HG restrictions — HIGH

```ini
; Robust.ini [GatekeeperService]
ForeignTripsAllowed_Level_0   = false
ForeignTripsAllowed_Level_50  = false
ForeignTripsAllowed_Level_100 = false
ForeignTripsAllowed_Level_150 = false
ForeignTripsAllowed_Level_200 = true
ForeignTripsAllowed_Level_250 = true
```

**Levels 0–150 are blocked for outgoing HG trips.** Only accounts at trust level
200+ can leave the grid. This is almost certainly intended (visitor accounts should
not be able to export the grid), but it must be confirmed that the test account
is level 200+. The same values are duplicated in `[UserAgentService]`.

**Suggested value:** unchanged. Confirm the account level rather than editing this.

---

## 3. Hypergrid modules — loading status

Configured in `config-include/GridHypergrid.ini` `[Modules]`, not in the region INIs.

| Module | Configured | Observed working in region log |
|---|---|---|
| `HGEntityTransferModule` | yes | yes (3 hits) |
| `HGInventoryBroker` | yes | yes (3 hits) |
| `HGInventoryAccessModule` | yes | yes (3 hits) |
| `HGMessageTransferModule` | yes | yes (3 hits) |
| `HGUserManagementModule` | yes | yes (6 hits) |
| `HGFriendsModule` | yes | 0 hits |
| `HGLureModule` | yes | 0 hits |

`HGFriendsModule` and `HGLureModule` show zero log lines. **This is not evidence of
failure** — both only log when actually used, and no friend/lure action was performed.
Do not change.

Also present and correct:
```ini
; GridHypergrid.ini [AssetService]
LocalGridAssetService   = "OpenSim.Services.Connectors.dll:AssetServicesConnector"
HypergridAssetService   = "OpenSim.Services.Connectors.dll:HGAssetServiceConnector"

; region INI, all 31
[HGInventoryAccessModule]
RestrictInventoryAccessAbroad = False
```
Suitcase inventory is enabled in 31/31 regions.

---

## 4. Robust public HG connectors

All present in `Robust.ini [ServiceList]`, correctly ported to `PublicPort` (22000):

| Connector | Line | Status |
|---|---|---|
| `GatekeeperServiceInConnector` | PublicPort | present |
| `UserAgentServerConnector` | PublicPort | present |
| `HeloServiceInConnector` | PublicPort | present |
| `HGFriendsServerConnector` | PublicPort | present |
| `HGGroupsServiceConnector` | PublicPort | present |
| `InstantMessageServerConnector` | PrivatePort | present |
| `HGInventoryServiceConnector` | PublicPort | present |
| `HGAssetServiceConnector` | PublicPort | present |

`/UserAgent` was verified responding — **only on `POST`**:

```
GET   /UserAgent  -> 404
POST  /UserAgent  -> 200, 225 ms
```

A `GET`-only health check would falsely report this service as missing.

---

## 5. HG identity and URLs — consistent

Intended identity `http://os.tasia.work.gd:22000` is applied uniformly:

```ini
[Const]
BaseHostname = "os.tasia.work.gd"
BaseURL      = "http://${Const|BaseHostname}"
PublicPort   = "22000"
PrivatePort  = "22001"
```

`HomeURI`, `GatekeeperURI`, `ExternalName`, `login` and `gatekeeper` all resolve to
`http://os.tasia.work.gd:22000`. No hostname, protocol or port inconsistencies found.

DNS: `os.tasia.work.gd -> 51.89.54.203`. Verified reachable: `HTTP 200` in 735 ms.

---

## 6. Public vs private services

Split is correct: public HG/login on 22000, internal on 22001.
`ServerURI` in the region database carries a per-region port, e.g.:

```
Dark_Secrets   http://os.tasia.work.gd:22341/
```

**This is normal Hypergrid behaviour, not a defect.** Partner grids do the same:

```
wolfterritories   http://grid.wolfterritories.org:8002/
opensimnetwork    http://opensimnetwork.zapto.org:8002/
```

The callback endpoint on 22341 was verified to exist:
`GET /agent/<uuid>/<regionid>/release/` returns **405 Method Not Allowed** = the
handler is present and requires `PUT`.

`GridServerURI` is unset (1 code reference only, `GridServicesConnector.cs:72`).
Marginal. No change recommended.

---

## 7. Custom authorization — FORK DIVERGENCE, NOT PROVEN AS THE CAUSE

All 31 region INIs override the standard connector:

```ini
; config-include/GridHypergrid.ini:20   (standard OpenSim)
AuthorizationServices = "LocalAuthorizationServicesConnector"

; regions/<all 31>/OpenSim.ini:151     (fork override, wins)
AuthorizationServices   = RemoteAuthorizationServicesConnector
; regions/<all 31>/OpenSim.ini:336
AuthorizationServerURI  = "http://127.0.0.1:22163/hg/auth"
```

Port 22163 is served by:

```
python.exe H:\grid_final\FreshMetaverseManager\hgauth.py --port 22163 --db ...
```

Implementation: `OpenSim\Region\CoreModules\ServiceConnectorsOut\Authorization\
RemoteAuthorizationServiceConnector.cs:44`, shipped inside
`OpenSim.Region.CoreModules.dll` (not as an addon).

**Assessment: this is a real fork divergence worth documenting, but it is NOT the
cause of the 30 s failure.** `CreateAgent` does not traverse
`AuthorizationServices`; the region authenticates inbound HTTP handlers via
`BaseStreamHandler.Authenticate(...)`. Evidence it works:
`[REMOTE AUTHORIZATION CONNECTOR]: Enabled remote authorization for region Dark_Secrets`.

**Recommendation: do not change.** The service is up and answering (404 in 3 ms on
`GET /hg/auth`, i.e. alive, path is POST-only). It is recorded here because it is
a fork-specific coupling to a local process that must be running for region HTTP to
work at all.

---

## 8. Region networking

- `ExternalHostName = "os.tasia.work.gd"` — consistent across regions.
- `InternalPort` / `http_listener_port` — present per region.
- `GatekeeperURIAlias`: **currently disabled** (commented out 2026-09-29 for testing,
  backup at `H:\grid\backups\hg-alias-experiment\Robust.ini.pre-alias-removal-20260929-011204`).
  Originally 32 entries (31 region ports + bare host).

**Untested risk:** with the alias disabled, same-grid teleports between our own
regions may be treated as foreign. This was never verified because only one region
was running during testing. **Restore the alias before running the full 30-region
grid** unless Mom decides otherwise on exposure grounds.

---

## 9. Correct — do not change

- `HomeURI` / `GatekeeperURI` in region INI (lines 263/351/352)
- `SimulationServiceSecret` identical in `Robust.ini [SimulationService]` and region
  `[Modules]` — required shared secret for `/agent` and `/object` control endpoints
- `AllowHypergridMapSearch = true` in `GridHypergrid.ini:68` — grid IS discoverable
  in HG map search
- `AllowTeleportsToAnyRegion = true`
- `BypassClientVerification = true` in `[UserAgentService]` — deliberate, see §10

---

## 10. Proxy / tunnel — Pangolin

```
; Robust.ini [UserAgentService]
; Behind the Pangolin tunnel every viewer arrives as 127.0.0.1, so the
; IP-match check in VerifyClient can never pass for direct-connecting
; foreign grids (they see the real client IP) and kills all outgoing HG
; teleports. Bypass it: agent-token auth (VerifyAgent) still applies.
BypassClientVerification = true
```

This is a **correct, deliberate workaround** for the Pangolin tunnel. It disables
the client IP match but keeps agent-token auth. Documented upstream behaviour;
leave as is.

`HasProxy` is **not set** and **must not be set**. It is read in
`GatekeeperServerConnector.cs:70`, `UserAgentServerConnector.cs:91` and
`LLLoginServiceInConnector.cs:95`, where it changes how the client IP is resolved
(forwarded headers). Our Robust is directly exposed on `51.89.54.203:22000`; the
Pangolin tunnel is viewer-side. Enabling it would break client IP detection.

---

## 11. QUIC / Quick-G interaction with HG

Quick-G is **already excluded from the foreign path**:

```
[ENTITY TRANSFER MODULE] Skipping QUIC proxy pre-registration for foreign destination
Grid_Welcome (https://i.let-us.cyou:9101/)
```

Code: `EntityTransferModule.cs:1478-1480`, `IsForeignDestination()` at 1594.
Region `serverURI` and gatekeeper URLs are not modified by QUIC code.
EventQueue teleport responses are unaffected.

HG correctly falls back to LLUDP/HTTP. No forced local QUIC endpoint.

**Separate defect found (not HG-related):** concurrent writes on one `QuicStream`:

```
[QuicClient] Flush error: This method may not be called when another write operation is pending.
[QuicServer] QUIC client disconnected: Send error: This method may not be called when another write operation...
```

Needs a write lock or `SemaphoreSlim` per stream. Visible at 1 user, will bite
under multi-viewer load.

---

## 12. Generated configs — where each setting comes from

| Setting | Origin |
|---|---|
| `AuthorizationServices` / `AuthorizationServerURI` | `Docs\deployment-examples\fresh-metaverse\igrid-package\templates\SimOpenSim.ini.tpl:138` (+ `Docs\deployment-examples\config-include\GridCommon.ini:21`) |
| `RestrictInventoryAccessAbroad` | `SimOpenSim.ini.tpl` |
| `GatekeeperURIAlias` | `Robust.ini.tpl` |
| `[Modules]` HG block | `config-include\GridHypergrid.ini` (hand-maintained) |
| Region `OpenSim.ini` (31 files) | generated by `Docs\deployment-examples\fresh-metaverse\igrid-package\generate_configs.py` |

**Do not hand-edit the 31 region INIs.** The generator will overwrite them.
Fix `SimOpenSim.ini.tpl` (or `GridCommon.ini`) and re-run `generate_configs.py`.

Note: `H:\grid\work\fixtest\generated-pgsql2\` is generator output and is
gitignored — it contains credentials (`console_pass`, `ApiToken`) and must never
be committed.

---

## 13. Source divergence from upstream — confirmed

Fork-specific HG changes found in this tree:

| Change | Location |
|---|---|
| `RemoteAuthorizationServicesConnector` | `OpenSim\Region\CoreModules\ServiceConnectorsOut\Authorization\` (new) |
| `SimulationServiceSecret` gate | `Robust.ini [SimulationService]` + region `[Modules]` |
| `BypassClientVerification` | `[UserAgentService]` |
| `HypergridEgressPolicy` | `OpenSim\Services\HypergridService\` (new, untracked source) |
| `ControlPlaneAccess` | `OpenSim\Server\Handlers\Base\` (new) + tests |
| `TrustedVerificationGrids` | `HGEntityTransferModule` — **implemented but never configured** (0/31 region INIs have `[EntityTransfer]`) |
| QUIC/Quick-G | multiple, now extracted to `TasiaAddons.Quic` |

`ForeignAgentsAllowed`, `AllowExcept`, `DisallowExcept`, `ForeignTripsAllowed_*`,
`VerifyClient`, `VerifyAgent`, `IsLocalGrid`, `GatekeeperURIAlias` all retain
**stock OpenSim semantics**.

---

## 14. Findings by severity

### 🔴 CRITICAL — can stop HG completely
1. Outgoing teleport hits our own 30 s ceiling in `CreateAgent`
   (`SimulationServiceConnector.cs:124`). Leading cause: circular callback deadlock,
   **unproven** — needs destination-side logs.
2. `ForeignTripsAllowed_Level_0/50/100/150 = false` blocks normal accounts from
   leaving the grid. Confirm the test account's trust level.

### 🟠 HIGH — likely to break HG
3. `DefaultHGRegion` points at `MainLand01`, which is not running in the current
   1-region state. Correct once the full grid is up.
4. `GatekeeperURIAlias` disabled — same-grid crossings unverified.
5. `TrustedVerificationGrids` configured but never read (0/31 region INIs have
   `[EntityTransfer]`).

### 🟡 MEDIUM — suspicious / custom
6. Fork override forcing `RemoteAuthorizationServicesConnector` → `hgauth.py` on
   localhost:22163. Working, but a hard dependency on a local process.
7. `MessageKey` unset — god-kill IM on "already logged in" may not be authorised.
8. `OutboundPermission` unset — no asset-outbound protection.
9. `GridServerURI` unset (marginal).
10. AI module: source builds 47 616 B, production runs 55 808 B (25-09) —
    deployed version is not reproducible from our source.

### ✅ Correct — must not be changed
- `ForeignAgentsAllowed = false` + `DisallowExcept` whitelist
- Public/private port split 22000 / 22001
- `SimulationServiceSecret` match between Robust and regions
- `AllowHypergridMapSearch = true`, `AllowTeleportsToAnyRegion = true`
- `BypassClientVerification = true` (Pangolin workaround)
- `HasProxy` unset
- All 8 public HG connectors present
- Callback endpoint on region port is valid (405 on GET)

---

## 15. Corrections to earlier statements in this session

Recorded so nobody repeats them:

- `AllowHypergridMapSearch` **is** set (`GridHypergrid.ini:68`). I earlier reported
  it as unset because I only searched `Robust.ini`.
- `[HGInventoryAccessModule]` is a **region** setting, present in 31/31 region INIs.
  I first reported it missing from `Robust.ini`, which was a category error.
- `HGFriendsModule` / `HGLureModule` zero log hits is **not** evidence of failure.
- `HasProxy` and `DefaultHGRegion` should **not** be changed despite my earlier
  suggestion.
- The `UserAgentConnector` is present as `UserAgentServerConnector`; an earlier
  "missing connector" claim was a bad search.
