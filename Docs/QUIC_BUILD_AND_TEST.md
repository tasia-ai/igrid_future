# QUIC backend build and test guide

## Targeted builds

Full solution builds may fail because unrelated optional modules are not clean.
Use targeted builds for the QUIC/server work:

```bash
cd /mnt/c/new/backend

dotnet build OpenSim/Services/LLLoginService/OpenSim.Services.LLLoginService.csproj -c Release
dotnet build OpenSim/Region/ClientStack/Linden/Caps/OpenSim.Region.ClientStack.LindenCaps.csproj -c Release
dotnet build OpenSim/Region/ClientStack/Linden/UDP/OpenSim.Region.ClientStack.LindenUDP.csproj -c Release
```

Expected result: `0 errors`. Existing warnings may remain.

## Docker all-in-one test grid

The test grid runs ROBUST, one region, MariaDB, HTTPS, and QUIC in one
container.

```bash
cd /mnt/c/new/backend
docker compose -f docker-compose.quic-test.yml down
docker compose -f docker-compose.quic-test.yml build opensim-all-in-one
docker compose -f docker-compose.quic-test.yml up -d opensim-all-in-one
```

Check logs:

```bash
docker compose -f docker-compose.quic-test.yml logs --no-color --tail=300 opensim-all-in-one
```

Expected readiness lines:

```text
[QuicServer] QUIC listener started on port 9001 with ALPN 'opensim-ll/1'
[REGION]: Enabling logins for Test Region
```

## Local test login

```text
Login URI: https://localhost:8002
User: Test User
Password: test123
Start: Home
```

## Local certificate notes

The Docker runner prefers these files if present:

```text
docker/quic-test/certs/quic-cert.pem
docker/quic-test/certs/quic-key.pem
```

It copies them to:

```text
bin/SSL/quic/quic-cert.pem
bin/SSL/quic/quic-key.pem
bin/SSL/quic/quic-cert.p12
```

For Firestorm/Tasia Viewer compatibility, local test certs must include SKI/AKI.

## Smoke test HTTPS and login

```bash
python3 - <<'PY'
import ssl, xmlrpc.client, urllib.request

ctx = ssl.create_default_context(cafile='/mnt/c/new/backend/docker/quic-test/certs/mkcert-rootCA.pem')

with urllib.request.urlopen('https://localhost:8002/get_grid_info', context=ctx, timeout=8) as r:
    print('gridinfo', r.status, r.read(300).decode('utf-8', 'replace'))

s = xmlrpc.client.ServerProxy('https://localhost:8002/', context=ctx, allow_none=True)
resp = s.login_to_simulator({
    'first': 'Test',
    'last': 'User',
    'passwd': 'test123',
    'start': 'home',
    'version': 'Tasia smoke-test',
    'channel': 'Tasia smoke-test',
    'mac': '00:00:00:00:00:00',
    'id0': '00000000-0000-0000-0000-000000000000'
})

print('login=', resp.get('login'))
print('seed_capability=', resp.get('seed_capability'))
print('sim_quic_host=', resp.get('sim_quic_host'))
print('sim_quic_port=', resp.get('sim_quic_port'))
PY
```

Expected:

```text
login= true
seed_capability= https://localhost:9002/CAPS/...
sim_quic_host= localhost
sim_quic_port= 9001
```

## Troubleshooting viewer certificate errors

Viewer log path:

```text
/home/marty/.firestorm_x64/logs/Firestorm.log
```

Important errors:

```text
Certificate Error: No Subject Key Id
SSL peer certificate or SSH remote key was not OK (Easy_51)
```

Fix: regenerate the server cert with Subject Key Identifier and Authority Key
Identifier, then restart the grid.

## Docker disk cleanup

If the Docker test image hits disk limits:

```bash
docker builder prune -f
docker container prune -f
docker volume prune -f
```
