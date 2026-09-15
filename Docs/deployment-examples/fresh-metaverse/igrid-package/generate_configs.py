#!/usr/bin/env python3
"""
I-Grid Fresh Metaverse - configuration generator.

Creates the full deploy layout from regions.yaml:
  robust/Robust.ini                 grid services on  22000
  asset/AssetServer.ini             dedicated assets  22001 (optional)
  proxy/reserved.txt                proxy port        22002 (unused/reserved)
  sims/<RegionName>/OpenSim.ini     per-region sim config, own process & port (22003+)
  sims/<RegionName>/regions/*.ini   region definitions
  deploy.json                       manifest consumed by the manager

Usage: python3 generate_configs.py [--out <dir>]
"""
import argparse
import json
import os
import re
import secrets
import shutil
import uuid as uuidlib
import yaml

ROOT = os.path.dirname(os.path.abspath(__file__))
TPL = os.path.join(ROOT, "templates")


def load_yaml(path):
    try:
        import yaml
    except ImportError:
        print("PyYAML is required. Install with: pip install pyyaml")
        raise SystemExit(1)
    with open(path) as f:
        return yaml.safe_load(f)


def build_fresh_regions():
    """Fresh01..Fresh22, 1x1 regions on a 5-column grid at X2200/Y2200+,
    away from MainLand (X1998-2004, Y1998-2004)."""
    out = []
    col = 0
    row = 0
    for n in range(1, 23):
        if col >= 5:
            col = 0
            row += 1
        out.append({
            "id": "Fresh%02d" % n,
            "name": "Fresh%02d" % n,
            "size": 1,
            "x": 2200 + col,
            "y": 2200 + row,
            "physics": "ubODE",
            "meshing": "Meshmerizer",
            "clamp_prim_size": True,
        })
        col += 1
    return out


def assign_ports(cfg, regions):
    start = cfg["gate"]["region_ports_start"]
    quic_start = int(cfg["gate"].get("region_quic_start", 0))
    for i, r in enumerate(regions):
        r["port"] = start + i
        r["quic_port"] = 0 if quic_start == 0 else quic_start + i
        # Sim HTTP listener (CAPS) stays on the viewer-facing range that is
        # already forwarded (LLUDP+30). QUIC Port=0 lets Quick-G brain allocate
        # a separate per-region QUIC listener port from its configured pool.
        r["http_port"] = start + 30 + i
    return regions


def make_uuid(name):
    # Stable deterministic UUID (namespace v5) so regeneration keeps identity.
    return str(uuidlib.uuid5(uuidlib.NAMESPACE_URL, "igrid/" + name))


def phys_engine(config):
    return "ubODE" if config.get("physics") == "ubODE" else "BulletSim"


def meshing(config):
    if config.get("physics") == "ubODE" and config.get("meshing") == "Meshmerizer":
        return "ubODEMeshmerizer"
    return config.get("meshing", "Meshmerizer")


def clamp_line(config):
    clamp = config.get("clamp_prim_size")
    if clamp is False:
        return "ClampPrimSize = false"
    return "ClampPrimSize = true"


def openssl_env():
    """Return os.environ plus a usable OPENSSL_CONF on Windows.

    Some Windows OpenSSL builds (e.g. the Perl-distributed one) ship with a
    broken default config path and abort on every `req` invocation unless
    OPENSSL_CONF points at a real file.
    """
    env = dict(os.environ)
    if os.name == "nt" and "OPENSSL_CONF" not in env:
        for cand in (
            r"C:\Program Files\Git\usr\ssl\openssl.cnf",
            r"C:\Strawberry\c\ssl\openssl.cnf",
            r"C:\Program Files\OpenSSL-Win64\bin\openssl.cfg",
            r"C:\OpenSSL-Win64\bin\openssl.cfg",
        ):
            if os.path.isfile(cand):
                env["OPENSSL_CONF"] = cand
                break
    return env


def _gen_quic_cert(path, password, host="fresh.metaverse"):
    """Generate self-signed PEM and PKCS#12 certificates for QUIC transport."""
    import subprocess
    import tempfile

    env = openssl_env()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with tempfile.TemporaryDirectory() as td:
        key_p = os.path.join(td, "key.pem")
        cert_p = os.path.join(td, "cert.pem")
        subprocess.run([
            "openssl", "req", "-x509", "-newkey", "rsa:2048",
            "-keyout", key_p, "-out", cert_p,
            "-days", "365", "-nodes",
            "-subj", "/CN=%s" % host,
        ], check=True, capture_output=True, env=env)
        subprocess.run([
            "openssl", "pkcs12", "-export",
            "-in", cert_p, "-inkey", key_p,
            "-out", path, "-passout", "pass:%s" % password,
        ], check=True, capture_output=True, env=env)
        shutil.copy2(cert_p, os.path.join(os.path.dirname(path), "quic-cert.pem"))
        shutil.copy2(key_p, os.path.join(os.path.dirname(path), "quic-key.pem"))
    print("QUIC cert: %s (self-signed, 365 days)" % path)


def render(tpl_name, vars_):
    with open(os.path.join(TPL, tpl_name)) as f:
        tpl = f.read()
    out = tpl
    for k, v in vars_.items():
        out = out.replace("{{" + k + "}}", str(v))
    # Leave any stray/unknown placeholder as-is won't happen; make sure none left
    return out


def ini_path(path):
    return os.path.abspath(path).replace("\\", "/")


def default_region_database(name):
    safe = re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")
    return "sim_" + safe


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, "generated"))
    ap.add_argument("--yaml", default=os.path.join(ROOT, "regions.yaml"))
    args = ap.parse_args()

    cfg = load_yaml(args.yaml)
    gate = cfg["gate"]
    host = gate["external_host"]
    region_host = gate.get("region_external_host", host)
    robust_port = gate["robust_port"]
    private_port = gate.get("private_port", gate.get("asset_port", robust_port))
    asset_port = gate["asset_port"]
    quic_port = gate["quic_port"]
    hg_auth_port = gate["hg_auth_port"]

    named = cfg["regions"]
    fresh = build_fresh_regions()
    regions = assign_ports(cfg, named + fresh)

    out = args.out
    sims_dir = os.path.join(out, "sims")
    os.makedirs(out, exist_ok=True)
    os.makedirs(sims_dir, exist_ok=True)
    os.makedirs(os.path.join(out, "robust", "data"), exist_ok=True)
    os.makedirs(os.path.join(out, "robust", "robust-include"), exist_ok=True)
    os.makedirs(os.path.join(out, "robust", "SSL", "quic"), exist_ok=True)
    os.makedirs(os.path.join(out, "asset", "data"), exist_ok=True)
    os.makedirs(os.path.join(out, "hg"), exist_ok=True)
    # Centralized QUIC cert — single renewal writes once for Robust + all 30 sims.
    # Sims and Robust hot-reload via mtime/FileSystemWatcher; no restart.
    central_quic_dir = os.path.join(ROOT, "bin", "SSL", "quic")
    os.makedirs(central_quic_dir, exist_ok=True)

    db_provider = cfg.get("db", {}).get("provider", "SQLite")
    if db_provider == "SQLite":
        db_conn = 'URI=file:./data/robust.db;Version=3;'
        sim_db_conn = 'URI=file:./data/sim.db;Version=3;'
        asset_db_conn = 'URI=file:./data/assets.db;Version=3;'
    elif db_provider == "MySQL":
        m = cfg["db"]
        db_conn = "Data Source={host};Database={db};User ID={user};Password={pw};Old Guids=true;SslMode=None;".format(
            host=m.get("host", "localhost"), db=m.get("database", "opensim"),
            user=m.get("user", "opensim"), pw=m.get("password", "opensim"))
        sim_db_conn = db_conn
        asset_db_conn = "Data Source={host};Database={db_assets};User ID={user};Password={pw};Old Guids=true;SslMode=None;".format(
            host=m.get("host", "localhost"),
            db_assets=m.get("database_assets", "igrid_assets"),
            user=m.get("user", "opensim"), pw=m.get("password", "opensim"))
    else:
        m = cfg["db"]
        db_conn = "Server={host};Port=5432;Database={db};User Id={user};Password={pw};".format(
            host=m.get("host", "localhost"), db=m.get("database", "opensim"),
            user=m.get("user", "opensim"), pw=m.get("password", "opensim"))
        sim_db_conn = db_conn
        asset_db_conn = "Server={host};Port=5432;Database={db_assets};User Id={user};Password={pw};".format(
            host=m.get("host", "localhost"),
            db_assets=m.get("database_assets", "igrid_assets"),
            user=m.get("user", "opensim"), pw=m.get("password", "opensim"))

    console_pass = secrets.token_urlsafe(18)
    quic_real_cert = os.environ.get("QUIC_REAL_CERT", "").lower() in ("1", "true", "yes")
    # When keeping a real cert, reuse the existing p12 password so the central
    # quic-cert.p12 doesn't invalidate on every regeneration (renewal writes
    # once to bin/SSL/quic/ then all handshakes hot-reload via mtime).
    if quic_real_cert:
        try:
            _existing_robust = os.path.join(out, "robust", "Robust.ini")
            if os.path.isfile(_existing_robust):
                with open(_existing_robust, encoding="utf-8", errors="ignore") as _f:
                    _txt = _f.read()
                    import re as _re
                    _m = _re.search(r'CertificatePassword\s*=\s*"?([0-9a-f]{16,})"?', _txt)
                    if _m:
                        quic_cert_pass = _m.group(1)
                        print(f"QUIC cert password: reusing existing {quic_cert_pass[:6]}… from Robust.ini")
                    else:
                        quic_cert_pass = secrets.token_hex(16)
                    del _txt, _m
            else:
                quic_cert_pass = secrets.token_hex(16)
        except Exception:
            quic_cert_pass = secrets.token_hex(16)
    else:
        quic_cert_pass = secrets.token_hex(16)

    def database_connection(database):
        if db_provider == "SQLite":
            return sim_db_conn
        m = cfg["db"]
        if db_provider == "MySQL":
            return "Data Source={host};Database={db};User ID={user};Password={pw};Old Guids=true;SslMode=None;".format(
                host=m.get("host", "localhost"), db=database,
                user=m.get("user", "opensim"), pw=m.get("password", "opensim"))
        return "Server={host};Port=5432;Database={db};User Id={user};Password={pw};".format(
            host=m.get("host", "localhost"), db=database,
            user=m.get("user", "opensim"), pw=m.get("password", "opensim"))

    # ---- Robust ----
    rich_vars = {
        "ESTATE_NAME": cfg["estate"]["name"],
        "ESTATE_OWNER": cfg["estate"]["owner"],
        "HOST": host,
        "REGION_HOST": region_host,
        "ROBUST_PORT": robust_port,
        "PRIVATE_PORT": private_port,
        "ASSET_PORT": asset_port,
        "QUIC_PORT": quic_port,
        "QUICKG_CONTROL_PORT": gate.get("quickg_control_port", 19001),
        "QUICKG_BRAIN_BIND": gate.get("quickg_brain_bind", "127.0.0.1"),
        "QUICKG_BRAIN_PORT": gate.get("quickg_brain_port", 19002),
        "QUICKG_BRAIN_LEASE_SECONDS": gate.get("quickg_brain_lease_seconds", 90),
        "REGION_QUIC_POOL_START": gate.get("region_quic_pool_start", 22000),
        "REGION_QUIC_POOL_END": gate.get("region_quic_pool_end", 22500),
        "REGION_QUIC_POOL_EXCLUDE": gate.get("region_quic_pool_exclude", 22445),
        "QUIC_HEARTBEAT_SECONDS": gate.get("quic_heartbeat_seconds", 30),
        "HG_AUTH_PORT": hg_auth_port,
        "DB_PROVIDER": db_provider,
        "GRID_STORAGE_PROVIDER": "IGrid.SQLite.dll:RegionStore" if db_provider == "SQLite" else "OpenSim.Data.%s.dll" % db_provider,
        "PRESENCE_STORAGE_PROVIDER": "IGrid.SQLite.dll:PresenceStore" if db_provider == "SQLite" else "OpenSim.Data.%s.dll" % db_provider,
        "ACCOUNT_STORAGE_PROVIDER": "IGrid.SQLite.dll:AccountStore" if db_provider == "SQLite" else "OpenSim.Data.%s.dll" % db_provider,
        "OFFLINE_STORAGE_PROVIDER": "IGrid.SQLite.dll:OfflineStore" if db_provider == "SQLite" else "OpenSim.Data.%s.dll" % db_provider,
        "DB_CONN": db_conn,
        "SIM_DB_CONN": sim_db_conn,
        "ASSET_DB_CONN": asset_db_conn,
        "CONSOLE_PASS": console_pass,
        "QUIC_CERT_PASS": quic_cert_pass,
        "CENTRAL_QUIC_PEM": ini_path(os.path.join(central_quic_dir, "quic-cert.pem")),
        "CENTRAL_QUIC_KEY": ini_path(os.path.join(central_quic_dir, "quic-key.pem")),
        "CENTRAL_QUIC_P12": ini_path(os.path.join(central_quic_dir, "quic-cert.p12")),
    }
    robust = render("Robust.ini.tpl", rich_vars)
    with open(os.path.join(out, "robust", "Robust.ini"), "w") as f:
        f.write(robust)
    # ── cute robust 404 ──
    r404_dir = os.path.join(out, "robust", "404")
    os.makedirs(r404_dir, exist_ok=True)
    r404_html = f"""<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>404 — Robust — Fresh Metaverse 🌸</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@700;800&family=Pacifico&display=swap" rel=stylesheet>
<style>
body{{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 20% 20%,#FFD1DC 0%,transparent 40%),radial-gradient(circle at 80% 80%,#E8DFF5 0%,transparent 40%),linear-gradient(180deg,#FFF7FB,#F8F0FF);font-family:Nunito,system-ui;color:#4A3040}}
.card{{background:rgba(255,255,255,.92);backdrop-filter:blur(10px);border:1px solid #FFE0EA;border-radius:22px;box-shadow:0 10px 30px rgba(255,143,171,.18);padding:28px 24px;max-width:560px;text-align:center}}
h1{{font-family:Pacifico,cursive;margin:0 0 8px;font-size:32px}}
a{{display:inline-block;margin-top:14px;background:linear-gradient(135deg,#FF8FAB,#FFB5D8);color:#fff;padding:10px 18px;border-radius:999px;text-decoration:none;font-weight:800;box-shadow:0 8px 20px rgba(255,143,171,.3)}}
</style>
<div class=card><h1>🌸 Oops — Robust — 404</h1><p>Grid service not found. Try the <a href="https://os.tasia.work.gd/web/">Fresh Metaverse home</a>.</p><p><small>Robust {quic_port} • QUIC {quic_port}</small></p><p><a href="https://os.tasia.work.gd/amber/">✨ Amber's Site</a></p></div>"""
    with open(os.path.join(r404_dir, "robust.html"), "w", encoding="utf-8") as f:
        f.write(r404_html)
    with open(os.path.join(out, "robust", "http_404.html"), "w", encoding="utf-8") as f:
        f.write(r404_html)

    quick_g = os.path.join(ROOT, "bin", "Quick-G.exe")
    if os.path.isfile(quick_g):
        shutil.copy2(quick_g, os.path.join(out, "robust", "Quick-G.exe"))

    # ---- Asset server (dedicated, only for MySQL/PGSQL; SQLite yields a placeholder) ---- 
    with open(os.path.join(out, "asset", "AssetServer.ini"), "w") as f:
        if db_provider == "SQLite":
            f.write(
                ";; Dedicated asset server is NOT enabled with the SQLite backend.\n"
                ";; Assets are served by Robust on port %d. Switch 'db.provider' in\n"
                ";; regions.yaml to MySQL/PGSQL to enable a separate asset process.\n" % robust_port
            )
        else:
            f.write(
                ";; Dedicated asset server process is not enabled by this generator.\n"
                ";; Robust serves assets on the configured public/private service ports.\n"
                ";; Asset database connection retained for reference:\n"
                "[AssetService]\n"
                "StorageProvider = \"OpenSim.Data.%s.dll\"\n"
                "ConnectionString = \"%s\"\n" % (db_provider, asset_db_conn)
            )

    # ---- QUIC self-signed cert ----
    if quic_real_cert:
        print("QUIC cert: keeping existing real certificate (QUIC_REAL_CERT=1), not overwriting")
    else:
        try:
            _gen_quic_cert(os.path.join(out, "robust", "SSL", "quic", "quic-cert.p12"),
                            quic_cert_pass)
        except Exception as e:
            print("WARNING: QUIC cert generation failed (%s). Generate manually." % e)
    # Sync central bin/SSL/quic for hot-reload single-file renewal (Robust + 30 sims read central first)
    try:
        for _f in ("quic-cert.pem", "quic-key.pem", "quic-cert.p12"):
            _src = os.path.join(out, "robust", "SSL", "quic", _f)
            _dst = os.path.join(central_quic_dir, _f)
            if os.path.isfile(_src) and (not os.path.isfile(_dst) or os.path.getmtime(_src) > os.path.getmtime(_dst) + 1):
                shutil.copy2(_src, _dst)
                print(f"QUIC central sync: {_f} -> bin/SSL/quic/ ({os.path.getsize(_dst)} bytes)")
            elif os.path.isfile(_dst) and not os.path.isfile(_src):
                os.makedirs(os.path.dirname(_src), exist_ok=True)
                shutil.copy2(_dst, _src)
                print(f"QUIC central sync: bin/SSL/quic/{_f} -> robust/SSL/quic/")
    except Exception as e:
        print(f"WARNING: central QUIC sync failed: {e}")

    # ---- HG auth endpoint placeholder ----
    with open(os.path.join(out, "hg", "README.txt"), "w") as f:
        f.write(
            "Hypergrid auth endpoint (Python, SQLite).\n"
            "The manager spawns hgauth.py on port %d automatically.\n"
            "Admin panel: manager Web UI -> Hypergrid tab.\n" % hg_auth_port
        )

    # ---- sims ----
    manifest = {
        "gate": gate,
        "db": cfg.get("db", {}),
        "estate": cfg["estate"],
        "console_pass": console_pass,
        "sims": [],
    }
    for r in regions:
        name = r["name"]
        sim_dir = os.path.join(sims_dir, name)
        reg_dir = os.path.join(sim_dir, "regions")
        os.makedirs(reg_dir, exist_ok=True)
        os.makedirs(os.path.join(sim_dir, "data"), exist_ok=True)
        # Centralized QUIC cert: all 30 sims + Robust share one file in bin/SSL/quic.
        # Hot-reload via mtime (sim) + FileSystemWatcher+poll (Robust) — renewal writes once.
        cert_source = os.path.join(out, "robust", "SSL", "quic", "quic-cert.p12")
        # Back-compat: keep a copy in each sim dir if central missing on disk (fallback still works),
        # but new deployments point configs at central and don't require per-sim files.
        if quic_real_cert:
            for cert_file in ("quic-cert.pem", "quic-key.pem"):
                pem_source = os.path.join(out, "robust", "SSL", "quic", cert_file)
                central_pem = os.path.join(central_quic_dir, cert_file)
                if os.path.isfile(pem_source) and (not os.path.isfile(central_pem) or os.path.getmtime(pem_source) > os.path.getmtime(central_pem) + 1):
                    shutil.copy2(pem_source, central_pem)
                # legacy per-sim copy only if central not yet present (rare bootstrapping)
                if not os.path.isfile(central_pem) and os.path.isfile(pem_source):
                    cert_dir = os.path.join(sim_dir, "SSL", "quic")
                    os.makedirs(cert_dir, exist_ok=True)
                    shutil.copy2(pem_source, os.path.join(cert_dir, cert_file))
        elif os.path.isfile(cert_source):
            # self-signed — ensure central has it
            for cert_file in ("quic-cert.pem", "quic-key.pem", "quic-cert.p12"):
                pem_source = os.path.join(out, "robust", "SSL", "quic", cert_file)
                central_f = os.path.join(central_quic_dir, cert_file)
                if os.path.isfile(pem_source) and (not os.path.isfile(central_f) or os.path.getmtime(pem_source) > os.path.getmtime(central_f) + 1):
                    shutil.copy2(pem_source, central_f)

        sizem = r["size"] * 256
        uuidv = make_uuid(name)
        region_database = r.get("database")
        if not region_database and db_provider != "SQLite":
            region_database = default_region_database(name)
        region_db_conn = database_connection(region_database) if region_database else sim_db_conn
        v = dict(rich_vars)
        v["SIM_DB_CONN"] = region_db_conn
        central_cert = ini_path(os.path.join(central_quic_dir, "quic-cert.p12"))
        central_pem = ini_path(os.path.join(central_quic_dir, "quic-cert.pem"))
        central_key = ini_path(os.path.join(central_quic_dir, "quic-key.pem"))
        v.update({
            "NAME": name,
            "SIM_DIR": ini_path(sim_dir),
            "REGIONS_DIR": ini_path(reg_dir),
            "DATA_DIR": ini_path(os.path.join(sim_dir, "data")),
            "INVENTORY_DIR": ini_path(os.path.join(sim_dir, "inventory")),
            "ASSETCACHE_DIR": ini_path(os.path.join(sim_dir, "assetcache")),
            "QUIC_CERT_PATH": central_cert,
            "QUIC_CERT_PEM_PATH": central_pem,
            "QUIC_KEY_PEM_PATH": central_key,
            "REGION_UUID": uuidv,
            "PORT": r["port"],
            "QUIC_PORT": r["quic_port"],
            "SIM_HTTP_PORT": r["http_port"],
            "X": r["x"],
            "Y": r["y"],
            "SIZE_X": sizem,
            "SIZE_Y": sizem,
            "PHYSICS_ENGINE": phys_engine(r),
            "MESHING": meshing(r),
            "CLAMP_LINE": clamp_line(r),
        })
        with open(os.path.join(sim_dir, "OpenSim.ini"), "w") as f:
            f.write(render("SimOpenSim.ini.tpl", v))
        with open(os.path.join(sim_dir, "regions", name + ".ini"), "w") as f:
            f.write(render("Region.ini.tpl", v))
        # ── cute per-region 404 ──
        c404_dir = os.path.join(sim_dir, "404")
        os.makedirs(c404_dir, exist_ok=True)
        c404_html = f"""<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>404 — {name} — Fresh Metaverse 🌸</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@700;800&family=Pacifico&display=swap" rel=stylesheet>
<style>
body{{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 20% 20%,#FFD1DC 0%,transparent 40%),radial-gradient(circle at 80% 80%,#E8DFF5 0%,transparent 40%),linear-gradient(180deg,#FFF7FB,#F8F0FF);font-family:Nunito,system-ui;color:#4A3040}}
.card{{background:rgba(255,255,255,.92);backdrop-filter:blur(10px);border:1px solid #FFE0EA;border-radius:22px;box-shadow:0 10px 30px rgba(255,143,171,.18);padding:28px 24px;max-width:560px;text-align:center}}
h1{{font-family:Pacifico,cursive;margin:0 0 8px;font-size:32px}}
code{{background:#FFF0F5;border:1px solid #FFE0EA;border-radius:999px;padding:2px 8px}}
a{{display:inline-block;margin-top:14px;background:linear-gradient(135deg,#FF8FAB,#FFB5D8);color:#fff;padding:10px 18px;border-radius:999px;text-decoration:none;font-weight:800;box-shadow:0 8px 20px rgba(255,143,171,.3)}}
small{{color:#8A6A7A}}
</style>
<div class=card>
<h1>🌸 Oops — {name} — 404</h1>
<p>The page you requested flew away like a hippo with a jetpack.</p>
<p><small>Region <b>{name}</b> • HTTP {r["http_port"]} • QUIC {r["quic_port"]} • LLUDP {r["port"]}</small></p>
<p><a href="https://os.tasia.work.gd/web/">🏠 Back to Fresh Metaverse</a> <a href="https://os.tasia.work.gd/amber/" style="background:#fff;color:#4A3040;border:2px solid #FFE0EA;box-shadow:none">✨ Amber's Site</a></p>
</div>"""
        with open(os.path.join(c404_dir, f"{name}.html"), "w", encoding="utf-8") as f:
            f.write(c404_html)
        # also provide legacy ./http_404.html for BaseHttpServer fallback (CWD = sim_dir after launch change)
        with open(os.path.join(sim_dir, "http_404.html"), "w", encoding="utf-8") as f:
            f.write(c404_html)

        manifest["sims"].append({
            "name": name,
            "port": r["port"],
            "quic_port": r["quic_port"],
            "http_port": r["http_port"],
            "x": r["x"],
            "y": r["y"],
            "size": r["size"] * 256,
            "physics": phys_engine(r),
            "meshing": meshing(r),
            "clamp_prim_size": not (r.get("clamp_prim_size") is False),
            "region_uuid": uuidv,
            "folder": os.path.relpath(sim_dir, out),
        })

    with open(os.path.join(out, "deploy.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    os.chmod(os.path.join(out, "deploy.json"), 0o600)

    # Human- and script-readable QUIC endpoint list. Sim restarts do NOT
    # refresh these columns (registrations don't rewrite them), so the
    # generator pushes them right here, every run. Source of truth:
    # regions.yaml gate + this generator.
    with open(os.path.join(out, "quic-ports.ini"), "w") as f:
        f.write(";; Fresh Metaverse - native per-region QUIC endpoints (generated).\n")
        f.write(";; Pushed into the grid database by this generator (see push below).\n")
        f.write(";; [Robust] public viewer endpoint: %s:%d\n" % (region_host, quic_port))
        for entry in manifest["sims"]:
            f.write("[%s]\n" % entry["name"])
            f.write("quicHost = %s\n" % region_host)
            f.write("quicPort = %d\n" % entry["quic_port"])
            f.write("lludpPort = %d\n" % entry["port"])
            f.write("httpPort = %d\n" % entry["http_port"])

    # Push native QUIC endpoints into the grid database so login/teleport/
    # neighbour advertisements always carry real per-sim ports (I-Grid model).
    # Best effort: a missing driver or unreachable DB only warns; the ini
    # above stays as the manual fallback.
    try:
        import psycopg2
        _db = cfg["db"]
        if _db.get("provider", "PGSQL") == "PGSQL":
            _conn = psycopg2.connect(host=_db.get("host", "127.0.0.1"),
                                     database=_db.get("database", "robust"),
                                     user=_db.get("user", "opensim"),
                                     password=_db.get("password", "opensim"),
                                     connect_timeout=10)
            _cur = _conn.cursor()
            for entry in manifest["sims"]:
                _cur.execute(
                    'update regions set "quicHost"=%s, "quicPort"=%s where "regionName"=%s',
                    (region_host, entry["quic_port"], entry["name"]))
            _conn.commit()
            print("  Grid DB: pushed quicHost/quicPort for %d regions" % len(manifest["sims"]))
            _cur.close()
            _conn.close()
        else:
            print("  Grid DB: push skipped (non-PGSQL backend; use quic-ports.ini)")
    except Exception as e:
        print("  Grid DB: push skipped (%s); use quic-ports.ini" % e)

    print("\n=== Fresh Metaverse deploy generated: %d sims (%d named + %d Fresh) ===" % (len(regions), len(named), len(fresh)))
    print("  Robust:   %d (grid services + login)" % robust_port)
    print("  Assets:   %d (dedicated)" % asset_port)
    if int(gate.get("region_quic_start", 0)) == 0:
        print("  QUIC:     %d (Robust/Quick-G viewer) + %d..%d (Quick-G brain pool, excluding %s)" % (
            quic_port,
            gate.get("region_quic_pool_start", 22000),
            gate.get("region_quic_pool_end", 22500),
            gate.get("region_quic_pool_exclude", 22445)))
    else:
        print("  QUIC:     %d (Robust/Quick-G viewer) + %d..%d (fixed per-region)" % (quic_port, gate["region_quic_start"], gate["region_quic_start"] + len(regions) - 1))
    print("  Sim HTTP: %d..%d (CAPS, viewer-facing)" % (gate["region_ports_start"] + 30, gate["region_ports_start"] + 30 + len(regions) - 1))
    print("  Regions:  %d..%d (internal viewer ports)" % (manifest["sims"][0]["port"], manifest["sims"][-1]["port"]))
    print("  HG auth:  %d (Python endpoint)" % hg_auth_port)
    print("  DB:       %s" % db_provider)
    if quic_real_cert:
        print("  QUIC cert: kept existing real certificate (QUIC_REAL_CERT=1)")
    else:
        print("  QUIC cert: SSL/quic/quic-cert.p12 (self-signed, password in deploy.json)")
    print("  deploy.json is chmod 0600 - keep it secret.")


if __name__ == "__main__":
    main()
