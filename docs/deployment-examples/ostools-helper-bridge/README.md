# OpenSim Helper Bridge (ostools) - Deployment Notes

This folder documents the working `ostools` bridge setup used in production for viewer money/land helper calls.

## Purpose

Expose viewer-compatible endpoints:

- `https://<host>/ostools/currency.php`
- `https://<host>/ostools/landtool.php`

while proxying internally to a Python bridge on `127.0.0.1:8015`.

## Files in this example

- `config.example.json` - bridge config template (no secrets)
- `opensim-helper-bridge.service.example` - systemd unit
- `php/bridge_proxy.php` - PHP proxy helper
- `php/currency.php` - currency endpoint shim
- `php/landtool.php` - land endpoint shim
- `php/healthz.php` - bridge health endpoint
- `bridge/custom_backend.py` - legacy helper passthrough backend
- `legacy-helper-compat-notes.md` - required legacy helper fixes for schema/function mismatches

## Minimal deployment flow

1. Deploy bridge app files to web host, e.g.:
   - `/var/www/html/web/ostools/`
2. Copy `config.example.json` to `config.json` and set values:
   - `public_base_url`
   - `helper_secret`
   - `mode` (`custom` for legacy passthrough shown here)
3. Install PHP shims (`currency.php`, `landtool.php`, `bridge_proxy.php`, `healthz.php`) into `/var/www/html/web/ostools/`.
4. Install systemd unit (example provided), then start service.
5. Set economy helper URI in OpenSim config:

```ini
[Economy]
EconomyHelper = "https://i.let-us.cyou/ostools/"
```

6. Validate:

```bash
curl https://i.let-us.cyou/ostools/healthz.php
```

Expected: JSON with `"ok": true`.

## Notes

- Keep secrets out of repository (`helper_secret`, DB credentials, tokens).
- Use placeholders only in committed files.
- Web/helper service restart is sufficient for bridge code/config changes.
- Region restart is only needed when changing region-side OpenSim INI values (like `EconomyHelper`).
