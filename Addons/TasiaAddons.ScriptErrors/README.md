# Script errors page (MVP)

Build `TasiaAddons.ScriptErrors.dll`, copy it beside the GridServer assemblies, enable the commented `ScriptErrorsConnector` entry in `[ServiceList]` in `GridServer.ini`, and configure:

```ini
[ScriptErrors]
Enabled = true
; Generate at least 32 random bytes; keep private and use HTTPS.
SigningKey = replace-with-at-least-32-random-bytes
MaxIngestPerMinute = 120
; Use a directory on shared storage writable by every Robust instance.
FeedFile = /srv/grid/shared/script-errors.jsonl
```

The central page is `/scripterrors`. Any authenticated grid account can see the global feed. Passwords are submitted only to the grid authentication service and are never stored or logged. Successful authentication creates a random, server-side, 30-minute session; the HttpOnly/SameSite cookie carries only its opaque identifier. Serve the endpoint over HTTPS. The bounded feed is newest-first, 50 rows per page, searches across region/engine/script/error, and retains at most 5,000 events. All displayed values are HTML-encoded. No source code field is accepted or rendered.

`/scripterrors/ingest` accepts POST only when `SigningKey` is configured. The exact UTF-8 JSON body is signed as HMAC-SHA256 over `unixTimestamp + "\n" + nonce + "\n" + body`; send the timestamp in `X-Script-Errors-Timestamp`, a fresh unpredictable nonce in `X-Script-Errors-Nonce`, and lowercase/uppercase hex digest in `X-Script-Errors-Signature`. The signed body has `Region`, `Engine`, `Script`, and `Message` fields. Requests outside a two-minute clock window, duplicate nonces, oversized bodies, invalid signatures, and requests over the configured rate are rejected. Leave `SigningKey` empty to disable ingestion. Use a different secret per trusted publisher where operationally possible; the verifier currently has one shared key. This endpoint does not trust loopback or proxy headers.

For each region process, set `ScriptErrorsURL` and `ScriptErrorsSigningKey` to the same secret under `[YEngine]` and `[InWorldz.Phlox]`. Use `https://<grid-host>/scripterrors/ingest` for a TLS endpoint, or `http://127.0.0.1:<Robust-private-port>/scripterrors/ingest` when all processes share one host. The reporter refuses non-loopback HTTP and sends sanitized runtime-error summaries on a bounded background queue; no secret means reporting is disabled. Only YEngine/Phlox runtime errors are captured (not compile diagnostics).

Messages are for operator diagnostics only: callers must send a short, sanitized summary, never exception objects, stack traces, source text, or user-provided script contents. Fields are bounded and control characters are removed before storage. The JSONL feed and replay/rate-limit state are process-local; do not run multiple writers against one feed file without external serialization. Feed sessions are held in each Robust process, so logins are instance-affine unless the HTTP front end has sticky sessions.
