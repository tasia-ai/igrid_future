# InternalScriptRpc deployment and upgrade guide

## Transport model

Firestorm's existing HTTP listener does not need to change. RPC uses a separate,
native OpenSim HTTPS listener over TCP on both legs:

1. Region caller to Robust: `RobustURL` on a TLS-enabled Robust listener.
2. Robust to destination region: the region's HTTP `ServerURI` host plus
   `RegionHttpsPortOffset`, or an already HTTPS `ServerURI` when its port is the
   RPC listener.

QUIC is a separate UDP transport and its TLS settings do not enable HTTPS.
OpenSim's native listeners need their own certificate configuration. Use a
PKCS#12 certificate with its private key, a trusted complete chain, and SANs
matching the Robust and region DNS names. Do not disable TLS validation or use
external TLS termination to a plaintext OpenSim listener.

## Port plan for the current grid (proposed; not yet enabled)

The checked-in source does not contain production secrets or runtime ini files.
The currently installed grid uses Robust's `PrivatePort=22001` and public
`Port=22002`; its region HTTP listeners use `22033`-`22061` and `22341` (with
some lower ports absent). The RPC plan keeps those listeners unchanged:

Audit performed 2026-10-26 found no native HTTPS listener configured in the
current Robust or region ini files. These are planned RPC ports, not verified
reachable ports. Keep the addon disabled until the HTTPS listeners, firewall/
NAT path, certificate, signing key, and owner allowlist are configured and
verified.

| Listener | Port plan |
| --- | --- |
| Robust RPC HTTPS | `22003` |
| Region RPC HTTPS | each existing region HTTP port + `10000` |
| Region port examples | `22041 -> 32041`; `22341 -> 32341` |

Set Robust's `[ServiceList]` RPC entry to `22003/...` and
`RegionHttpsPortOffset=10000`. For every region process, set
`InternalScriptRpc.HttpsPort` to its own HTTP port plus `10000`. The region
config's `[Network] https_port` must equal that `HttpsPort`. Assign a unique
port for any region process that has no explicit `http_listener_port`; do not
let multiple processes bind the same address/port. Before enabling the module,
confirm the range is unused, and configure Windows Firewall and edge NAT/firewall
for TCP 22003 and the region HTTPS ports. Only open these ports if required by
the actual Robust-to-region routing topology.

## Listener and addon config

These examples are templates: use the actual paths and protect the PFX password
and signing key as secrets. Use the same strong random signing key and
non-empty, exact owner UUID allowlist on Robust and every participating region.
Do not commit live secrets or passwords.

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
    AllowedOwners = <explicit-owner-uuid-list>
    RegionHttpsPortOffset = 10000
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
    AllowedOwners = <same-explicit-owner-uuid-list>
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
2. Back up the existing `OpenSim.Framework.dll`, `OpenSim.Region.Framework.dll`,
   and `OpenSim.Region.OptionalModules.dll` before replacing them.
3. Copy those assemblies and `TasiaAddons.InternalScriptRpc.dll` plus its
   `.deps.json` into the OpenSim runtime `bin` directory. Do not copy tests, PFX
   files, passwords, or signing keys from the source tree.
4. Apply the listener and per-process port configuration above, retaining
   encrypted/backup copies of the modified ini files. Verify plugin discovery
   and startup log messages, and verify the TLS handshake/hostname and route
   on Robust and a region before allowing cross-region operations.
5. Start services, make a bounded authenticated test between two regions, then
   test one SmartNPC teleport. Do not describe the feature as end-to-end verified
   until the actual teleport succeeds.
6. If startup or tests fail, stop services, restore the backed-up assemblies and
   ini files, and restart. Keep RPC `Enabled=false` until certificates, URLs,
   ports, signing key, and owner allowlist are all set correctly.

Official reference: [OpenSimulator Robust configuration](https://opensimulator.org/wiki/Configuration/files/Robust/Robust.HG.ini).
