#!/usr/bin/env python3
"""Sync Robust's regions.quicHost/quicPort with each region's actual QUIC listener.

Usage:
  ./fix_quic_ports.sh             # dry run
  ./fix_quic_ports.sh --apply     # dump DB, then update in a transaction

Database credentials come from the normal MySQL option file or MYSQL_PWD;
no password is stored in this script.
"""

from __future__ import annotations

import argparse
import configparser
import datetime as dt
import os
from pathlib import Path
import re
import subprocess
import sys


DB_HOST = os.environ.get("DB_HOST", "i.let-us.cyou")
DB_USER = os.environ.get("DB_USER", "root")
DB_NAME = os.environ.get("DB_NAME", "robust")
REGIONGEN_DIR = Path(os.environ.get("REGIONGEN_DIR", "/home/marty/opensim/regiongen/Regions"))
QUIC_HOST = os.environ.get("QUIC_HOST", "i.let-us.cyou")
BACKUP_DIR = Path(os.environ.get("BACKUP_DIR", "/home/marty/opensim/backups"))


def mysql(*args: str, input_text: str | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["mysql", "--host", DB_HOST, "--user", DB_USER, "--database", DB_NAME,
         "--batch", "--raw", "--skip-column-names", *args],
        input=input_text, text=True, capture_output=True, check=False,
    )


def parse_ini(path: Path) -> configparser.ConfigParser:
    parser = configparser.ConfigParser(interpolation=None, strict=False)
    parser.optionxform = str.lower
    with path.open(encoding="utf-8-sig") as stream:
        parser.read_file(stream)
    return parser


def region_configs() -> dict[str, Path]:
    """Map DB region names to the simulator INI hosting each region.

    Most regions have a same-named simulator directory. The two estate regions
    Tasia_Water_Park and Tasia_Park are hosted inside Little_Girls and
    New-Horizons respectively, so their Region/*.ini files identify the owner.
    """
    mapping: dict[str, Path] = {}
    for region_file in sorted(REGIONGEN_DIR.glob("*/Region/*.ini")):
        try:
            region_ini = parse_ini(region_file)
        except (OSError, configparser.Error) as exc:
            raise RuntimeError(f"Cannot parse region definition {region_file}: {exc}") from exc
        owner_ini = region_file.parent.parent / "Opensim.ini"
        for name in region_ini.sections():
            if name in mapping and mapping[name] != owner_ini:
                raise RuntimeError(f"Region {name!r} has multiple simulator INIs")
            mapping[name] = owner_ini

    # Compatibility for region folders that don't have a Region/*.ini file.
    for candidate in sorted(REGIONGEN_DIR.glob("*/Opensim.ini")):
        mapping.setdefault(candidate.parent.name, candidate)
    return mapping


def sql_quote(value: str) -> str:
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--apply", action="store_true", help="backup and update the database")
    args = ap.parse_args()

    if not re.fullmatch(r"[A-Za-z0-9.-]+", QUIC_HOST):
        ap.error("QUIC_HOST must be a hostname or IP address")

    result = mysql("--execute", "SELECT UUID, regionName, quicHost, quicPort FROM regions ORDER BY regionName")
    if result.returncode:
        print("ERROR: MySQL query failed:", result.stderr.strip(), file=sys.stderr)
        return 2

    configs = region_configs()
    changes: list[tuple[str, str, int, str, int]] = []
    skipped: list[str] = []
    unchanged = 0
    rows = [line.split("\t") for line in result.stdout.splitlines() if line]

    print("=== QUIC Region Port Sync ===")
    print(f"Mode: {'APPLY' if args.apply else 'DRY-RUN'}")

    for row in rows:
        if len(row) != 4:
            print(f"ERROR: unexpected DB row: {row!r}", file=sys.stderr)
            return 2
        region_uuid, name, old_host, old_port = row
        owner_ini = configs.get(name)
        if owner_ini is None or not owner_ini.is_file():
            skipped.append(name)
            print(f"  SKIP {name}: no region definition / simulator INI found")
            continue
        try:
            sim_ini = parse_ini(owner_ini)
            quic = sim_ini["ClientStack.Quic"]
            if quic.getboolean("Enabled", fallback=False) is not True:
                skipped.append(name)
                print(f"  SKIP {name}: QUIC is disabled in {owner_ini}")
                continue
            new_port = quic.getint("Port")
        except (OSError, KeyError, ValueError, configparser.Error) as exc:
            print(f"ERROR: cannot read QUIC listener for {name} from {owner_ini}: {exc}", file=sys.stderr)
            return 2
        if not 1 <= new_port <= 65535:
            print(f"ERROR: invalid listener port {new_port} for {name}", file=sys.stderr)
            return 2
        if not re.fullmatch(r"[0-9a-fA-F-]{36}", region_uuid):
            print(f"ERROR: invalid region UUID for {name}: {region_uuid!r}", file=sys.stderr)
            return 2
        if old_host == QUIC_HOST and old_port.isdigit() and int(old_port) == new_port:
            unchanged += 1
            print(f"  OK   {name}: {QUIC_HOST}:{new_port}")
            continue
        changes.append((region_uuid, name, new_port, old_host, int(old_port) if old_port.isdigit() else 0))
        print(f"  FIX  {name}: {old_host or '(empty)'}:{old_port or '0'} -> {QUIC_HOST}:{new_port} ({owner_ini.parent.name})")

    print(f"\nSummary: {len(changes)} changes, {unchanged} already correct, {len(skipped)} skipped, {len(rows)} DB rows")
    if skipped:
        print("Skipped regions need their host simulator INI restored or an explicit decision; no guessed ports were written.")
    if not args.apply:
        if changes:
            print("Dry run only. Re-run with --apply to back up and update the DB.")
        return 0
    if not changes:
        print("Nothing to update.")
        return 0

    BACKUP_DIR.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = BACKUP_DIR / f"robust-quic-ports-{stamp}.sql"
    dump = subprocess.run(
        ["mysqldump", "--host", DB_HOST, "--user", DB_USER, "--single-transaction", DB_NAME, "regions"],
        text=True, capture_output=True, check=False,
    )
    if dump.returncode or not dump.stdout.strip():
        print("ERROR: database backup failed; refusing to update:", dump.stderr.strip(), file=sys.stderr)
        return 2
    backup.write_text(dump.stdout, encoding="utf-8")
    print(f"Database backup: {backup}")

    statements = ["START TRANSACTION;"]
    for region_uuid, _name, port, _old_host, _old_port in changes:
        statements.append(
            "UPDATE regions SET quicHost=" + sql_quote(QUIC_HOST) +
            f", quicPort={port} WHERE UUID={sql_quote(region_uuid)};"
        )
    statements.append("COMMIT;")
    applied = mysql("--execute", "\n".join(statements))
    if applied.returncode:
        print("ERROR: database update failed; transaction was not committed:", applied.stderr.strip(), file=sys.stderr)
        return 2
    print(f"Updated {len(changes)} region endpoint records.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
