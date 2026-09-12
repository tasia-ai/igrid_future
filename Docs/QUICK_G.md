# Quick-G Windows 10 compatibility helper

Quick-G is the single, ROBUST-owned quic-go front end for installations whose
native .NET QUIC stack is unavailable on Windows 10. It preserves the existing
four-byte length-prefixed OpenSim QUIC transport and forwards each connection
to the simulator QUIC endpoint registered through ROBUST.

`[QuickG] Enabled = auto` starts `Quick-G.exe` only on Windows 10 (builds below
22000). `true` forces it and `false` disables it. ROBUST passes configuration on
the command line. On Windows, Quick-G waits on a handle to the exact ROBUST
process, so a crash or forced termination immediately cancels both listeners and
active connections without the premature-stdin-EOF behavior of console/service
launchers. Normal shutdown uses the loopback control endpoint. Quick-G uses no
PID or lock file, so a subsequent ROBUST launch has no stale state.

The Windows CI job compiles the helper and places it beside `Robust.exe` before
creating the normal release ZIP. Go is only a build dependency and is not
required on the deployed server.

ROBUST must also load `QuicProxyConnector` in `[ServiceList]` and have
`[QuicProxy] Enabled = true`; otherwise neither its registration endpoints nor
the native/Quick-G proxy routing layer is initialized. Simulators keep their
`[ClientStack.Quic]` section enabled and point `ProxyRegistrationURL` at the
ROBUST private HTTP port. `[QuickG]` itself belongs only in the ROBUST config.

On Windows, `AllowNativeQuicFallback` defaults to `false`. If Quick-G cannot
start, an interactive ROBUST launch explains the failure and waits for SPACE,
then checks for a manually started helper and retries automatic startup once.
Service/non-interactive launches fail clearly instead of silently selecting a
native QUIC implementation that is unavailable on Windows 10. Set the option to
`true` only on a machine where native QUIC is known to work (for example Windows
11).
