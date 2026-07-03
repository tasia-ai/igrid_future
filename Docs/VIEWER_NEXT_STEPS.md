# Viewer next steps after backend upload

After backend HTTPS + QUIC support is uploaded, viewer work should be handled in
small, testable changes.

## Current backend contract

The backend advertises QUIC using these login response keys:

```text
sim_quic_host
sim_quic_port
```

And event queue messages may include the same fields for region transitions:

```text
EnableSimulator
TeleportFinish
CrossedRegion
```

Viewer QUIC framing:

```text
4-byte big-endian payload length
raw LLUDP packet bytes
```

ALPN:

```text
opensim-ll/1
```

Server readiness signal:

```text
GenericMessage("quicready")
```

## Small viewer fixes to review

1. Ensure HTTPS GridInfo/login works with local and production CA bundles.
2. Ensure QUIC status UI distinguishes:
   - no simulator circuit
   - legacy LLUDP
   - QUIC connected but not ready
   - QUIC ready
3. Keep fallback policy explicit:
   - if server advertises QUIC and QUIC fails, do not silently fall back unless
     config allows it.
   - if server does not advertise QUIC, use legacy LLUDP.
4. Make logs clear for:
   - certificate failure
   - GridInfo failure
   - QUIC connect failure
   - missing `quicready`
   - region transition QUIC endpoint changes

## Platform build order

Recommended order:

1. Linux x64 local developer build.
2. Windows x64 build.
3. macOS build.

Do not start all-platform packaging until Linux passes:

- HTTPS GridInfo
- HTTPS login
- HTTPS CAPS
- QUIC first region attach
- teleport/cross-region handoff
- legacy LLUDP grid login when QUIC is not advertised

## Linux validation commands

Viewer logs:

```text
/home/marty/.firestorm_x64/logs/Firestorm.log
```

Backend logs:

```bash
cd /mnt/c/new/backend
docker compose -f docker-compose.quic-test.yml logs --no-color --tail=500 opensim-all-in-one
```

## Release checklist

- Backend docs included.
- Viewer release notes mention HTTPS + QUIC requirements.
- CA/certificate requirements documented.
- Legacy LLUDP compatibility tested.
- QUIC test grid tested from a clean viewer profile.
- macOS/Windows/Linux builds use the same QUIC protocol constants.
