# HTTPS + QUIC deployment guide

This guide explains how to enable HTTPS login/CAPS and QUIC simulator transport
for Tasia Viewer/OpenSim deployments.

## Requirements

- .NET 8 runtime/SDK for OpenSim.
- Linux hosts running QUIC need `libmsquic` installed.
- A real trusted TLS certificate for production.
- DNS names used by viewers must match certificate CN/SAN values.

## Certificate requirements

Viewer certificate validation requires:

- Common Name matching the hostname used by the viewer.
- Subject Alternative Name for the same hostname/IPs.
- Subject Key Identifier extension.
- Authority Key Identifier extension.
- Key Usage including `Digital Signature`.
- Extended Key Usage including `TLS Web Server Authentication`.

For production, use a public CA certificate, for example Let's Encrypt.

For local testing, `mkcert` works if the generated certificate includes SKI/AKI.

## ROBUST HTTPS login/GridInfo

In `Robust.ini`:

```ini
[Const]
BaseHostname = "grid.example.com"
BaseURL = "https://${Const|BaseHostname}"
PublicPort = "8002"

[Network]
port = ${Const|PublicPort}
https_main = True
cert_path = "SSL/quic/quic-cert.p12"
cert_pass = "change-me"

[GridInfoService]
login = ${Const|BaseURL}:${Const|PublicPort}/
gridname = "Your Grid"
gridnick = "yourgrid"
```

Viewer login URI:

```text
https://grid.example.com:8002
```

## Region HTTPS CAPS

In `OpenSim.ini`:

```ini
[Const]
BaseHostname = "grid.example.com"
BaseURL = "https://${Const|BaseHostname}"

[Network]
http_listener_port = 9000
http_listener_ssl = true
http_listener_sslport = 9002
http_listener_cn = grid.example.com
http_listener_cert_path = "SSL/quic/quic-cert.p12"
http_listener_cert_pass = "change-me"
ExternalHostNameForLSL = ${Const|BaseHostname}
```

OpenSim should advertise HTTPS CAPS in the login response:

```text
seed_capability = https://grid.example.com:9002/CAPS/...
```

## Region hostname

In `Regions.ini`:

```ini
[Region Name]
InternalAddress = 0.0.0.0
InternalPort = 9000
ExternalHostName = grid.example.com
RegionType = DefaultRegion, FallbackRegion
```

## QUIC transport

In `OpenSim.ini` and ROBUST config used by `LLLoginService`:

```ini
[ClientStack.Quic]
Enabled = true
Port = 9001
Alpn = "opensim-ll/1"
CertificatePath = "SSL/quic/quic-cert.pem"
PrivateKeyPath = "SSL/quic/quic-key.pem"

AdvertiseInLoginResponse = true
AdvertiseInEventQueue = true
AdvertiseHost = "grid.example.com"
AdvertisePort = 9001
```

The login response should contain:

```text
sim_quic_host = grid.example.com
sim_quic_port = 9001
```

## Firewall ports

Open these viewer-facing ports:

```text
8002/tcp  ROBUST HTTPS login/GridInfo
9002/tcp  HTTPS CAPS
9001/udp  QUIC simulator transport
```

Keep legacy LLUDP only if fallback is desired:

```text
9000/udp  LLUDP legacy simulator transport
```

## Validation checklist

```bash
curl https://grid.example.com:8002/get_grid_info
openssl s_client -connect grid.example.com:8002 -servername grid.example.com
openssl s_client -connect grid.example.com:9002 -servername grid.example.com
```

Expected viewer behavior:

- GridInfo loads over HTTPS.
- Login succeeds.
- `seed_capability` is HTTPS.
- QUIC connection opens to advertised host/port.
- Viewer shows QUIC encrypted simulator status.
