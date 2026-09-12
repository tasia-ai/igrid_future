# SStats Fix — OpenSim Statistics Module (MySQL-backed)

## What This Contains

Fixed version of OpenSim's `/SStats` web statistics module with:
1. **Bug fixes** in the C# source code
2. **MySQL-backed Python dashboard** — password-shielded, reads same DB as OpenSim
3. **Docker deployment** — OpenSim + MySQL + Dashboard + dotnet-monitor
4. **dotnet-monitor** integration for production diagnostics

## Bugs Fixed

| File | Bug | Fix |
|------|-----|-----|
| `WebStatsModule.cs:268` | Path parsing off-by-one: `Length > 9` should be `> 8` | Changed boundary check |
| `WebStatsModule.cs:61` | Static `dbConn` shared across instances (thread-unsafe) | Changed to instance field |
| `WebStatsModule.cs:420` | `readLogLines` crashes if log file missing/locked | Added try/catch + File.Exists |
| `WebStatsModule.cs:136` | No authentication on stats endpoint | Added HTTP Basic Auth + session cookies |
| `WebStatsModule.cs:255` | No error handling in report rendering | Added try/catch with 500 response |
| `Updater_distributor.cs:51` | `StreamReader` not in `using` (resource leak) | Wrapped in `using` block |
| `RegionStatsHandler.cs:84` | Hardcoded `http://` scheme | Detects `X-Forwarded-Proto` header |

## Files

```
sstats_fix/
├── fixed-sources/
│   ├── WebStatsModule.cs          # Fixed C# module (copy over original)
│   ├── Updater_distributor.cs     # Fixed resource leak
│   └── RegionStatsHandler.cs      # Fixed hardcoded http://
├── WebStats.ini.example           # OpenSim config for SStats
├── monitor/
│   └── dotnet-monitor.json        # dotnet-monitor configuration
├── docker/
│   ├── Dockerfile                 # OpenSim + SStats image
│   ├── docker-compose.yml         # Full stack: OpenSim + MySQL + Dashboard
│   ├── entrypoint.sh              # Docker entrypoint
│   └── .env.example               # Environment variables template
├── python-app/
│   ├── sstats_proxy.py            # Password-shielded MySQL dashboard
│   ├── Dockerfile                 # Python app image
│   └── requirements.txt           # Python dependencies (pymysql)
└── README.md                      # This file
```

## How to Update OpenSim with Fixed C# Code

### Option A: Replace files in source tree

```bash
# From the repo root (/home/marty/igrid-server-code-2026-07-03)

# 1. Backup originals
cp OpenSim/Region/OptionalModules/UserStatistics/WebStatsModule.cs \
   OpenSim/Region/OptionalModules/UserStatistics/WebStatsModule.cs.bak
cp OpenSim/Region/OptionalModules/UserStatistics/Updater_distributor.cs \
   OpenSim/Region/OptionalModules/UserStatistics/Updater_distributor.cs.bak
cp OpenSim/Region/Framework/Scenes/RegionStatsHandler.cs \
   OpenSim/Region/Framework/Scenes/RegionStatsHandler.cs.bak

# 2. Copy fixed files
cp sstats_fix/fixed-sources/WebStatsModule.cs \
   OpenSim/Region/OptionalModules/UserStatistics/WebStatsModule.cs
cp sstats_fix/fixed-sources/Updater_distributor.cs \
   OpenSim/Region/OptionalModules/UserStatistics/Updater_distributor.cs
cp sstats_fix/fixed-sources/RegionStatsHandler.cs \
   OpenSim/Region/Framework/Scenes/RegionStatsHandler.cs

# 3. Add WebStats.ini to bin/ (if not present)
cp sstats_fix/WebStats.ini.example bin/WebStats.ini
# Edit bin/WebStats.ini — set your password!

# 4. Rebuild
cd tools/
./build-release.sh
# or: msbuild /t:Build /p:AllowUnsafeBlocks=true /p:Configuration=Release
```

### Option B: Docker (recommended)

```bash
cd sstats_fix/docker/

# 1. Create .env from template
cp .env.example .env
# Edit .env — set MySQL password and dashboard credentials

# 2. Build and start
docker-compose up -d --build

# 3. Check status
docker-compose ps
docker-compose logs -f sstats-dashboard
```

### Option C: Patch only (git-compatible)

```bash
# Generate a diff of just the changes:
diff -u OpenSim/Region/OptionalModules/UserStatistics/WebStatsModule.cs.bak \
        sstats_fix/fixed-sources/WebStatsModule.cs > sstats_fix/patches/WebStatsModule.patch

# Apply later:
patch -p0 < sstats_fix/patches/WebStatsModule.patch
```

## MySQL Table Schema

The Python dashboard reads/writes the **same table** the C# module uses:

```sql
-- This table is shared between OpenSim (C#) and the Python dashboard
-- Created automatically by either side if missing
CREATE TABLE IF NOT EXISTS stats_session_data (
    session_id VARCHAR(36) NOT NULL PRIMARY KEY,
    agent_id VARCHAR(36) NOT NULL DEFAULT '',
    region_id VARCHAR(36) NOT NULL DEFAULT '',
    last_updated INT NOT NULL DEFAULT 0,
    name_f VARCHAR(50) NOT NULL DEFAULT '',
    name_l VARCHAR(50) NOT NULL DEFAULT '',
    avg_fps FLOAT NOT NULL DEFAULT 0,
    avg_ping FLOAT NOT NULL DEFAULT 0,
    mem_use FLOAT NOT NULL DEFAULT 0,
    client_version VARCHAR(255) NOT NULL DEFAULT '',
    -- ... (full schema in WebStatsModule.cs SQL_STATS_TABLE_CREATE)
);
```

## Python Dashboard

**Direct MySQL mode** (recommended — reads same DB as OpenSim):

```bash
export MYSQL_HOST=localhost
export MYSQL_DB=opensim
export MYSQL_USER=opensim
export MYSQL_PASS=yourpassword
export PROXY_USERNAME=admin
export PROXY_PASSWORD=changeme

pip install pymysql
python sstats_fix/python-app/sstats_proxy.py

# Open: http://localhost:8080
```

**Endpoints:**
- `GET /` — Password-shielded dashboard (login required)
- `GET /health` — Health check (no auth)
- `GET /api/stats` — JSON API (auth required)
- `POST /api/stats` — Insert session data (auth required)
- `GET /logout` — Logout

## OpenSim Config (bin/WebStats.ini)

```ini
[WebStats]
enabled = true

; Auth (recommended for production)
AuthEnabled = true
AuthUsername = admin
AuthPassword = YOUR_STRONG_PASSWORD

; Optional: hide endpoint behind secret path
; StatsSecret = mySecretToken
StatsSecret =
```

## Multi-Sim Setup (31+ sims)

Each OpenSim process creates its **own** diagnostic socket. With many sims:

```
/dotnet-sockets/
├── sim-1/
│   └── dotnet-monitor.sock    ← sim-1's IPC socket
├── sim-2/
│   └── dotnet-monitor.sock    ← sim-2's IPC socket
├── sim-3/
│   └── dotnet-monitor.sock    ← sim-3's IPC socket
└── ...
```

### Generate docker-compose blocks for N sims

```bash
cd sstats_fix/docker/

# Generate 31 sim blocks and append to docker-compose.yml:
./generate-sims.sh 31 >> docker-compose-custom.yml

# Or edit docker-compose.yml directly — each block looks like:
#   sim-N:
#     <<: *sim-defaults
#     container_name: sim-N
#     environment:
#       SIM_NAME: sim-N
#       DOTNET_DiagnosticPorts: /dotnet-sockets/sim-N/dotnet-monitor.sock
#     ports:
#       - "9000+(N-1)*10:9000/tcp"
#       - "9000+(N-1)*10:9000/udp"
```

### Port allocation for 31 sims

| Sim | Container | UDP/TCP Port |
|-----|-----------|-------------|
| sim-1 | sim-1 | 9000 |
| sim-2 | sim-2 | 9010 |
| sim-3 | sim-3 | 9020 |
| ... | ... | ... |
| sim-31 | sim-31 | 9300 |

### List active sim sockets

```bash
# From host:
./list-sims.sh

# From inside monitor container:
docker exec dotnet-monitor ls -la /dotnet-sockets/*/dotnet-monitor.sock
```

### How dotnet-monitor finds all sims

The dotnet-monitor container mounts the shared `dotnet-sockets` volume (read-only) and sees all socket files. It auto-discovers processes with matching sockets:

```bash
# See all connected processes:
curl -u admin:changeme http://localhost:52323/processes

# See metrics for a specific sim:
curl "http://localhost:52325/metrics?processName=sim-1"
```

## dotnet-monitor — Why It Wasn't Working & How to Fix

### Root Cause

**OpenSim runs on .NET 8** (`TargetFramework: net8.0`, executed via `dotnet OpenSim.dll`).

dotnet-monitor could not connect because:

1. **Missing `DOTNET_DiagnosticPorts` env var** on the OpenSim process — without this, the app doesn't expose the IPC pipe that dotnet-monitor connects to
2. **Wrong runtime in Docker** — the entrypoint was using `mono OpenSim.exe` instead of `dotnet OpenSim.dll`
3. **No IPC socket sharing** — dotnet-monitor needs to reach the diagnostic port

### The Fix (already applied)

```bash
# The OpenSim process MUST have this env var:
export DOTNET_DiagnosticPorts=/tmp/dotnet-monitor.sock

# And must run via dotnet (NOT mono):
dotnet OpenSim.dll -inifile=OpenSim.ini
```

The fixed `entrypoint.sh` sets this automatically.

### How It Works

```
┌─────────────────────────────────┐
│ Docker Container (opensim)      │
│                                 │
│  OpenSim.dll (:8002 HTTP)       │
│    │                            │
│    ├── Exposes IPC socket:      │
│    │   /tmp/dotnet-monitor.sock │
│    │                            │
│  dotnet-monitor (:52323 API)    │
│    │                            │
│    ├── Connects to socket above │
│    ├── Collects: CPU, memory,   │
│    │   threads, exceptions,     │
│    │   GC, HTTP requests        │
│    └── Exposes metrics :52325   │
└─────────────────────────────────┘
```

### Endpoints

```bash
# Health check (no auth)
curl http://localhost:52323/health

# Process info (auth required)
curl -u admin:monitor2026 http://localhost:52323/processes

# Prometheus-compatible metrics
curl http://localhost:52325/metrics

# Full metrics with custom providers (System.Runtime, ASP.NET, OpenSim)
curl http://localhost:52325/metrics?app=OpenSim

# Trigger CPU trace dump manually
curl -X POST http://localhost:52323/collecttrace \
  -u admin:monitor2026 \
  -H "Content-Type: application/json" \
  -d '{"profile": "Cpu", "duration": "00:00:10"}'

# Trigger memory dump manually
curl -X POST http://localhost:52323/collectdump \
  -u admin:monitor2026 \
  -H "Content-Type: application/json" \
  -d '{"type": "Full"}'
```

### Auto-Capture Rules (configured)

| Rule | Trigger | Action |
|------|---------|--------|
| High CPU | > 80% for 5s | Auto-capture 30s CPU trace |
| High Memory | > 2GB working set | Auto-capture full dump |
| High Exceptions | > 50/sec | Auto-capture 15s trace |
| Slow Requests | > 5s avg duration | Auto-capture 20s HTTP trace |

### Without Docker (bare metal)

If running OpenSim directly on the host:

```bash
# Terminal 1: Start OpenSim with diagnostic port
export DOTNET_DiagnosticPorts=/tmp/dotnet-monitor.sock
export DOTNET_gcServer=1
cd bin/
dotnet OpenSim.dll -inifile=OpenSim.ini

# Terminal 2: Start dotnet-monitor
dotnet monitor collect \
  --urls http://+:52323 \
  --metricUrls http://+:52325 \
  --diagnostic-port /tmp/dotnet-monitor.sock \
  --auth Mode=Username
```

### OpenSim's Own Monitoring

OpenSim also has a built-in stats system (`StatsManager`) accessible via console:
```
stats show all          # Show all registered stats
stats show server       # Server stats (CPU, memory, threads)
stats show clientstack  # Network/packet queue stats
stats record start      # Start logging stats to file
stats save stats.json   # Save snapshot to file
```

## Quick Start (Docker Compose)

```bash
cd /home/marty/igrid-server-code-2026-07-03/sstats_fix/docker

# Setup
cp .env.example .env
nano .env  # Set passwords!

# Launch full stack
docker-compose up -d

# Dashboard: http://localhost:8080
# OpenSim HTTP: http://localhost:8002
# Monitor: http://localhost:52323

# View logs
docker-compose logs -f sstats-dashboard
docker-compose logs -f opensim
```
