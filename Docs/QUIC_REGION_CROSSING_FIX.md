# QUIC Region Crossing Fix — Specification

**Date:** 2026-05-22  
**Author:** Tasia  
**Status:** Deployed (sim restart required)

---

## 1. Bug Description

### Symptom
When an avatar teleports between regions on I-Grid, QUIC transport is lost. The viewer falls back to UDP for the destination region.

### Root Cause
During region crossing events (`EnableSimulator`, `TeleportFinishEvent`, `CrossRegion`), the **source** simulator advertises its **own** QUIC host/port instead of the **destination** region's.

Since each I-Grid region runs as a separate simulator process with a unique QUIC port (9000–9035), the viewer receives incorrect QUIC connection info for the destination:

- Source region A (QUIC port 9000) sends EnableSimulator for region B
- Advertises `sim_quic_host` + `sim_quic_port` **from source A's config**
- Viewer tries to connect to port 9000 thinking it's region B → fails

### Code Location

`EventQueueGetHandlers.cs` — `AddQuicSimulatorInfo()`:

```csharp
private void AddQuicSimulatorInfo(osUTF8 sb, IPEndPoint fallbackEndPoint)
{
    // ...
    string host = m_quicAdvertiseHost;       // ← ALWAYS source sim's host
    // ...
    LLSDxmlEncode2.AddElem("sim_quic_port", m_quicAdvertisePort, sb);  // ← ALWAYS source sim's port
}
```

Called from `EnableSimulator()`, `TeleportFinishEvent()`, and `CrossRegion()` — all three pass only the destination's UDP endpoint as a fallback, but the QUIC host/port are always pulled from the **local** sim config.

---

## 2. Affected Systems

| Component | Impact |
|-----------|--------|
| **Teleport** (cross-sim) | QUIC lost on destination — falls back to UDP |
| **Border crossing** (walk/ride) | QUIC lost on new region — falls back to UDP |
| **Login → first region** | ✅ Not affected — login response uses per-region QUIC from `GridRegion` |
| **Child agent creation** (neighbour regions) | QUIC lost — viewer uses UDP for child connections |

All region-crossing event types are affected because they share the same `AddQuicSimulatorInfo()` helper.

---

## 3. Architecture

### Data flow — login (works correctly)

```
GridRegion.QuicHost / QuicPort
    ↓
LLLoginService.AddQuicLoginAdvertisement()
    ↓ Checks destination GridRegion → uses per-region QUIC if set, global fallback
    ↓
Login response LLSD: sim_quic_host, sim_quic_port ✅
```

### Data flow — region crossing (BROKEN, now fixed)

```
EntityTransferModule (source sim)
    ↓ calls with destination GridRegion
IEventQueue.EnableSimulator(..., quicHost, quicPort)
    ↓
EventQueueGetHandlers.EnableSimulator(..., quicHost, quicPort)
    ↓
AddQuicSimulatorInfo(sb, endpoint, quicHost, quicPort)
    ↓ Uses dest QUIC if provided, otherwise source config fallback
    ↓
EventQueue LLSD: sim_quic_host, sim_quic_port ✅ (now correct)
```

### Key objects

| Object | Properties | Source |
|--------|-----------|--------|
| `GridRegion` | `QuicHost` (string), `QuicPort` (uint) | Region registration → grid DB |
| `EventQueueGetModule` | `m_quicAdvertiseHost`, `m_quicAdvertisePort` (local config) | `[ClientStack.Quic]` in OpenSim.ini per sim |
| `RegionInfo` | `QuicHost`, `QuicPort` | Per-region config, serialized at registration |

---

## 4. Changes Made

### 4.1 Interface — `IEventQueue.cs`

```csharp
// Before:
void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY);
void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, ...);
void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, ...);

// After:
void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY,
                     string quicHost = null, uint quicPort = 0);
void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, ...,
                         string quicHost = null, uint quicPort = 0);
void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, ...,
                 string quicHost = null, uint quicPort = 0);
```

Default values (`null`, `0`) preserve backward compatibility — any code still calling without QUIC info falls back to the old behaviour.

### 4.2 Event handlers — `EventQueueGetHandlers.cs`

All three event methods pass the new `quicHost`/`quicPort` parameters through to `AddQuicSimulatorInfo()`.

`AddQuicSimulatorInfo()` updated:

```csharp
private void AddQuicSimulatorInfo(osUTF8 sb, IPEndPoint fallbackEndPoint,
    string quicHost = null, uint quicPort = 0)
{
    if (!m_quicAdvertiseEnabled)
        return;

    string host = quicHost ?? m_quicAdvertiseHost;           // ← dest QUIC if provided
    if (string.IsNullOrWhiteSpace(host))
        host = fallbackEndPoint.Address.ToString();

    uint port = quicPort > 0 ? quicPort : (uint)m_quicAdvertisePort;  // ← dest QUIC if provided

    LLSDxmlEncode2.AddElem("sim_quic_host", host, sb);
    LLSDxmlEncode2.AddElem("sim_quic_port", (int)port, sb);
}
```

### 4.3 Callers — `EntityTransferModule.cs`

Five call sites updated to pass the destination `GridRegion`'s QUIC info:

| Line | Method | QUIC source |
|------|--------|-------------|
| 976 | `EnableSimulator` for teleport | `finalDestination.QuicHost` / `finalDestination.QuicPort` |
| 1078 | `TeleportFinishEvent` (main TP path) | `finalDestination.QuicHost` / `finalDestination.QuicPort` |
| 1223 | `TeleportFinishEvent` (neighbour TP) | `finalDestination.QuicHost` / `finalDestination.QuicPort` |
| 1784 | `EnableSimulator` for child agent | `neighbourRegion.QuicHost` / `neighbourRegion.QuicPort` |
| 2604 | `EnableSimulator` for neighbour | `reg.QuicHost` / `reg.QuicPort` |

### 4.4 Test mock — `TestEventQueueGetModule.cs`

Updated `EnableSimulator`, `TeleportFinishEvent`, `CrossRegion` signatures to match interface (add optional params).

---

## 5. Deployment

### DLLs replaced

| DLL | Contains |
|-----|----------|
| `OpenSim.Region.Framework.dll` | `IEventQueue` interface |
| `OpenSim.Region.ClientStack.LindenUDP.dll` | `EventQueueGetHandlers` (EnableSimulator, TeleportFinish, CrossRegion, AddQuicSimulatorInfo) |
| `OpenSim.Region.CoreModules.dll` | `EntityTransferModule` (callers) |

### Deploy path

```
/mnt/c/new/igrid_server_code/build/Release/
    ↓ scp (sshpass)
root@i.let-us.cyou:/home/marty/opensim/igrid-bin/bin/
    ↓ docker bind mount (shared by all 32 region simulators)
```

### Prerequisites

- Config MUST have `QuicHost`/`QuicPort` set per region in `OpenSim.ini` under `[ClientStack.Quic]`, OR the global `AdvertiseHost`/`AdvertisePort` for each sim instance.
- Grid DB `regions` table must have `quicHost`/`quicPort` columns populated (set at region registration time from `RegionInfo`).

### Verification

1. Restart all region simulators
2. Connect with Tasia Viewer
3. Teleport between two regions on different ports
4. Check viewer logs for `sim_quic_host`/`sim_quic_port` in `TeleportFinish` event — should match destination region's QUIC port, not source's
5. Verify QUIC packets flow on the destination region (no fallback to UDP)

---

## 6. Testing Matrix

| Scenario | Expected result |
|----------|----------------|
| Login → Region A | QUIC works (was already correct) |
| Teleport Region A → Region B (different sim, different QUIC port) | QUIC works on B ✅ |
| Teleport Region A → Region B (same sim, same QUIC port) | QUIC works on B (no change, same port) ✅ |
| Walk across region border A ↔ B | QUIC works on new region ✅ |
| Child agent creation for neighbour | QUIC works for child connection ✅ |
| Login → Region A (no QUIC configured) | No QUIC advertised (unchanged) ✅ |
| Standard (non-Tasia) viewer teleport | `sim_quic_host`/`sim_quic_port` ignored (unchanged) ✅ |

---

## 7. Security & Edge Cases

- QUIC info is only as trustworthy as the config that populated `GridRegion.QuicHost`/`GridRegion.QuicPort`
- Empty or null `QuicHost` falls back to the sim's own `AdvertiseHost` from config (existing behaviour)
- `QuicPort == 0` falls back to the sim's own `AdvertisePort` (existing behaviour)
- The `advertiseEnabled` flag is still the source sim's decision — if source doesn't advertise QUIC, destination won't either
- LLSD encoding uses `(int)port` — `uint Port` from GridRegion is safe to cast (ports are 1–65535)

---

## 8. Future Improvements

- **Per-region QUIC overrides in Grid DB:** The `GridRegion.QuicHost`/`QuicPort` fields already exist in code but need DB columns persisted (see `QUIC_AUTODISCOVERY_AND_REDIRECT_REPORT.md`)
- **Pre-login QUIC discovery:** An endpoint like `GET /quic_regions` exists but is optional — viewers could discover QUIC regions before logging in
- **Multi-region simulators:** For sims hosting multiple regions on the same QUIC port, the current fix handles this correctly (same port for all regions on that sim)
