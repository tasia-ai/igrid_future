# MetricsConnector Addon Module — Plan

## Goal
Build a region module that collects OpenSim metrics and exposes them via HTTP API.
Python dashboard queries this instead of dotnet-monitor.

## Data to Collect

### 1. Sim Stats (same as jsonSimStats)
- Sim FPS (from Scene.StatsReporter.LastReportedSimFPS)
- Physics FPS, Net FPS
- Agents in view (Scene.GetScenePresences count)
- Object count (Scene.GetSceneObjectCount)
- Script count (Scene.GetActiveScriptsCount)
- Memory usage
- Dropped packets, resent packets

### 2. HTTP Endpoint/CAPS Summary
- Hook into BaseHTTPServer request handling
- Track per-endpoint: total calls, avg duration, total time
- CAPS handler stats

### 3. Timing
- Uptime
- Last backup time
- Total sim frames

## API Endpoint
```
GET /tasia-ngc/metrics/{regionID}
Authorization: Bearer {API_TOKEN}

Response:
{
  "region_name": "River-Retreat",
  "sim_fps": 45.2,
  "physics_fps": 45.0,
  "agents_in_view": 5,
  "object_count": 1234,
  "script_count": 56,
  "memory_mb": 1024,
  "packets_dropped": 0,
  "packets_resent": 0,
  "uptime_seconds": 3600,
  "total_frames": 1000000,
  "endpoints": {
    "/assets/": {"calls": 1234, "avg_ms": 12.5, "total_ms": 15420},
    "/cap/": {"calls": 5678, "avg_ms": 5.2, "total_ms": 29526}
  }
}
```

## Config
```ini
[MetricsConnector]
  Enabled = true
  ApiEnabled = true
  ApiToken = {token}
  ApiPathPrefix = /tasia-ngc/metrics
  CollectIntervalSeconds = 10
```

## Files to Create
- `addon-modules/TasiaAddons.MetricsConnector/MetricsConnector.cs`
- `addon-modules/TasiaAddons.MetricsConnector/TasiaAddons.MetricsConnector.csproj`

## Project References
- OpenSim.Framework.dll
- OpenSim.Region.Framework.dll
- OpenSim.Services.Interfaces.dll
- OpenMetaverse.dll
- log4net
- Mono.Addins
