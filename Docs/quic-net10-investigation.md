# QUIC on .NET 10 - investigation notes

## Status

The grid runs the pre-cutover build `0.9.3.0` (net9 regions, net8 Robust) restored
from `H:\grid\backups\programhome-bin-20261002-194326`. QUIC works there in the Tasia
viewer. The .NET 10 build does not get past connection establishment.

## Proven by measurement

| Fact | Evidence |
|---|---|
| QUIC listener binds on every region | `Get-NetUDPEndpoint` shows 31 listeners on 22200-22231; `[QuicServer] QUIC listener started on port 22208 with ALPN 'opensim-ll/1'` |
| Certificate is valid and matches the host | `CN=ok.tasia.work.gd`, SAN `os.tasia.work.gd`, Let's Encrypt, valid 2026-09-13..2026-12-12, full chain present in PEM |
| Regions advertise their own endpoint | `[QuicServer] RegionInfo QUIC endpoint set for Fresh01: os.tasia.work.gd:22208` |
| The module does feed the host | `QuicServerModule.cs:668` calls `m_udpServer.ProcessIncomingQuicPacket(payload, transport)` |
| The host sends `quicready` | `LLUDPServer.cs:1415` sends it as soon as a `UseCircuitCode` arrives over QUIC |
| Ports are reachable from outside | The viewer reaches `222xx` successfully against the old build |
| Same cert in both locations | `H:\grid\igrid-package\bin\SSL\quic\quic-cert.pem` and `...\opensim-base\SSL\quic\quic-cert.pem` are equivalent |

**Conclusion:** everything observable server-side is correct on .NET 10 except the
completion of the QUIC/TLS connection itself. `quicready` is sent from inside the
`UseCircuitCode` path, so if the viewer reports "waiting for connection to region",
no connection was ever established and no packet ever reached us.

## Verified defects already fixed in this branch

- `LocalConsole`: end of input was reported as `Invalid command` in a loop. 31 regions
  wrote 136 GB and pinned the box at 100% CPU. Fixed in `ef48a3a8af`.
- Plugin load contexts: the client stack was loaded twice (once as a region module
  plugin, once as a plugin dependency), so `is LLUDPServerShim` was permanently false.
  Fixed in `461c965398`, guarded by `Tests/OpenSim.PluginLoadContext.Tests`.
- Deployment parity assertion in `tools/deploy-region-server.ps1`.

## Remaining suspects, all inside the .NET 10 QUIC stack

1. `msquic` availability in the **self-contained** publish. A framework-dependent
   host uses the OS copy; a self-contained one must ship and find `msquic.dll`.
   Binding succeeds, so it loaded, but the negotiated version may still differ.
2. ALPN negotiation. `QuicListenerOptions.ApplicationProtocols` and
   `SslServerAuthenticationOptions.ApplicationProtocols` both advertise
   `opensim-ll/1`. On .NET 10 the `ConnectionOptionsCallback` result governs; if the
   viewer offers a different ALPN the connection is dropped. Nothing logs the
   ClientHello, so this is unverified.
3. `ConnectionOptionsCallback` behaviour differs on .NET 10 (it is called per
   handshake and `LoadCertificateContext()` runs each time).

## First task for the rewrite

Log the handshake instead of guessing. In `AcceptLoopAsync`, log every exception from
`AcceptConnectionAsync` with its `QuicError` and numeric code, and in
`OnConnectionOptions` log the ALPNs offered in the ClientHello alongside the one we
answer with. Also log the resolved `msquic` version at listener start.

That turns "the viewer stalls" into a named failure, and every one of the suspects
above becomes observable in one region start instead of a full grid restart.

## Design decisions for the native (non-proxy) module

- Keep `PacketFraming`, `QuicClientConnection`, `QuicViewerTransport` (tested layer).
- Rewrite `QuicServerModule`: bind, accept, forward inbound payloads to
  `ProcessIncomingQuicPacket`, publish `RegionInfo.QuicHost/QuicPort`, send nothing
  about proxy or brain.
- Drop `QuicProxyConnector` (1613 lines, Robust side) - not used by native transport.
- Trim `QuicServerConfig`: remove brain lease, proxy registration and the hardcoded
  `H:\grid\igrid-package\bin\SSL\quic` preference. A certificate that cannot be loaded
  must fail loudly, never fall back to a silent self-signed one.