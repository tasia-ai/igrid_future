# Quick-G Windows 10 compatibility helper and QUIC brain

Quick-G is the single, ROBUST-owned quic-go front end for installations whose
native .NET QUIC stack is unavailable on Windows 10. It preserves the existing
four-byte length-prefixed OpenSim QUIC transport and forwards each viewer
connection to the simulator QUIC endpoint registered through ROBUST.

Quick-G also provides an optional region brain. Regions configured with
`[ClientStack.Quic] Port = 0` ask the brain for a simulator-local QUIC listener
port before their listener starts. Leases are unique within the configured pool,
are kept alive by region heartbeats, and expire automatically after a crashed
region stops heartbeating. If Quick-G itself restarts, a running region can
re-adopt its current port with its next heartbeat.

## ROBUST startup safety

`[QuickG] Enabled = auto` starts `Quick-G.exe` only on Windows 10 (builds below
22000). `true` forces it and `false` disables it. ROBUST passes configuration on
the command line. On Windows, an automatically started Quick-G waits on a handle
to the exact ROBUST process, so a crash or forced termination immediately
cancels its listeners and active connections. Normal shutdown uses the loopback
control endpoint.

Quick-G can also be started manually by simply running `Quick-G.exe`. With no
`--parent-pid` it runs in manual standalone mode and waits for ROBUST to adopt
it through the control endpoint.

When `AllowNativeQuicFallback = false`, Quick-G is mandatory. If automatic
startup fails, interactive ROBUST startup pauses only the QUIC startup path and
shows a console banner. Start `Quick-G.exe` manually and press SPACE. ROBUST
checks again and remains paused until Quick-G is actually healthy. It does not
silently switch to native QUIC. Service/non-interactive launches fail clearly
instead of hanging on a console prompt.

This setting is a QUIC fallback only. It does not change, tunnel, replace or
otherwise alter the normal LLUDP transport.

## ROBUST configuration

ROBUST must load `QuicProxyConnector` in `[ServiceList]` and have
`[QuicProxy] Enabled = true`; otherwise neither its registration endpoints nor
the native/Quick-G proxy routing layer is initialized.

Example:

```ini
[QuicProxy]
    Enabled = true
    Port = 9001
    ALPN = opensim-ll/1
    CertificatePath = SSL/quic/quic-cert.pem
    PrivateKeyPath = SSL/quic/quic-key.pem

[QuickG]
    Enabled = auto
    Executable = Quick-G.exe
    Port = 9001
    ControlPort = 19001

    ; Quick-G brain. Keep 127.0.0.1 when all sims can reach ROBUST loopback
    ; (for example host-networked local sims). Use 0.0.0.0 only when remote or
    ; bridged simulator hosts must contact this private brain port directly.
    BrainBind = 127.0.0.1
    BrainPort = 19002
    BrainLeaseSeconds = 90

    ; Automatically assigned internal simulator QUIC listener pool.
    RegionPortStart = 22000
    RegionPortEnd = 22500
    RegionPortExclude = 22445

    ; false = pause ROBUST QUIC startup until Quick-G is healthy.
    AllowNativeQuicFallback = false

    CertificatePath = SSL/quic/quic-cert.pem
    PrivateKeyPath = SSL/quic/quic-key.pem
    ALPN = opensim-ll/1
```

The Quick-G control endpoint (`ControlPort`) always binds to loopback and owns
health, route registration and shutdown. The region brain uses its own port so
remote region access never exposes the shutdown endpoint.

## Region configuration

Simulators keep `[ClientStack.Quic]` enabled and point `ProxyRegistrationURL`
at the ROBUST private HTTP connector. Set `Port = 0` to let Quick-G assign the
region listener port:

```ini
[ClientStack.Quic]
    Enabled = true
    Port = 0
    ALPN = opensim-ll/1

    ProxyRegistrationURL = http://robust-host:8003/admin/quic/circuit

    ; Optional. If omitted, the hostname is derived from ProxyRegistrationURL
    ; and BrainPort is used. Set this explicitly for unusual routing/NAT setups.
    BrainURL = http://robust-host:19002
    BrainPort = 19002
    BrainHeartbeatSeconds = 30
```

A fixed non-zero `Port` keeps the old behavior and does not request a brain
lease.

## Build/deployment

The Windows CI job compiles the helper and places it beside `Robust.exe` before
creating the normal release ZIP. Go is only a build dependency and is not
required on the deployed server.
