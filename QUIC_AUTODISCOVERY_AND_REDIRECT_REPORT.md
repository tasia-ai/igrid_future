# QUIC Region Autodiscovery & HTTP→HTTPS Redirect — Feasibility Report

**Date:** 2026-05-21  
**Author:** Tasia  
**Status:** Research only — no code changes

---

## 1. QUIC Region Autodiscovery

### Current state
QUIC host/port is injected **per-session** into the viewer's login response and region handoff events (EnableSimulator, TeleportFinish, CrossedRegion). There is **no** database table, no GridService awareness, and no pre-login discovery mechanism.

### What already exists
- `sim_quic_host` / `sim_quic_port` fields in login response LLSD ✅
- `EventQueueGetHandlers` adds QUIC info to region border crossings ✅
- `LLLoginService.AddQuicLoginAdvertisement()` injects these fields ✅

### What's missing

| Gap | Impact |
|-----|--------|
| No `QuicHost`/`QuicPort` in `GridRegion` class | GridService can't return or store QUIC info per region |
| No DB columns (`quic_host`, `quic_port`) in regions table | No persistence across restarts |
| Login service reads a **single global** config value | All regions get same QUIC host — can't have per-region overrides |
| No pre-login discovery endpoint | Viewers must log in first to discover QUIC regions |
| Standard viewers don't understand QUIC at all | They'll ignore `sim_quic_host` silently |

### How to fix it (2 parts)

**Part A — Add DB persistence** (easy, ~3 files):
- Add `QuicHost` (string) and `QuicPort` (int) to `GridRegion` class
- Add MySQL columns `quicHost VARCHAR(255)`, `quicPort INT` to `regions` table
- Populate from region config at `RegisterRegion()` time
- Let LLLoginService read per-region QUIC info from `GridRegion` instead of global config

**Part B — Pre-login discovery for Tasia Viewer** (moderate):
- Since standard viewers can't understand QUIC, add a **new endpoint** like `GET /quic_regions` on ROBUST
- Returns JSON/LLSD list of regions with QUIC capabilities
- Tasia Viewer fetches this before login to know which regions support QUIC
- Advertise this endpoint URL via `get_grid_info` (e.g., `quic-regions-url` field)

### Verdict
✅ **Doable.** Part A is ~100 lines across 3 files. Part B is another ~80 lines for the handler + grid info field.

---

## 2. HTTP → HTTPS Automatic Redirect on 8002/8003

### Current architecture

| Server Type | HTTP port | HTTPS port | Sibling pair? |
|-------------|-----------|------------|---------------|
| **ROBUST (grid)** | 8002 (or `port`) | 8002 (or `https_port`) | ❌ No sibling — single server, one protocol |
| **Region simulator** | 9000 (or `http_listener`) | 9001 (or `http_listener_ssl`) | ✅ Yes, paired via `SiblingServer` |

ROBUST currently listens on **one** port (8002) and can be EITHER HTTP or HTTPS, not both. If `https_listener = True`, a second listener on `https_port` (default 8443, but often set to 8002 in production) handles SSL.

### Problem
There's **no redirect logic at all**. If a viewer or client connects via HTTP to a server that expects HTTPS, they get a raw SSL error or connection refused — no friendly redirect.

### How to fix it (30-50 lines in `BaseHttpServer.cs`)

**For region simulators** (already have SiblingServer):
```csharp
if (!request.IsSecured && SiblingServer != null)
    response.Redirect("https://" + host + ":" + SiblingServer.Port + path, 301);
```

**For ROBUST** (no sibling — needs to find the HTTPS server):
```csharp
if (!request.IsSecured && !m_ssl)
{
    // Find HTTPS server by looking at MainServer.Servers
    foreach (var srv in MainServer.Servers.Values)
        if (srv.UseSSL)
            redirect to srv.Port;
}
```

**Key detail**: Port 8003 (private) should stay HTTP — internal service calls between ROBUST components don't need SSL overhead. The redirect should only apply to **public** ports/paths.

### Additional improvements
- Add `Strict-Transport-Security` header on HTTPS responses (~5 lines)
- Add config flag `http_redirect_https = true` in `[Network]` section to enable/disable
- Optionally skip redirect for specific paths (internal `/admin/` URLs, service-to-service calls)

### Verdict
✅ **Very easy.** ~40 lines of code, all in `BaseHttpServer.cs`. No new files, no config schema changes needed. Can be done as a single commit.

---

## Summary

| Feature | Difficulty | Files affected | Lines of code | Risk |
|---------|------------|---------------|---------------|------|
| QUIC DB persistence (GridRegion + DB) | Easy | 3-4 | ~100 | Low |
| QUIC pre-login discovery endpoint | Moderate | 2-3 | ~80 | Low |
| HTTP→HTTPS auto-redirect | **Very easy** | 1 (`BaseHttpServer.cs`) | ~40 | Low |
| HSTS headers | Trivial | 1 | ~5 | None |

**Both features are independently useful and don't conflict with each other.** Recommended order:

1. **First:** HTTP→HTTPS redirect (small, safe, immediate benefit)
2. **Then:** QUIC DB persistence + discovery endpoint
