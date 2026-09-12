# Quick-G Windows 10 compatibility helper

Quick-G is the single, ROBUST-owned quic-go front end for installations whose
native .NET QUIC stack is unavailable on Windows 10. It preserves the existing
four-byte length-prefixed OpenSim QUIC transport and forwards each connection
to the simulator QUIC endpoint registered through ROBUST.

`[QuickG] Enabled = auto` starts `Quick-G.exe` only on Windows 10 (builds below
22000). `true` forces it and `false` disables it. ROBUST passes configuration on
the command line and keeps the write end of an anonymous stdin pipe. Quick-G
watches the read end; normal shutdown, a crash, or forced termination closes
the pipe and immediately cancels both listeners and active connections. Quick-G
uses no PID or lock file, so a subsequent ROBUST launch has no stale state.

The Windows CI job compiles the helper and places it beside `Robust.exe` before
creating the normal release ZIP. Go is only a build dependency and is not
required on the deployed server.
