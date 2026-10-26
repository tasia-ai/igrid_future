# InternalScriptRpc deployment and upgrade guide

## Transport model

Firestorm's existing HTTP listener does not need to change. For this single-host
grid, RPC uses separate native OpenSim HTTPS listeners over TCP, bound to
loopback only:

1. Region caller to Robust: `RobustURL` on a TLS-enabled Robust listener.
2. Robust to destination region: the region's HTTP `ServerURI` host plus
   `RegionHttpsPortOffset`, or an already HTTPS `ServerURI` when its port is the
   RPC listener.

QUIC is a separate UDP transport and its TLS settings do not enable HTTPS.
OpenSim's native listeners need their own certificate configuration. Use a
PKCS#12 certificate with its private key, a trusted complete chain, and SANs
matching the Robust and region DNS names. Do not disable TLS validation or use
external TLS termination to a plaintext OpenSim listener. With
`LoopbackOnly=true`, RPC outbound sockets connect to `127.0.0.1` while TLS still
validates the configured DNS hostname. This mode requires Robust and all region
processes to run on the same machine; it requires no router/NAT port forwarding
or Windows Firewall openings. Do not use it for a multi-host grid.

## Required runtime version

This source tree currently targets **.NET 10** (`Directory.Build.props` and the
SmartNPC project explicitly target `net10.0`). The live Robust host's
`Robust.runtimeconfig.json` targets .NET 10, but the region host selected by the
launcher was verified to use `OpenSim.runtimeconfig.json` targeting **.NET
8.0.26**. Do not copy .NET 10 assemblies into that .NET 8 region runtime: the
repo's region dependency graph also targets .NET 10. The attempted net8 build
failed with framework-reference incompatibilities across that graph.

Upgrade the region runtime as a complete, matching .NET 10 publish; do not
change only `Phlox.ScriptEngine.dll` or the RPC addon. The region publish must
contain the OpenSim host, `Phlox.ScriptEngine.dll`, `OpenSim.Region.OptionalModules.AI.dll`,
`TasiaAddons.InternalScriptRpc.dll`, and their matching dependency closure. The
region host project now references the SmartNPC module so its current assembly
is included in the publish output. Build Robust from the same source revision
and deploy its matching GridServer/framework output as well, so the Robust
loopback binding and RPC connector agree with the region build.

From the repository root, build/publish both hosts to staging directories:

```powershell
dotnet publish Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj `
  -c Release -o artifacts/region-net10
dotnet publish Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj `
  -c Release -o artifacts/robust-net10
```

Before deploying, verify the region `OpenSim.runtimeconfig.json` and the
GridServer publish's `OpenSim.Server.GridServer.runtimeconfig.json` both target
.NET 10, and confirm the required files exist in the region output. The current
manager launches a `Robust.dll` host, whereas this repository's GridServer
publish emits `OpenSim.Server.GridServer.dll`/`.exe`; confirm how the active
host consumes that output before replacing anything. Do not rename an executable
or copy a GridServer publish over the live Robust folder blindly. Identify the
actual launch paths from manager process command lines; do not assume the
staging tree or folder name identifies the runtime. Stop services, back up each
active `bin` directory, then overlay only the compatible matching publish
output. Preserve runtime data, region configs, secrets, PFX files, and manager
configuration. Never mix one host's publish output with the other's assemblies.

## Port plan for the current single-host grid

The checked-in source does not contain production secrets or runtime ini files.
The currently installed grid uses Robust's `PrivatePort=22001` and public
`Port=22002`; its region HTTP listeners use `22033`-`22062` and `22341` (with
some lower ports absent). The RPC plan keeps those listeners unchanged:

The 2026-10-26 attempt exposed a runtime mismatch: the launcher selected the
.NET 8 region host and every region stopped with
`LoopbackOnly is enabled but the HTTPS listener is not bound to loopback`.
The 32 region ini files were restored from backup after that failure. The
loopback-only Robust settings remain in `H:\grid_final\Robust.HG.fixed.ini`, but
the launcher's actual Robust INI path still needs confirmation. Do not enable RPC
again until the matching .NET 10 hosts are deployed and startup/listener checks
pass. A real cross-region teleport test is still required.

| Listener | Port plan |
| --- | --- |
| Robust RPC HTTPS | `22003` |
| Region RPC HTTPS | each existing region HTTP port + `10000` |
| Region port examples | `22041 -> 32041`; `22062 -> 32062`; `22341 -> 32341` |

One region has no explicit `http_listener_port` and inherits `9000`, so its RPC
listener is `19000`.

The fixed Robust config's `[ServiceList]` RPC entry is set to `22003/...`, and
`RegionHttpsPortOffset=10000`. For every region process, set
`InternalScriptRpc.HttpsPort` to its own HTTP port plus `10000`. The region
config's `[Network] https_port` must equal that `HttpsPort`. Assign a unique
port for any region process that has no explicit `http_listener_port`; do not
let multiple processes bind the same address/port. The live port range was
checked for conflicts before configuration. `LoopbackOnly=true` keeps RPC
listeners inaccessible from external interfaces; do not add firewall/NAT
exposures for them.

## Listener and addon config

These examples are templates: use the actual paths and protect the PFX password
and signing key as secrets. Use the same strong random signing key,
`MinimumAdminUserLevel=150`, and any manual owner UUID entries on Robust and
every participating region. Robust and regions need a working
`UserAccountService` for level-based authorization. An empty `AllowedOwners` is
safe only when that service is available; failed account lookups never authorize
an RPC. Do not commit live secrets or passwords.

On Robust, enable a native HTTPS listener in `[Network]` (preserving existing
HTTP ports):

```ini
[Network]
    https_listener = true
    https_external = false
    https_port = 22003
    cert_path = "<absolute-path-to-rpc-certificate.p12>"
    cert_pass = "<PFX-password>"

[ServiceList]
    InternalScriptRpc = 22003/TasiaAddons.InternalScriptRpc.dll:InternalScriptRpcConnector

[InternalScriptRpc]
    Enabled = true
    SigningKey = <same-32-byte-minimum-random-secret>
    AllowedOwners = <optional-manual-owner-uuid-list>
    MinimumAdminUserLevel = 150
    RegionHttpsPortOffset = 10000
    LoopbackOnly = true
```

For each region process, retain its existing HTTP listener and add its separate
native HTTPS listener. Example for a process with `http_listener_port=22041`:

```ini
[Network]
    ssl_listener = true
    ssl_external = false
    https_port = 32041
    cert_path = "<absolute-path-to-rpc-certificate.p12>"
    cert_pass = "<same-PFX-password>"

[InternalScriptRpc]
    Enabled = true
    HttpsPort = 32041
    SigningKey = <same-32-byte-minimum-random-secret>
    AllowedOwners = <same-optional-manual-owner-uuid-list>
    MinimumAdminUserLevel = 150
    LoopbackOnly = true
    RobustURL = https://ok.tasia.work.gd:22003
```

Use a cert whose SAN includes both `ok.tasia.work.gd` and `os.tasia.work.gd` (or
the actual configured names). The grid may retain its existing HTTP region
`ServerURI`; Robust maps only the RPC request to the offset port. Confirm that
the Robust URL and region hostnames resolve and the certificates validate from
each calling/destination process.

If reusing the current QUIC PEM certificate/key pair, first verify its dates and
SANs, then create a PKCS#12 copy for the OpenSim HTTP listeners using OpenSSL's
interactive password prompt, for example:
`openssl pkcs12 -export -in quic-cert.pem -inkey quic-key.pem -out rpc-cert.p12`.
Do not put the password on a command line or commit the resulting PFX/private
key. QUIC and OpenSim HTTP use separate certificate configuration; the PFX is
only a format conversion of the same certificate identity.

## Safe upgrade and rollback

1. Stop all Robust and region processes. Confirm no process has the deployed
   assemblies loaded.
2. Back up the complete active Robust and region `bin` directories.
3. Deploy the matching .NET 10 publish outputs as described above. Do not copy
   tests, PFX files, passwords, or signing keys from the source tree.
4. Apply the listener and per-process port configuration above, retaining
   encrypted/backup copies of the modified ini files. Verify plugin discovery
   and startup log messages, and verify the TLS handshake/hostname and route
   on Robust and a region before allowing cross-region operations.
5. Start services, make a bounded authenticated test between two regions, then
   test one SmartNPC teleport. Do not describe the feature as end-to-end verified
   until the actual teleport succeeds.
6. If startup or tests fail, stop services, restore the backed-up `bin` directories
   and ini files, and restart. Keep RPC `Enabled=false` until certificates, URLs,
   ports, signing key, and owner allowlist are all set correctly.

Official reference: [OpenSimulator Robust configuration](https://opensimulator.org/wiki/Configuration/files/Robust/Robust.HG.ini).
