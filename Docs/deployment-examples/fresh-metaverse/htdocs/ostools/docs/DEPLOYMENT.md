# Deployment guide for Fresh Metaverse — os.tasia.work.gd:22000

## Recommended layout

- Robust: `os.tasia.work.gd:22000` (PGSQL 127.0.0.1:5432 robust with environment-provided credentials) grid Fresh Metaverse
- Money server: `127.0.0.1:1026` or internal-only host
- Helper bridge: `127.0.0.1:8015`
- Public helper URI exposed by Apache/XAMPP (.htaccess) or nginx:
  - `https://os.tasia.work.gd/ostools/currency.php`
  - `https://os.tasia.work.gd/ostools/landtool.php`
  - `https://os.tasia.work.gd/ostools/healthz`

## Install

```bash
sudo mkdir -p /opt/opensim_helper_bridge
sudo cp -R . /opt/opensim_helper_bridge/
cd /opt/opensim_helper_bridge
sudo cp config.example.json config.json
sudo nano config.json
```

## Service

```bash
sudo cp examples/opensim-helper-bridge.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now opensim-helper-bridge.service
sudo systemctl status opensim-helper-bridge.service
```

## nginx

Use `examples/nginx.conf` as the base and merge it into your existing server block.

## OpenSim helper URI (Robust.ini)

Make sure your grid advertises:

```ini
[GridInfoService]
economy = "https://os.tasia.work.gd/ostools/"
web_profile_url = os.tasia.work.gd/ostools/profile/[AGENT_NAME]
login = http://os.tasia.work.gd:22000/
gridname = "Fresh Metaverse"
```

## Test sequence

1. `curl https://os.tasia.work.gd/ostools/healthz`  (or `http://127.0.0.1:8015/healthz` locally)
2. watch bridge logs with `journalctl -u opensim-helper-bridge -f`
3. click **Buy Currency** in Firestorm
4. click **Buy Land** / land sale flow in Firestorm
5. see whether the bridge forwards or fails on the upstream money service

## First debug trick

Before building a custom adapter, set `mode = xmlrpc_passthrough` and point it at `http://127.0.0.1:1026/`.

If requests start hitting your money server cleanly, you are basically done.
