# OpenSim Helper Bridge for Firestorm land sales and buy-currency

This pack gives you a small Python service that exposes the helper endpoints Firestorm expects:

- `/currency.php`
- `/landtool.php`

The bridge speaks XML-RPC to the viewer and can do one of three things behind the scenes:

1. **xmlrpc_passthrough** — forward the same helper calls to an upstream XML-RPC money server, good first test for `http://127.0.0.1:1026/`
2. **rest_json** — translate the helper calls into JSON REST requests to your own money API
3. **custom** — edit `bridge/custom_backend.py` for whatever weird format your money server uses

There is also a **noop** mode for viewer/UI testing, but it does **not** mint real money unless you explicitly allow it.

## Files

- `app.py` — launcher
- `bridge/server.py` — XML-RPC HTTP server and dispatcher
- `bridge/backends.py` — upstream backend adapters
- `bridge/custom_backend.py` — fill this in if your money server API is custom
- `bridge/im_notify.py` — optional IM notifications based on the uploaded `send_im.php` flow
- `config.example.json` — example config
- `examples/nginx.conf` — reverse proxy config for `os.tasia.work.gd` (Fresh Metaverse)
- `examples/opensim-config-snippets.ini` — minimal OpenSim helper URI example
- `examples/opensim-helper-bridge.service` — systemd unit
- `tests/smoke_test.py` — local smoke test

## Quick start

```bash
cp config.example.json config.json
# edit config.json
python3 app.py
```

Health check:

```bash
curl http://127.0.0.1:8015/healthz
```

### First thing to try on your grid

Set this in `config.json`:

```json
{
  "mode": "xmlrpc_passthrough",
  "upstream_xmlrpc_url": "http://127.0.0.1:1026/",
  "public_base_url": "https://os.tasia.work.gd/ostools/"
}
```

Then reverse proxy (Apache or nginx):

- `https://os.tasia.work.gd/ostools/currency.php` -> `http://127.0.0.1:8015/currency.php`
- `https://os.tasia.work.gd/ostools/landtool.php` -> `http://127.0.0.1:8015/landtool.php`

If your money server on `1026` already understands the helper XML-RPC methods, that might be enough.

## Confirm tokens

The bridge generates a signed `confirm` token on quote/preflight and validates it on buy calls. This stops random spoofed follow-up requests from succeeding.

Disable only for testing:

```json
{
  "verify_confirm": false
}
```

## Optional in-world IM notifications

Set `notifier.enabled = true` and fill in the OAuth details if you want helper successes to send IMs using your existing `send_im` pattern.

## Notes about “unlimited buying”

There are two very different meanings here:

1. **Viewer/UI flow works without “insufficient funds” errors**
2. **Actual balances get credited with no cap**

This bridge only handles the helper flow. Real balances, caps, and ledger safety still belong in your money server or region money module.

If you just want to test the viewer path, you can temporarily use:

```json
{
  "mode": "noop",
  "allow_noop_buy_currency": true,
  "allow_noop_land_buy": true,
  "verify_confirm": false
}
```

But that is **not** a secure production economy.

## Adapting to your real money server

### Case A: money server on 1026 already speaks XML-RPC

Leave the bridge in `xmlrpc_passthrough` mode.

### Case B: money server on 1026 speaks JSON REST

Use `rest_json` and fill in the endpoint URLs.

### Case C: money server on 1026 is custom or ugly

Use `custom` mode and edit `bridge/custom_backend.py`.

## Deployment

1. Copy this folder to `/opt/opensim_helper_bridge`
2. Put your real config in `/opt/opensim_helper_bridge/config.json`
3. Install the systemd unit from `examples/opensim-helper-bridge.service`
4. Put the nginx snippet in your site config
5. Set your OpenSim grid helper URI to `https://os.tasia.work.gd/ostools/` (economy) — GridInfo in Robust.ini also advertises `web_profile_url = os.tasia.work.gd/ostools/profile/[AGENT_NAME]`

## Smoke test

```bash
python3 tests/smoke_test.py
```

## What you still need to fill in

Because you did not give me the money-server API yet, these parts are placeholders until you wire them:

- actual buy-currency credit logic
- actual land-buy backend approval logic
- balance / cap rules
- secure session validation if your backend exposes it
- any seller/admin IM messages you want beyond the simple examples
