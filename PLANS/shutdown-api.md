# Grid Shutdown API Plan

## Status: Half-done, continue tomorrow

## What's done:
- RestartModule.cs: Added `shutdown` action case — runs `MainConsole.Instance.RunCommand("backup")` then schedules quit via restart with 10s delay

## What's needed:
1. Build RestartModule.cs → deploy DLL
2. Add `/api/shutdown` endpoint to Python sstats_proxy.py:
   - API key auth (env var SHUTDOWN_API_KEY)
   - Calls each sim's `/tasia-ngc/restart/{regionID}` with action=shutdown + Bearer token
   - After all sims shutdown, runs backup via Docker exec
3. Docker-compose: need to know sim HTTP ports for the API calls
4. Add SHUTDOWN_API_KEY env var to docker-compose

## Flow:
```
POST /api/shutdown (with API key)
  → For each sim: POST http://localhost:PORT/tasia-ngc/restart/{regionID}
      Body: {"action": "shutdown"}
      Header: Authorization: Bearer {RESTART_TOKEN}
  → Wait for responses
  → Return status
```

## Config needed in sim Opensim.ini:
```ini
[RestartModule]
  Enable = true
  ApiEnabled = true
  ApiToken = {secret_token}
  DefaultDelaySeconds = 180
  MarkerPath = /tmp/region_restart_marker
```
