#!/usr/bin/env python3
"""
SStats MySQL Dashboard — Password-Shielded HTTP Stats for OpenSim
=================================================================
Reads/writes the SAME `stats_session_data` table that the C# WebStatsModule
uses (via SQLite or MySQL). Connects to the SAME MySQL database OpenSim uses.

Architecture:
  ┌──────────────┐     writes      ┌─────────────┐     reads      ┌──────────────────┐
  │ OpenSim      │ ──────────────> │ MySQL DB    │ <───────────── │ Python SStats    │
  │ (C# SStats)  │                 │ opensim     │                │ Dashboard :8080  │
  └──────────────┘                 └─────────────┘                │ (password-       │
                                      ^  stats_session_data        │  shielded)       │
                                      |  table (shared)            └──────────────────┘

Features:
  - Connects to the same MySQL as OpenSim (same connection string)
  - Creates `stats_session_data` table if missing (same schema as C# module)
  - Password-shielded web dashboard (HTTP Basic Auth + session cookies)
  - Tabbed UI: Overview, Regions, Sessions, Monitor
  - Cross-DB region name resolution (opensim + robust)
  - dotnet-monitor Prometheus metrics integration
  - Real-time stats: users, sessions, FPS, ping, bandwidth, client versions
  - JSON API endpoints for programmatic access
  - Rate limiting, session management, access logging
  - Can also INSERT stats data (acts as a stats collector)
  - ThreadingHTTPServer for concurrent requests

Usage:
    MYSQL_HOST=localhost \
    MYSQL_DB=opensim \
    MYSQL_USER=opensim \
    MYSQL_PASS=yourpassword \
    PROXY_USERNAME=admin \
    PROXY_PASSWORD=changeme \
    python sstats_proxy.py

Requirements:
    pip install pymysql
"""

import os
import sys
import time
import json
import secrets
import logging
import re
import traceback
from http.server import HTTPServer, BaseHTTPRequestHandler
from socketserver import ThreadingMixIn
from urllib.parse import urlparse, parse_qs
from datetime import datetime, timezone
from collections import defaultdict
import base64
import html as html_module

try:
    import urllib.request
    import urllib.error
    HAS_URLLIB = True
except ImportError:
    HAS_URLLIB = False

# Optional: MySQL via PyMySQL (pure Python, no C deps)
try:
    import pymysql
    pymysql.install_as_MySQLdb()
    import MySQLdb
    HAS_MYSQL = True
except ImportError:
    HAS_MYSQL = False
    print("[WARNING] pymysql not installed. Install with: pip install pymysql")
    print("          Running in DEMO mode with mock data.")


# =============================================================================
# Configuration (from environment — mirrors OpenSim's Robust.ini format)
# =============================================================================
# MySQL connection (same as OpenSim)
MYSQL_HOST = os.environ.get("MYSQL_HOST", "localhost")
MYSQL_PORT = int(os.environ.get("MYSQL_PORT", "3306"))
MYSQL_DB = os.environ.get("MYSQL_DB", "opensim")
MYSQL_USER = os.environ.get("MYSQL_USER", "opensim")
MYSQL_PASS = os.environ.get("MYSQL_PASS", "")
MYSQL_CHARSET = os.environ.get("MYSQL_CHARSET", "utf8mb4")
MYSQL_TABLE = os.environ.get("MYSQL_TABLE", "stats_session_data")
MYSQL_CONNECT_TIMEOUT = int(os.environ.get("MYSQL_CONNECT_TIMEOUT", "10"))
MYSQL_READ_TIMEOUT = int(os.environ.get("MYSQL_READ_TIMEOUT", "15"))

# Dashboard auth
PROXY_PORT = int(os.environ.get("PROXY_PORT", "52326"))
PROXY_HOST = os.environ.get("PROXY_HOST", "0.0.0.0")
PROXY_USERNAME = os.environ.get("PROXY_USERNAME", "admin")
PROXY_PASSWORD = os.environ.get("PROXY_PASSWORD", "changeme")
SESSION_SECRET = os.environ.get("SESSION_SECRET", secrets.token_hex(32))
SESSION_TIMEOUT = int(os.environ.get("SESSION_TIMEOUT", "3600"))
MAX_LOGIN_ATTEMPTS = int(os.environ.get("MAX_LOGIN_ATTEMPTS", "5"))
LOGIN_LOCKOUT_SECONDS = int(os.environ.get("LOGIN_LOCKOUT_SECONDS", "300"))

# MetricsConnector
METRICS_API_TOKEN = os.environ.get("METRICS_API_TOKEN", "KOAKrJ3JJ2BduOxjTIEqjzyVpiKGwfO9SUiKmqF_oZyp5oxSBuRyClv7nrS2F1U5")
METRICS_HOST = os.environ.get("METRICS_HOST", "127.0.0.1")

# Region name -> (port, uuid) mapping
METRICS_PORT_MAP = {
    "Grid_Estate_Services": (2000, "f00c2c3e-55a2-4080-a2ee-d3957a010303"),
    "Grid_Welcome": (2001, "3b7d9bcb-f0c0-49de-84db-33d72446f06a"),
    "Little-Creek": (2002, "b0317b94-0214-43a4-ac41-1cc828ff2d65"),
    "Amber": (2003, "b0317b94-0214-43a4-ac41-1cc828ff2d66"),
    "The_Ceiba_Tree": (2004, "2cbd032a-361b-4f70-990b-de4981a80970"),
    "River-Retreat": (2005, "4b1b5744-9ab1-4507-af62-c0d835171e42"),
    "The_Furniture_Vault": (2006, "5175e2fd-7fa3-4910-83f3-b49afd8ae7f3"),
    "Leeloo": (2007, "e17e1839-5733-45fb-b046-de478b87606f"),
    "Little_Girls": (2008, "10449eab-71d4-4666-9d05-3ec6efc7601b"),
    "Blue-Heaven": (2009, "c00afa57-60bb-41f8-af02-fcc82e817539"),
    "Blue": (2010, "7efa77c7-f38d-47b2-9271-d3ed8126f718"),
    "Out_back": (2011, "00347229-ca93-430f-b69f-fc9eef50aa34"),
    "Clavius": (2012, "88d9d725-a3e8-430e-8aea-ea6f3c3a868c"),
    "New-Horizons": (2013, "2ebf634f-c1a0-4aef-a9d7-c8b5fd914838"),
    "Amber_Store2": (2014, "20000000-0000-4000-8000-000000000000"),
    "Residential_Area_01": (2015, "10000000-0000-4000-0000-000000000001"),
    "Residential_Area_02": (2016, "10000000-0000-4000-0000-000000000002"),
    "Crystal-Island": (2017, "c20bd5ef-e724-4462-8f49-152a06f7640a"),
    "Sunflower-Dream": (2018, "2cbd121a-261b-4f70-880b-de4981a80971"),
    "Amber_Store": (2019, "f00c2c3e-55a2-4080-a2ee-d3957a010302"),
    "little_beens": (2020, "01c343a7-fe38-4261-8137-9ecf596e16ac"),
    "I_LOVE_Sandbox": (2021, "10000000-1000-4000-1000-000000000001"),
    "Casino": (2022, "7efa77c7-f38d-47b2-9271-d3ed8126f720"),
    "Grid_Test": (2023, "3b7d9bcb-f0c0-49de-84db-33d72446f06c"),
    "Abody": (2026, "20000000-0000-4000-8000-000000000001"),
    "Grave": (2027, "cb325e3e-0524-4edb-bca0-5e81de406b02"),
    "events": (2028, "bde59b6b-a550-4c45-bba0-79ef90a467e4"),
    "elf": (2029, "f934be6a-5624-45c6-82a8-c3c12a345045"),
    "Hyperport": (2030, "b9540c60-20ac-4ece-8cb9-b538460e8cad"),
    "White_City_Sim": (2031, "6fac4f54-fb5b-41d5-9fc6-3a8eb0ddf471"),
    "Enchanted_Garden": (2032, "6aed4112-fc16-4c60-9541-d169c8be6758"),
    "testing_polygon": (2035, "7fb5b4eb-7f37-4aa7-947b-dea3becdcdae"),
}


# =============================================================================
# Logging
# =============================================================================
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[logging.StreamHandler(sys.stdout)]
)
log = logging.getLogger("sstats-mysql")


# =============================================================================
# Thread-safe HTTP Server
# =============================================================================
class ThreadingHTTPServer(ThreadingMixIn, HTTPServer):
    """Handle each request in a new thread — prevents one slow query from blocking all."""
    daemon_threads = True
    allow_reuse_address = True


# =============================================================================
# MySQL Database Layer
# =============================================================================
SQL_CREATE_TABLE = """CREATE TABLE IF NOT EXISTS `{table}` (
    session_id VARCHAR(36) NOT NULL PRIMARY KEY,
    agent_id VARCHAR(36) NOT NULL DEFAULT '',
    region_id VARCHAR(36) NOT NULL DEFAULT '',
    last_updated INT NOT NULL DEFAULT 0,
    remote_ip VARCHAR(16) NOT NULL DEFAULT '',
    name_f VARCHAR(50) NOT NULL DEFAULT '',
    name_l VARCHAR(50) NOT NULL DEFAULT '',
    avg_agents_in_view FLOAT NOT NULL DEFAULT 0,
    min_agents_in_view INT NOT NULL DEFAULT 0,
    max_agents_in_view INT NOT NULL DEFAULT 0,
    mode_agents_in_view INT NOT NULL DEFAULT 0,
    avg_fps FLOAT NOT NULL DEFAULT 0,
    min_fps FLOAT NOT NULL DEFAULT 0,
    max_fps FLOAT NOT NULL DEFAULT 0,
    mode_fps FLOAT NOT NULL DEFAULT 0,
    a_language VARCHAR(25) NOT NULL DEFAULT '',
    mem_use FLOAT NOT NULL DEFAULT 0,
    meters_traveled FLOAT NOT NULL DEFAULT 0,
    avg_ping FLOAT NOT NULL DEFAULT 0,
    min_ping FLOAT NOT NULL DEFAULT 0,
    max_ping FLOAT NOT NULL DEFAULT 0,
    mode_ping FLOAT NOT NULL DEFAULT 0,
    regions_visited INT NOT NULL DEFAULT 0,
    run_time FLOAT NOT NULL DEFAULT 0,
    avg_sim_fps FLOAT NOT NULL DEFAULT 0,
    min_sim_fps FLOAT NOT NULL DEFAULT 0,
    max_sim_fps FLOAT NOT NULL DEFAULT 0,
    mode_sim_fps FLOAT NOT NULL DEFAULT 0,
    start_time FLOAT NOT NULL DEFAULT 0,
    client_version VARCHAR(255) NOT NULL DEFAULT '',
    s_cpu VARCHAR(255) NOT NULL DEFAULT '',
    s_gpu VARCHAR(255) NOT NULL DEFAULT '',
    s_os VARCHAR(255) NOT NULL DEFAULT '',
    s_ram INT NOT NULL DEFAULT 0,
    d_object_kb FLOAT NOT NULL DEFAULT 0,
    d_texture_kb FLOAT NOT NULL DEFAULT 0,
    d_world_kb FLOAT NOT NULL DEFAULT 0,
    n_in_kb FLOAT NOT NULL DEFAULT 0,
    n_in_pk INT NOT NULL DEFAULT 0,
    n_out_kb FLOAT NOT NULL DEFAULT 0,
    n_out_pk INT NOT NULL DEFAULT 0,
    f_dropped INT NOT NULL DEFAULT 0,
    f_failed_resends INT NOT NULL DEFAULT 0,
    f_invalid INT NOT NULL DEFAULT 0,
    f_off_circuit INT NOT NULL DEFAULT 0,
    f_resent INT NOT NULL DEFAULT 0,
    f_send_packet INT NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"""


class MySQLConnection:
    """Single MySQL connection with auto-reconnect."""

    def __init__(self, host, port, db, user, passwd, label="primary"):
        self.label = label
        self.host = host
        self.port = port
        self.db = db
        self.user = user
        self.passwd = passwd
        self.conn = None
        self.connected = False
        self._connect()

    def _connect(self):
        if not HAS_MYSQL:
            log.warning(f"[{self.label}] MySQL not available")
            return
        try:
            self.conn = MySQLdb.connect(
                host=self.host,
                port=self.port,
                db=self.db,
                user=self.user,
                passwd=self.passwd,
                charset=MYSQL_CHARSET,
                autocommit=True,
                connect_timeout=MYSQL_CONNECT_TIMEOUT,
                read_timeout=MYSQL_READ_TIMEOUT,
            )
            self.connected = True
            log.info(f"[{self.label}] Connected to MySQL: {self.host}:{self.port}/{self.db}")
        except Exception as e:
            log.error(f"[{self.label}] MySQL connection failed: {e}")
            self.connected = False

    def _reconnect(self):
        try:
            if self.conn:
                self.conn.close()
        except Exception:
            pass
        self.conn = None
        self.connected = False
        self._connect()

    def query(self, sql, params=None):
        if not self.connected:
            self._reconnect()
        if not self.connected:
            return []
        try:
            cursor = self.conn.cursor(MySQLdb.cursors.DictCursor)
            cursor.execute(sql, params)
            rows = cursor.fetchall()
            cursor.close()
            return rows if rows else []
        except MySQLdb.OperationalError as e:
            log.warning(f"[{self.label}] Operational error (reconnecting): {e}")
            self._reconnect()
            if self.connected:
                try:
                    cursor = self.conn.cursor(MySQLdb.cursors.DictCursor)
                    cursor.execute(sql, params)
                    rows = cursor.fetchall()
                    cursor.close()
                    return rows if rows else []
                except Exception as e2:
                    log.error(f"[{self.label}] Reconnected query still failed: {e2}")
                    return []
            return []
        except Exception as e:
            log.error(f"[{self.label}] Query error: {e}")
            return []

    def execute(self, sql, params=None):
        if not self.connected:
            self._reconnect()
        if not self.connected:
            return 0
        try:
            cursor = self.conn.cursor()
            cursor.execute(sql, params)
            affected = cursor.rowcount
            cursor.close()
            return affected
        except Exception as e:
            log.error(f"[{self.label}] Execute error: {e}")
            return 0

    def close(self):
        if self.conn:
            try:
                self.conn.close()
            except Exception:
                pass


class SStatsDB:
    """Manages two MySQL connections: opensim (stats) + robust (region names)."""

    def __init__(self):
        # Primary: opensim DB (stats_session_data table)
        self.opensim = MySQLConnection(
            host=MYSQL_HOST, port=MYSQL_PORT, db=MYSQL_DB,
            user=MYSQL_USER, passwd=MYSQL_PASS, label="opensim"
        )
        # Secondary: robust DB (regions table with region names)
        self.robust = MySQLConnection(
            host=MYSQL_HOST, port=MYSQL_PORT, db="robust",
            user=MYSQL_USER, passwd=MYSQL_PASS, label="robust"
        )

        self.connected = self.opensim.connected
        if self.opensim.connected:
            self._ensure_table()

        # Cache region names
        self._region_cache = {}
        self._region_cache_time = 0
        self._load_region_cache()

    def _ensure_table(self):
        if not self.opensim.connected:
            return
        try:
            cursor = self.opensim.conn.cursor()
            safe_table = MYSQL_TABLE.replace('`', '``')
            cursor.execute(SQL_CREATE_TABLE.format(table=safe_table))
            cursor.close()
            log.info(f"Table `{MYSQL_TABLE}` verified/created")
        except Exception as e:
            log.error(f"Failed to create table: {e}")

    def _load_region_cache(self):
        """Load region name mapping from robust DB."""
        if not self.robust.connected:
            return
        try:
            rows = self.robust.query("SELECT uuid, regionName FROM regions")
            self._region_cache = {str(r["uuid"]): str(r["regionName"]) for r in rows}
            self._region_cache_time = time.time()
            log.info(f"Loaded {len(self._region_cache)} region names from robust DB")
        except Exception as e:
            log.warning(f"Could not load region cache: {e}")

    def get_region_name(self, region_id):
        """Get human-readable region name from cache."""
        if time.time() - self._region_cache_time > 300:
            self._load_region_cache()
        return self._region_cache.get(str(region_id), str(region_id)[:12] + "..." if len(str(region_id)) > 12 else str(region_id))

    # -------------------------------------------------------------------------
    # Stats Queries
    # -------------------------------------------------------------------------

    def get_summary(self):
        sql = f"""
            SELECT
                COUNT(DISTINCT agent_id) as total_users,
                COUNT(*) as total_sessions,
                AVG(avg_fps) as avg_client_fps,
                AVG(avg_sim_fps) as avg_sim_fps,
                AVG(avg_ping) as avg_ping,
                SUM(n_out_kb) as total_kb_out,
                SUM(n_in_kb) as total_kb_in,
                AVG(mem_use) as avg_mem_use
            FROM `{MYSQL_TABLE}`
            WHERE name_l != 'Heartbeat'
        """
        rows = self.opensim.query(sql)
        if rows:
            r = rows[0]
            return {
                "total_users": int(r.get("total_users") or 0),
                "total_sessions": int(r.get("total_sessions") or 0),
                "avg_client_fps": round(float(r.get("avg_client_fps") or 0), 2),
                "avg_sim_fps": round(float(r.get("avg_sim_fps") or 0), 2),
                "avg_ping": round(float(r.get("avg_ping") or 0), 2),
                "total_kb_out": round(float(r.get("total_kb_out") or 0), 2),
                "total_kb_in": round(float(r.get("total_kb_in") or 0), 2),
                "avg_mem_use": round(float(r.get("avg_mem_use") or 0), 2),
            }
        return {}

    def get_client_versions(self):
        sql = f"""
            SELECT client_version, COUNT(*) as cnt, AVG(avg_sim_fps) as simfps
            FROM `{MYSQL_TABLE}`
            GROUP BY client_version
            ORDER BY COUNT(*) DESC
            LIMIT 15
        """
        rows = self.opensim.query(sql)
        return [
            {
                "version": str(r.get("client_version", "")),
                "count": int(r.get("cnt", 0)),
                "avg_fps": round(float(r.get("simfps") or 0), 2)
            }
            for r in rows
        ]

    def get_recent_sessions(self, limit=50):
        sql = f"""
            SELECT name_f, name_l, agent_id, session_id, client_version,
                   last_updated, start_time, region_id, avg_fps, avg_ping,
                   mem_use, remote_ip, n_out_kb, n_in_kb, run_time, regions_visited
            FROM `{MYSQL_TABLE}`
            WHERE name_l != 'Heartbeat'
            ORDER BY last_updated DESC
            LIMIT %s
        """
        rows = self.opensim.query(sql, (limit,))
        result = []
        for r in rows:
            try:
                last_ts = int(r.get("last_updated", 0) or 0)
                last_str = datetime.fromtimestamp(last_ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC") if last_ts else "?"
            except Exception:
                last_str = "?"
            try:
                start_ts = int(r.get("start_time", 0) or 0)
                start_str = datetime.fromtimestamp(start_ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC") if start_ts else "?"
            except Exception:
                start_str = "?"
            rid = str(r.get("region_id", ""))
            result.append({
                "name": f"{r.get('name_f', '')} {r.get('name_l', '')}".strip(),
                "agent_id": str(r.get("agent_id", "")),
                "session_id": str(r.get("session_id", "")),
                "client_version": str(r.get("client_version", "")),
                "last_updated": last_str,
                "region_id": rid,
                "region_name": self.get_region_name(rid),
                "avg_fps": round(float(r.get("avg_fps") or 0), 1),
                "avg_ping": round(float(r.get("avg_ping") or 0), 1),
                "mem_mb": round(float(r.get("mem_use") or 0), 1),
                "remote_ip": str(r.get("remote_ip", "")),
                "kb_out": round(float(r.get("n_out_kb") or 0), 1),
                "kb_in": round(float(r.get("n_in_kb") or 0), 1),
                "run_time": round(float(r.get("run_time") or 0), 0),
                "regions_visited": int(r.get("regions_visited") or 0),
            })
        return result

    def get_active_users(self):
        """Online users from Presence + stats merge. Deduped by agent_id."""
        # 1. Get online users from Presence (dedup — keep latest session per user)
        sql = """
            SELECT p.UserID, p.RegionID, p.LastSeen,
                   r.regionName, u.FirstName, u.LastName
            FROM Presence p
            INNER JOIN UserAccounts u ON p.UserID = u.PrincipalID
            LEFT JOIN regions r ON p.RegionID = r.uuid
            WHERE p.RegionID IS NOT NULL
            ORDER BY p.LastSeen DESC
        """
        rows = self.robust.query(sql)
        seen = set()
        online_map = {}
        for r in rows:
            uid = str(r.get("UserID", ""))
            if uid in seen:
                continue
            seen.add(uid)
            fname = str(r.get("FirstName", "") or "")
            lname = str(r.get("LastName", "") or "")
            if not fname and not lname:
                fname = uid[:8]
            online_map[uid] = {
                "name": f"{fname} {lname}".strip(),
                "agent_id": uid,
                "fps": 0,
                "ping": 0,
                "mem_mb": 0,
                "client": "",
                "region_id": str(r.get("RegionID", "")),
                "region_name": str(r.get("regionName", "") or "Unknown"),
                "last_seen": str(r.get("LastSeen", "?")),
                "online": True,
            }

        # 2. Enrich online users with stats data (FPS, ping, mem, client)
        if online_map:
            placeholders = ','.join(['%s'] * len(online_map))
            sql_stats = f"""
                SELECT agent_id, avg_fps, avg_ping, mem_use, client_version
                FROM `{MYSQL_TABLE}`
                WHERE agent_id IN ({placeholders})
                ORDER BY last_updated DESC
            """
            try:
                stats_rows = self.opensim.query(sql_stats, tuple(online_map.keys()))
                filled = set()
                for sr in stats_rows:
                    uid = str(sr.get("agent_id", ""))
                    if uid in filled:
                        continue
                    filled.add(uid)
                    if uid in online_map:
                        online_map[uid]["fps"] = round(float(sr.get("avg_fps") or 0), 1)
                        online_map[uid]["ping"] = round(float(sr.get("avg_ping") or 0), 1)
                        online_map[uid]["mem_mb"] = round(float(sr.get("mem_use") or 0), 1)
                        online_map[uid]["client"] = str(sr.get("client_version", ""))
            except Exception as e:
                log.error(f"Stats merge for online users: {e}")

        result = list(online_map.values())

        # 3. Add recent offline users from stats_session_data (not in Presence)
        offline_ids = [r["agent_id"] for r in result]
        if offline_ids:
            placeholders = ','.join(['%s'] * len(offline_ids))
            sql2 = f"""
                SELECT name_f, name_l, agent_id, avg_fps, avg_ping,
                       mem_use, client_version, region_id, last_updated
                FROM `{MYSQL_TABLE}`
                WHERE name_l != 'Heartbeat' AND agent_id NOT IN ({placeholders})
                AND last_updated > UNIX_TIMESTAMP(DATE_SUB(NOW(), INTERVAL 1 DAY))
                ORDER BY last_updated DESC
                LIMIT 50
            """
            rows2 = self.opensim.query(sql2, tuple(offline_ids))
        else:
            sql2 = f"""
                SELECT name_f, name_l, agent_id, avg_fps, avg_ping,
                       mem_use, client_version, region_id, last_updated
                FROM `{MYSQL_TABLE}`
                WHERE name_l != 'Heartbeat'
                AND last_updated > UNIX_TIMESTAMP(DATE_SUB(NOW(), INTERVAL 1 DAY))
                ORDER BY last_updated DESC
                LIMIT 50
            """
            rows2 = self.opensim.query(sql2)

        seen_offline = set()
        for r in rows2:
            try:
                last_ts = int(r.get("last_updated", 0) or 0)
                last_str = datetime.fromtimestamp(last_ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M") if last_ts else "?"
            except Exception:
                last_str = "?"
            rid = str(r.get("region_id", ""))
            aid = str(r.get("agent_id", ""))
            if aid in seen_offline:
                continue
            seen_offline.add(aid)
            result.append({
                "name": f"{r.get('name_f', '')} {r.get('name_l', '')}".strip(),
                "agent_id": aid,
                "fps": round(float(r.get("avg_fps") or 0), 1),
                "ping": round(float(r.get("avg_ping") or 0), 1),
                "mem_mb": round(float(r.get("mem_use") or 0), 1),
                "client": str(r.get("client_version", "")),
                "region_id": rid,
                "region_name": self.get_region_name(rid),
                "last_seen": last_str,
                "online": False,
            })
        return result

    def get_region_stats(self):
        sql = f"""
            SELECT region_id,
                   COUNT(DISTINCT agent_id) as users,
                   COUNT(*) as sessions,
                   AVG(avg_fps) as avg_fps,
                   AVG(avg_ping) as avg_ping,
                   AVG(avg_sim_fps) as avg_sim_fps,
                   AVG(mem_use) as avg_mem
            FROM `{MYSQL_TABLE}`
            GROUP BY region_id
            ORDER BY users DESC
        """
        rows = self.opensim.query(sql)
        result = []
        for r in rows:
            rid = str(r.get("region_id", ""))
            result.append({
                "region_id": rid,
                "region_name": self.get_region_name(rid),
                "users": int(r.get("users", 0)),
                "sessions": int(r.get("sessions", 0)),
                "avg_fps": round(float(r.get("avg_fps") or 0), 2),
                "avg_ping": round(float(r.get("avg_ping") or 0), 2),
                "avg_sim_fps": round(float(r.get("avg_sim_fps") or 0), 2),
                "avg_mem": round(float(r.get("avg_mem") or 0), 1),
            })
        return result

    def get_region_health(self):
        """Get heartbeat data for all regions — shows FPS even when empty."""
        sql = f"""
            SELECT region_id, avg_sim_fps, last_updated, client_version
            FROM `{MYSQL_TABLE}`
            WHERE name_l = 'Heartbeat'
            ORDER BY region_id
        """
        rows = self.opensim.query(sql)
        result = []
        now = int(time.time())
        for r in rows:
            rid = str(r.get("region_id", ""))
            last_ts = int(r.get("last_updated", 0) or 0)
            age = now - last_ts
            result.append({
                "region_id": rid,
                "region_name": self.get_region_name(rid),
                "sim_fps": round(float(r.get("avg_sim_fps") or 0), 1),
                "age_seconds": age,
                "status": "alive" if age < 60 else ("stale" if age < 300 else "dead"),
                "last_update": datetime.fromtimestamp(last_ts, tz=timezone.utc).strftime("%H:%M:%S") if last_ts else "?",
            })
        return result

    def get_table_info(self):
        sql = f"SELECT COUNT(*) as cnt FROM `{MYSQL_TABLE}`"
        rows = self.opensim.query(sql)
        return {"row_count": int(rows[0]["cnt"]) if rows else 0}

    def insert_session(self, data):
        sql = f"""
            INSERT INTO `{MYSQL_TABLE}`
            (session_id, agent_id, region_id, last_updated, remote_ip,
             name_f, name_l, avg_fps, avg_ping, mem_use, client_version,
             s_cpu, s_gpu, s_os, s_ram)
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
                last_updated=VALUES(last_updated),
                avg_fps=VALUES(avg_fps),
                avg_ping=VALUES(avg_ping),
                mem_use=VALUES(mem_use)
        """
        return self.opensim.execute(sql, (
            data.get("session_id", str(secrets.token_hex(16))),
            data.get("agent_id", ""),
            data.get("region_id", ""),
            data.get("last_updated", int(time.time())),
            data.get("remote_ip", ""),
            data.get("name_f", ""),
            data.get("name_l", ""),
            data.get("avg_fps", 0),
            data.get("avg_ping", 0),
            data.get("mem_use", 0),
            data.get("client_version", ""),
            data.get("s_cpu", ""),
            data.get("s_gpu", ""),
            data.get("s_os", ""),
            data.get("s_ram", 0),
        ))

    def close(self):
        self.opensim.close()
        self.robust.close()


# =============================================================================
# Session & Auth Management
# =============================================================================
sessions = {}
login_attempts = {}


def generate_session_token(username):
    token = secrets.token_urlsafe(32)
    now = time.time()
    sessions[token] = {"user": username, "created": now, "last_access": now}
    return token


def validate_session(token):
    if not token or token not in sessions:
        return None
    sess = sessions[token]
    if time.time() - sess["last_access"] > SESSION_TIMEOUT:
        del sessions[token]
        return None
    sess["last_access"] = time.time()
    return sess["user"]


def check_rate_limit(ip):
    if ip in login_attempts:
        info = login_attempts[ip]
        if info.get("lockout_until", 0) > time.time():
            return False
    return True


def record_failed_login(ip):
    now = time.time()
    if ip not in login_attempts:
        login_attempts[ip] = {"count": 0, "lockout_until": 0}
    info = login_attempts[ip]
    info["count"] += 1
    if info["count"] >= MAX_LOGIN_ATTEMPTS:
        info["lockout_until"] = now + LOGIN_LOCKOUT_SECONDS
        info["count"] = 0
        log.warning(f"IP {ip} locked out for {LOGIN_LOCKOUT_SECONDS}s")


def clear_failed_login(ip):
    login_attempts.pop(ip, None)


# =============================================================================
# dotnet-monitor Prometheus metrics fetcher
# =============================================================================
def fetch_monitor_metrics():
    """Fetch metrics from MetricsConnector API on each sim."""
    if not HAS_URLLIB:
        return {"error": "urllib not available", "regions": []}

    results = []
    errors = []

    for region_name, (port, uuid) in METRICS_PORT_MAP.items():
        url = f"http://{METRICS_HOST}:{port}/tasia-ngc/metrics/{uuid}"
        try:
            req = urllib.request.Request(url, headers={
                "Authorization": f"Bearer {METRICS_API_TOKEN}",
                "Accept": "application/json"
            })
            with urllib.request.urlopen(req, timeout=3) as resp:
                data = json.loads(resp.read().decode("utf-8"))
                results.append(data)
        except Exception as e:
            errors.append(f"{region_name}: {e}")

    return {
        "regions": results,
        "errors": errors,
        "connected": len(results) > 0,
    }


def _parse_prometheus(text):
    """Parse Prometheus text format into structured metrics."""
    metrics = []
    lines = text.strip().split("\n")
    current_metric = None
    help_text = ""
    type_text = ""

    for line in lines:
        line = line.strip()
        if not line:
            continue
        if line.startswith("# HELP "):
            help_text = line[7:]
        elif line.startswith("# TYPE "):
            type_text = line[7:]
            parts = type_text.split(" ", 1)
            current_metric = {
                "name": parts[0] if parts else "",
                "type": parts[1] if len(parts) > 1 else "",
                "help": help_text.split(" ", 1)[1] if " " in help_text else "",
                "samples": []
            }
            metrics.append(current_metric)
            help_text = ""
        elif not line.startswith("#"):
            # Parse metric line: name{labels} value [timestamp]
            match = re.match(r'^([a-zA-Z_:][a-zA-Z0-9_:]*)\{([^}]*)\}\s+([\d.eE+-]+(?:NaN|Inf|-Inf)?)', line)
            if match and current_metric:
                labels = {}
                for kv in match.group(2).split(","):
                    if "=" in kv:
                        k, v = kv.split("=", 1)
                        labels[k.strip()] = v.strip().strip('"')
                current_metric["samples"].append({
                    "labels": labels,
                    "value": match.group(3)
                })
            else:
                # Simple metric without labels
                match2 = re.match(r'^([a-zA-Z_:][a-zA-Z0-9_:]*)\s+([\d.eE+-]+(?:NaN|Inf|-Inf)?)', line)
                if match2 and current_metric:
                    current_metric["samples"].append({
                        "labels": {},
                        "value": match2.group(2)
                    })

    # Filter out empty metrics and summarize
    meaningful = [m for m in metrics if m["samples"]]
    return {"error": "", "metrics": meaningful, "raw": text, "connected": True, "count": len(meaningful)}


# =============================================================================
# Dashboard HTML — Tabbed UI
# =============================================================================
LOGIN_PAGE = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>SStats - Login Required</title>
<style>
  *{margin:0;padding:0;box-sizing:border-box}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:linear-gradient(135deg,#0f0c29,#302b63,#24243e);min-height:100vh;display:flex;align-items:center;justify-content:center;color:#fff}
  .box{background:rgba(255,255,255,0.05);backdrop-filter:blur(10px);border:1px solid rgba(255,255,255,0.1);border-radius:16px;padding:40px;width:380px;box-shadow:0 8px 32px rgba(0,0,0,0.3)}
  .box h1{text-align:center;font-size:24px;margin-bottom:8px;background:linear-gradient(90deg,#00d2ff,#7b61ff);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
  .box .sub{text-align:center;color:rgba(255,255,255,0.6);font-size:14px;margin-bottom:30px}
  .field{margin-bottom:20px}
  .field label{display:block;margin-bottom:6px;font-size:12px;color:rgba(255,255,255,0.7);text-transform:uppercase;letter-spacing:1px}
  .field input{width:100%;padding:12px 16px;border:1px solid rgba(255,255,255,0.15);border-radius:8px;background:rgba(255,255,255,0.08);color:#fff;font-size:15px;outline:none}
  .field input:focus{border-color:#7b61ff}
  .btn{width:100%;padding:14px;border:none;border-radius:8px;background:linear-gradient(90deg,#00d2ff,#7b61ff);color:#fff;font-size:16px;font-weight:600;cursor:pointer}
  .btn:hover{opacity:0.9}
  .err{background:rgba(255,50,50,0.15);border:1px solid rgba(255,50,50,0.3);border-radius:8px;padding:10px;margin-bottom:20px;font-size:13px;color:#ff6b6b;text-align:center}
</style>
</head>
<body>
<div class="box">
  <h1>SStats</h1>
  <div class="sub">OpenSim Statistics Dashboard</div>
  __ERROR_HTML__
  <form method="POST" action="/login">
    <div class="field"><label>Username</label><input type="text" name="username" required autofocus></div>
    <div class="field"><label>Password</label><input type="password" name="password" required></div>
    <button type="submit" class="btn">Sign In</button>
  </form>
</div>
</body>
</html>"""


def build_dashboard_html(db):
    """Build the complete tabbed dashboard HTML with all data pre-rendered."""
    db_ok = db and db.connected

    # Fetch data with individual error protection
    summary = {}
    active = []
    versions = []
    recent = []
    regions = []
    info = {"row_count": 0}

    try:
        summary = db.get_summary() if db_ok else {}
    except Exception as e:
        log.error(f"get_summary failed: {e}")

    try:
        active = db.get_active_users() if db_ok else []
    except Exception as e:
        log.error(f"get_active_users failed: {e}")

    try:
        versions = db.get_client_versions() if db_ok else []
    except Exception as e:
        log.error(f"get_client_versions failed: {e}")

    try:
        recent = db.get_recent_sessions(50) if db_ok else []
    except Exception as e:
        log.error(f"get_recent_sessions failed: {e}")

    try:
        regions = db.get_region_stats() if db_ok else []
    except Exception as e:
        log.error(f"get_region_stats failed: {e}")

    try:
        health = db.get_region_health() if db_ok else []
    except Exception as e:
        log.error(f"get_region_health failed: {e}")

    try:
        info = db.get_table_info() if db_ok else {"row_count": 0}
    except Exception as e:
        log.error(f"get_table_info failed: {e}")

    # Prepare JSON data for client-side rendering
    active_json = json.dumps(active, default=str)
    versions_json = json.dumps(versions, default=str)
    sessions_json = json.dumps(recent, default=str)
    regions_json = json.dumps(regions, default=str)
    health_json = json.dumps(health, default=str)
    summary_json = json.dumps(summary, default=str)

    # Determine DB status
    opensim_status = "connected" if (db and db.opensim.connected) else "disconnected"
    robust_status = "connected" if (db and db.robust.connected) else "disconnected"

    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")

    return DASHBOARD_TEMPLATE.replace("__SUMMARY_JSON__", summary_json) \
        .replace("__ACTIVE_JSON__", active_json) \
        .replace("__VERSIONS_JSON__", versions_json) \
        .replace("__SESSIONS_JSON__", sessions_json) \
        .replace("__REGIONS_JSON__", regions_json) \
        .replace("__HEALTH_JSON__", health_json) \
        .replace("__ROW_COUNT__", str(info.get("row_count", 0))) \
        .replace("__TIMESTAMP__", ts) \
        .replace("__DB_HOST__", html_module.escape(MYSQL_HOST)) \
        .replace("__DB_NAME__", html_module.escape(MYSQL_DB)) \
        .replace("__OPENSIM_STATUS__", opensim_status) \
        .replace("__ROBUST_STATUS__", robust_status) \
        .replace("__TABLE_NAME__", html_module.escape(MYSQL_TABLE))


DASHBOARD_TEMPLATE = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>SStats Dashboard - OpenSim Statistics</title>
<style>
  *{margin:0;padding:0;box-sizing:border-box}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:#0a0e1a;color:#c8ccd4;min-height:100vh;overflow-x:hidden}

  /* Header */
  .header{background:linear-gradient(135deg,#131829 0%,#0d1225 100%);padding:16px 24px;border-bottom:1px solid #1e2740;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px}
  .header h1{font-size:20px;background:linear-gradient(90deg,#00d2ff,#7b61ff);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
  .header .meta{font-size:11px;color:#555;margin-top:2px}
  .header .links{display:flex;gap:14px;align-items:center}
  .header .links a{color:#7b61ff;text-decoration:none;font-size:13px;transition:color .2s}
  .header .links a:hover{color:#a89bff}

  /* Tab navigation */
  .tabs{display:flex;gap:0;background:#0d1225;border-bottom:2px solid #1a1f35;padding:0 24px;overflow-x:auto}
  .tab-btn{padding:12px 24px;background:none;border:none;color:#556;font-size:14px;font-weight:500;cursor:pointer;border-bottom:2px solid transparent;margin-bottom:-2px;transition:all .2s;white-space:nowrap}
  .tab-btn:hover{color:#99a}
  .tab-btn.active{color:#00d2ff;border-bottom-color:#00d2ff}
  .tab-btn .badge{background:#7b61ff;color:#fff;font-size:10px;padding:1px 6px;border-radius:8px;margin-left:6px;font-weight:600}

  /* Tab content */
  .tab-content{display:none;padding:20px 24px;max-width:1400px;margin:0 auto}
  .tab-content.active{display:block}

  /* Summary cards */
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:14px;margin:16px 0}
  .card{background:rgba(255,255,255,0.025);border:1px solid #1a2035;border-radius:12px;padding:18px;text-align:center;transition:border-color .2s}
  .card:hover{border-color:#2a3555}
  .card .value{font-size:28px;font-weight:700;color:#00d2ff;margin:6px 0}
  .card .label{font-size:11px;color:#556;text-transform:uppercase;letter-spacing:1px}
  .card.accent .value{color:#7b61ff}
  .card.green .value{color:#4caf50}
  .card.orange .value{color:#ff9800}

  /* Section boxes */
  .section{background:rgba(255,255,255,0.02);border:1px solid #1a2035;border-radius:12px;padding:18px;margin:16px 0}
  .section h2{font-size:14px;color:#7b61ff;margin-bottom:12px;font-weight:600;text-transform:uppercase;letter-spacing:0.5px}
  .section h3{font-size:13px;color:#889;margin:12px 0 8px;font-weight:500}

  /* Tables */
  .table-wrap{overflow-x:auto}
  table{width:100%;border-collapse:collapse;font-size:12px}
  th{text-align:left;padding:8px 10px;color:#556;border-bottom:1px solid #1a2035;font-weight:600;text-transform:uppercase;font-size:10px;letter-spacing:1px;white-space:nowrap}
  td{padding:7px 10px;border-bottom:1px solid #111828;white-space:nowrap}
  tr:hover td{background:rgba(123,97,255,0.04)}
  .mono{font-family:'Fira Code',Consolas,monospace;font-size:11px;color:#667}

  /* Status indicators */
  .dot{display:inline-block;width:7px;height:7px;border-radius:50%;margin-right:5px}
  .dot.on{background:#4caf50}
  .dot.off{background:#e94560}
  .dot.warn{background:#ff9800}

  /* Badges */
  .badge-sm{display:inline-block;padding:2px 8px;border-radius:6px;font-size:10px;font-weight:600}
  .badge-sm.ok{background:rgba(76,175,80,0.15);color:#4caf50}
  .badge-sm.warn{background:rgba(255,152,0,0.15);color:#ff9800}
  .badge-sm.err{background:rgba(233,69,96,0.15);color:#e94560}

  /* Pie chart canvas */
  .chart-container{display:flex;align-items:center;gap:24px;flex-wrap:wrap}
  canvas.pie{max-width:220px;max-height:220px}
  .legend{font-size:12px;line-height:1.8}
  .legend span{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:middle}

  /* Empty state */
  .empty{color:#444;font-style:italic;padding:20px;text-align:center}

  /* Monitor */
  .metric-card{background:rgba(255,255,255,0.02);border:1px solid #1a2035;border-radius:8px;padding:12px 16px;margin:6px 0}
  .metric-card .name{font-size:12px;color:#7b61ff;font-weight:600}
  .metric-card .desc{font-size:11px;color:#555;margin-bottom:6px}
  .metric-card .val{font-size:16px;font-weight:600;color:#00d2ff}
  .metric-card .lbl{font-size:10px;color:#667;margin-left:4px}
  .monitor-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:8px}

  /* Footer */
  .footer{text-align:center;font-size:11px;color:#334;padding:16px;border-top:1px solid #1a2035;margin-top:20px}

  /* Responsive */
  @media(max-width:768px){
    .grid{grid-template-columns:repeat(2,1fr)}
    .tab-btn{padding:10px 14px;font-size:13px}
    .header{padding:12px 16px}
    .tab-content{padding:14px 12px}
    .chart-container{flex-direction:column}
  }
  @media(max-width:480px){
    .grid{grid-template-columns:1fr}
  }
</style>
</head>
<body>

<div class="header">
  <div>
    <h1>SStats Dashboard</h1>
    <div class="meta">__DB_HOST__ / __DB_NAME__ &bull; Auto-refresh 30s</div>
  </div>
  <div class="links">
    <span style="font-size:11px;color:#555"><span class="dot __OPENSIM_STATUS__"></span>opensim DB</span>
    <span style="font-size:11px;color:#555"><span class="dot __ROBUST_STATUS__"></span>robust DB</span>
    <a href="/api/stats">JSON API</a>
    <a href="/api/regions">Regions API</a>
    <a href="/logout">Logout</a>
  </div>
</div>

<div class="tabs">
  <button class="tab-btn active" onclick="showTab('overview')">Overview</button>
  <button class="tab-btn" onclick="showTab('health')">Region Health</button>
  <button class="tab-btn" onclick="showTab('regions')">User Regions</button>
  <button class="tab-btn" onclick="showTab('sessions')">Sessions</button>
  <button class="tab-btn" onclick="showTab('monitor')">Monitor</button>
</div>

<!-- ==================== OVERVIEW TAB ==================== -->
<div id="tab-overview" class="tab-content active">
  <div class="grid" id="summary-cards"></div>

  <div class="section">
    <h2>Active Users</h2>
    <div class="table-wrap" id="active-table"></div>
  </div>

  <div class="section">
    <h2>Client Versions</h2>
    <div class="chart-container">
      <canvas id="pie-chart" class="pie" width="220" height="220"></canvas>
      <div id="pie-legend" class="legend"></div>
    </div>
    <div class="table-wrap" id="versions-table" style="margin-top:14px"></div>
  </div>
</div>

<!-- ==================== REGION HEALTH TAB ==================== -->
<div id="tab-health" class="tab-content">
  <div class="section">
    <h2>Region Health (heartbeat)</h2>
    <p style="color:#888;margin-bottom:10px">All regions with live FPS — updates every 30s, even when empty.</p>
    <div class="table-wrap" id="health-table"></div>
  </div>
</div>

<!-- ==================== REGIONS TAB ==================== -->
<div id="tab-regions" class="tab-content">
  <div class="section">
    <h2>Per-Region Breakdown</h2>
    <div class="table-wrap" id="regions-table"></div>
  </div>
</div>

<!-- ==================== SESSIONS TAB ==================== -->
<div id="tab-sessions" class="tab-content">
  <div class="section">
    <h2>Recent Sessions</h2>
    <div class="table-wrap" id="sessions-table"></div>
  </div>
</div>

<!-- ==================== MONITOR TAB ==================== -->
<div id="tab-monitor" class="tab-content">
  <div class="section">
    <h2>Sim Metrics</h2>
    <p style="color:#888;margin-bottom:10px">Per-region stats from MetricsConnector addon.</p>
    <div id="monitor-content"><div class="empty">Loading...</div></div>
  </div>
</div>

<div class="footer">
  SStats MySQL Dashboard &bull; Table: __TABLE_NAME__ &bull; Rows: __ROW_COUNT__ &bull; __TIMESTAMP__
</div>

<script>
// === Data injected by server ===
var SUMMARY = __SUMMARY_JSON__;
var ACTIVE = __ACTIVE_JSON__;
var VERSIONS = __VERSIONS_JSON__;
var SESSIONS = __SESSIONS_JSON__;
var REGIONS = __REGIONS_JSON__;
var HEALTH = __HEALTH_JSON__;

// === Region Health table ===
(function(){
  var el = document.getElementById('health-table');
  if(!HEALTH.length){ el.innerHTML='<div class="empty">No heartbeat data yet. Restart sims to activate.</div>'; return; }
  var h='<table><tr><th>Region</th><th>Status</th><th>Sim FPS</th><th>Last Update</th><th>Age</th></tr>';
  HEALTH.forEach(function(r){
    var cls = r.status==='alive'?'color:#4caf50':(r.status==='stale'?'color:#ff9800':'color:#e94560');
    h+='<tr><td title="'+esc(r.region_id)+'">'+esc(r.region_name)+'</td>'
      +'<td style="'+cls+'"><strong>'+esc(r.status)+'</strong></td>'
      +'<td>'+esc(String(r.sim_fps))+'</td>'
      +'<td>'+esc(r.last_update)+'</td>'
      +'<td>'+esc(r.age_seconds)+'s</td></tr>';
  });
  h+='</table>';
  el.innerHTML=h;
})();

// === Tab switching ===
function showTab(name) {
  document.querySelectorAll('.tab-content').forEach(function(el){ el.classList.remove('active') });
  document.querySelectorAll('.tab-btn').forEach(function(el){ el.classList.remove('active') });
  document.getElementById('tab-' + name).classList.add('active');
  var btns = document.querySelectorAll('.tab-btn');
  var names = ['overview','regions','sessions','monitor'];
  var idx = names.indexOf(name);
  if(idx >= 0 && btns[idx]) btns[idx].classList.add('active');
  if(name === 'monitor') loadMonitor();
}

// === Escape HTML ===
function esc(s) {
  var d = document.createElement('div');
  d.appendChild(document.createTextNode(s));
  return d.innerHTML;
}

// === Summary cards ===
(function(){
  var cards = document.getElementById('summary-cards');
  var items = [
    {label:'Active Users', value:SUMMARY.total_users||0, cls:''},
    {label:'Total Sessions', value:SUMMARY.total_sessions||0, cls:''},
    {label:'Avg Client FPS', value:SUMMARY.avg_client_fps||0, cls:'green'},
    {label:'Avg Sim FPS', value:SUMMARY.avg_sim_fps||0, cls:'green'},
    {label:'Avg Ping', value:(SUMMARY.avg_ping||0)+'ms', cls: (SUMMARY.avg_ping>100?'orange':'')},
    {label:'Avg Memory', value:(SUMMARY.avg_mem_use||0)+'MB', cls:'accent'},
    {label:'KB Out', value:SUMMARY.total_kb_out||0, cls:''},
    {label:'KB In', value:SUMMARY.total_kb_in||0, cls:''}
  ];
  var html = '';
  items.forEach(function(c){
    html += '<div class="card '+c.cls+'"><div class="label">'+esc(c.label)+'</div><div class="value">'+esc(String(c.value))+'</div></div>';
  });
  cards.innerHTML = html;
})();

// === Active users table ===
(function(){
  var el = document.getElementById('active-table');
  if(!ACTIVE.length){ el.innerHTML='<div class="empty">No users found</div>'; return; }
  var online=ACTIVE.filter(function(r){return r.online;});
  var offline=ACTIVE.filter(function(r){return !r.online;});
  var show=[].concat(online,offline);
  var h='<table><tr><th></th><th>Name</th><th>FPS</th><th>Ping</th><th>Mem</th><th>Region</th><th>Client</th><th>Last Seen</th></tr>';
  show.forEach(function(r){
    var dot=r.online?'&#x1F7E2;':'&#x1F534;';
    h+='<tr style="'+(r.online?'':'opacity:0.45')+'"><td>'+dot+'</td><td>'+esc(r.name)+'</td><td>'+esc(String(r.fps))+'</td>'
      +'<td'+(r.ping>100?' style="color:#ff9800"':'')+'>'+esc(String(r.ping))+'</td>'
      +'<td>'+esc(String(r.mem_mb))+'</td>'
      +'<td class="mono" title="'+esc(r.region_id)+'">'+esc(r.region_name)+'</td>'
      +'<td class="mono">'+esc(r.client)+'</td>'
      +'<td>'+esc(r.last_seen)+'</td></tr>';
  });
  h+='</table>';
  if(online.length) h='<p style="color:#4caf50;margin-bottom:8px">'+online.length+' online now</p>'+h;
  el.innerHTML=h;
})();

// === Client versions table + pie chart ===
(function(){
  var el = document.getElementById('versions-table');
  var legend = document.getElementById('pie-legend');
  if(!VERSIONS.length){ el.innerHTML='<div class="empty">No version data</div>'; return; }
  var h='<table><tr><th>Version</th><th>Count</th><th>Avg FPS</th></tr>';
  VERSIONS.forEach(function(v){
    h+='<tr><td class="mono">'+esc(v.version)+'</td><td>'+esc(String(v.count))+'</td><td>'+esc(String(v.avg_fps))+'</td></tr>';
  });
  h+='</table>';
  el.innerHTML=h;

  // Pie chart
  var canvas = document.getElementById('pie-chart');
  if(!canvas.getContext) return;
  var ctx = canvas.getContext('2d');
  var colors=['#00d2ff','#7b61ff','#4caf50','#ff9800','#e94560','#00bcd4','#9c27b0','#ff5722','#607d8b','#8bc34a','#ffc107','#3f51b5','#f44336','#2196f3','#cddc39'];
  var total = VERSIONS.reduce(function(s,v){ return s+v.count; },0);
  var angle = -Math.PI/2;
  var legHtml = '';
  VERSIONS.forEach(function(v,i){
    var slice = (v.count/total)*2*Math.PI;
    ctx.beginPath();
    ctx.moveTo(110,110);
    ctx.arc(110,110,100,angle,angle+slice);
    ctx.closePath();
    ctx.fillStyle=colors[i%colors.length];
    ctx.fill();
    angle+=slice;
    legHtml+='<span style="background:'+colors[i%colors.length]+'"></span>'+esc(v.version)+' ('+v.count+')<br>';
  });
  // Inner circle for donut
  ctx.beginPath();
  ctx.arc(110,110,55,0,2*Math.PI);
  ctx.fillStyle='#0a0e1a';
  ctx.fill();
  // Center text
  ctx.fillStyle='#00d2ff';
  ctx.font='bold 24px Segoe UI,sans-serif';
  ctx.textAlign='center';
  ctx.fillText(String(total),110,108);
  ctx.fillStyle='#556';
  ctx.font='11px Segoe UI,sans-serif';
  ctx.fillText('total',110,124);
  legend.innerHTML=legHtml;
})();

// === Regions table ===
(function(){
  var el = document.getElementById('regions-table');
  if(!REGIONS.length){ el.innerHTML='<div class="empty">No region data available</div>'; return; }
  var h='<table><tr><th>Region</th><th>Users</th><th>Sessions</th><th>Avg FPS</th><th>Avg Ping</th><th>Avg Sim FPS</th><th>Avg Mem</th></tr>';
  REGIONS.forEach(function(r){
    h+='<tr><td title="'+esc(r.region_id)+'">'+esc(r.region_name)+'</td>'
      +'<td><strong>'+esc(String(r.users))+'</strong></td>'
      +'<td>'+esc(String(r.sessions))+'</td>'
      +'<td'+(r.avg_fps<20?' style="color:#e94560"':' style="color:#4caf50"')+'>'+esc(String(r.avg_fps))+'</td>'
      +'<td'+(r.avg_ping>100?' style="color:#ff9800"':'')+'>'+esc(String(r.avg_ping))+'</td>'
      +'<td>'+esc(String(r.avg_sim_fps))+'</td>'
      +'<td>'+esc(String(r.avg_mem))+'</td></tr>';
  });
  h+='</table>';
  el.innerHTML=h;
})();

// === Sessions table ===
(function(){
  var el = document.getElementById('sessions-table');
  if(!SESSIONS.length){ el.innerHTML='<div class="empty">No sessions recorded</div>'; return; }
  var h='<table><tr><th>Name</th><th>Region</th><th>FPS</th><th>Ping</th><th>Mem</th><th>KB Out</th><th>KB In</th><th>Run Time</th><th>Last Updated</th></tr>';
  SESSIONS.forEach(function(r){
    h+='<tr><td>'+esc(r.name)+'</td>'
      +'<td class="mono" title="'+esc(r.region_id)+'">'+esc(r.region_name)+'</td>'
      +'<td>'+esc(String(r.avg_fps))+'</td>'
      +'<td>'+esc(String(r.avg_ping))+'</td>'
      +'<td>'+esc(String(r.mem_mb))+'</td>'
      +'<td>'+esc(String(r.kb_out))+'</td>'
      +'<td>'+esc(String(r.kb_in))+'</td>'
      +'<td>'+esc(String(Math.round(r.run_time)))+'s</td>'
      +'<td>'+esc(r.last_updated)+'</td></tr>';
  });
  h+='</table>';
  el.innerHTML=h;
})();

// === Monitor (lazy load) ===
function loadMonitor(){
  var el = document.getElementById('monitor-content');
  el.innerHTML='<div class="empty">Fetching metrics from sims...</div>';
  fetch('/api/monitor')
    .then(function(r){ return r.json(); })
    .then(function(data){
      if(!data.regions || !data.regions.length){
        var msg = data.errors && data.errors.length ? data.errors.join('; ') : 'No metrics available';
        el.innerHTML='<div class="empty" style="color:#ff9800">'+esc(msg)+'</div>';
        return;
      }
      var html='<table><tr><th>Region</th><th>SimFPS</th><th>PhysFPS</th><th>Dilation</th><th>Agents</th><th>Objects</th><th>Scripts</th><th>Frame ms</th><th>Net ms</th><th>In pps</th><th>Out pps</th><th>Script ms</th><th>Pending DL</th><th>Memory</th><th>GC0/1/2</th><th>Handles</th><th>Threads</th><th>WorkingSet</th><th>Uptime</th></tr>';
      data.regions.forEach(function(r){
        var uptime = r.uptime_seconds ? Math.floor(r.uptime_seconds/3600)+'h '+Math.floor((r.uptime_seconds%3600)/60)+'m' : '?';
        var gc = (r.gc_gen0||0)+'/'+(r.gc_gen1||0)+'/'+(r.gc_gen2||0);
        html+='<tr><td>'+esc(r.region_name||'?')+'</td>'
          +'<td'+(r.sim_fps<20?' style="color:#e94560"':' style="color:#4caf50"')+'>'+esc(String(r.sim_fps||0))+'</td>'
          +'<td>'+esc(String(r.physics_fps||0))+'</td>'
          +'<td>'+esc(String(r.time_dilation||0))+'</td>'
          +'<td>'+esc(String(r.agents||0))+'</td>'
          +'<td>'+esc(String(r.objects||0))+'</td>'
          +'<td>'+esc(String(r.active_scripts||0))+'</td>'
          +'<td'+(r.frame_ms>50?' style="color:#ff9800"':'')+'>'+esc(String(r.frame_ms||0))+'</td>'
          +'<td>'+esc(String(r.net_ms||0))+'</td>'
          +'<td>'+esc(String(r.in_packets_ps||0))+'</td>'
          +'<td>'+esc(String(r.out_packets_ps||0))+'</td>'
          +'<td>'+esc(String(r.script_ms||0))+'</td>'
          +'<td>'+esc(String(r.pending_downloads||0))+'</td>'
          +'<td>'+esc(String(Math.round(r.memory_mb||0)))+' MB</td>'
          +'<td>'+esc(gc)+'</td>'
          +'<td>'+esc(String(r.handle_count||0))+'</td>'
          +'<td>'+esc(String(r.native_threads||0))+'</td>'
          +'<td>'+esc(String(Math.round(r.working_set_mb||0)))+' MB</td>'
          +'<td>'+esc(uptime)+'</td></tr>';
      });
      html+='</table>';
      el.innerHTML=html;
    })
    .catch(function(e){ el.innerHTML='<div class="empty" style="color:#ff9800">'+esc(String(e))+'</div>'; });
}

// === Region Health table ===
</script>
</body>
</html>"""


# =============================================================================
# HTTP Handler
# =============================================================================
class SStatsHandler(BaseHTTPRequestHandler):
    db = None  # Set by main()
    server_version = "SStats-MySQL/2.0"

    def log_message(self, fmt, *args):
        log.info(f"{self.client_address[0]} - {fmt % args}")

    def _get_ip(self):
        return self.client_address[0]

    def _extract_auth(self):
        auth = self.headers.get("Authorization", "")
        if auth.startswith("Basic "):
            try:
                decoded = base64.b64decode(auth[6:]).decode("utf-8", errors="replace")
                u, p = decoded.split(":", 1)
                return u, p
            except Exception:
                pass
        return None, None

    def _get_cookie(self, name):
        cookie_header = self.headers.get("Cookie", "")
        if not cookie_header:
            return None
        for part in cookie_header.split(";"):
            part = part.strip()
            if part.startswith(name + "="):
                return part[len(name) + 1:]
        return None

    def _send_login(self, error=None):
        err_html = ""
        if error:
            err_html = '<div class="err">{}</div>'.format(html_module.escape(str(error)))
        body = LOGIN_PAGE.replace("__ERROR_HTML__", err_html).encode("utf-8")
        self._send_raw(401, "text/html; charset=utf-8", body,
                       extra_headers=[("WWW-Authenticate", 'Basic realm="SStats"')])

    def _send_html(self, body_str, code=200):
        body = body_str.encode("utf-8") if isinstance(body_str, str) else body_str
        self._send_raw(code, "text/html; charset=utf-8", body)

    def _send_json(self, data, code=200):
        body = json.dumps(data, indent=2, default=str).encode("utf-8")
        self._send_raw(code, "application/json; charset=utf-8", body)

    def _send_raw(self, code, content_type, body, extra_headers=None):
        try:
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store, no-cache, must-revalidate")
            if extra_headers:
                for hk, hv in extra_headers:
                    self.send_header(hk, hv)
            self.end_headers()
            self.wfile.write(body)
        except Exception as e:
            log.warning(f"Failed to send response (code={code}): {e}")

    def _send_error_page(self, code, title, message):
        body = (
            "<!DOCTYPE html><html><head><title>{title}</title>"
            "<style>body{{font-family:sans-serif;background:#0a0e1a;color:#e0e0e0;"
            "display:flex;justify-content:center;align-items:center;min-height:100vh;}}"
            ".box{{background:#16213e;border:1px solid #0f3460;border-radius:12px;"
            "padding:40px;text-align:center;max-width:500px;}}"
            "h1{{color:#e94560;}}p{{color:#888;}}</style></head>"
            "<body><div class='box'><h1>{title}</h1><p>{message}</p></div></body></html>"
        ).format(title=html_module.escape(str(title)), message=html_module.escape(str(message)))
        self._send_raw(code, "text/html; charset=utf-8", body.encode("utf-8"))

    def _check_auth(self):
        token = self._get_cookie("sstats_session")
        if validate_session(token):
            return True
        u, p = self._extract_auth()
        if u and p:
            ip = self._get_ip()
            if not check_rate_limit(ip):
                self._send_login("Too many attempts. Please wait.")
                return False
            if u == PROXY_USERNAME and p == PROXY_PASSWORD:
                clear_failed_login(ip)
                return True
            else:
                record_failed_login(ip)
                self._send_login("Invalid username or password.")
                return False
        self._send_login()
        return False

    # --- GET routes ---

    def do_GET(self):
        try:
            self._do_GET_inner()
        except Exception as e:
            log.error(f"Unhandled exception in do_GET: {e}\n{traceback.format_exc()}")
            try:
                self._send_error_page(500, "Internal Server Error",
                                       "An unexpected error occurred. Check server logs.")
            except Exception:
                pass

    def _do_GET_inner(self):
        path = urlparse(self.path).path

        if path == "/health":
            self._send_json({
                "status": "ok",
                "mysql_opensim": self.db.opensim.connected if self.db else False,
                "mysql_robust": self.db.robust.connected if self.db else False,
                "table": MYSQL_TABLE,
                "version": "2.0"
            })
            return

        if path == "/login":
            self._send_login()
            return

        if path == "/logout":
            t = self._get_cookie("sstats_session")
            if t and t in sessions:
                del sessions[t]
            self.send_response(302)
            self.send_header("Location", "/login")
            self.send_header("Set-Cookie", "sstats_session=; Path=/; Max-Age=0")
            self.end_headers()
            return

        if path == "/api/stats":
            if not self._check_auth():
                return
            data = {
                "summary": {},
                "active_users": [],
                "client_versions": [],
                "recent_sessions": [],
                "table_info": {},
            }
            if self.db and self.db.connected:
                try:
                    data["summary"] = self.db.get_summary()
                except Exception as e:
                    log.error(f"API get_summary: {e}")
                try:
                    data["active_users"] = self.db.get_active_users()
                except Exception as e:
                    log.error(f"API get_active_users: {e}")
                try:
                    data["client_versions"] = self.db.get_client_versions()
                except Exception as e:
                    log.error(f"API get_client_versions: {e}")
                try:
                    data["recent_sessions"] = self.db.get_recent_sessions(50)
                except Exception as e:
                    log.error(f"API get_recent_sessions: {e}")
                try:
                    data["table_info"] = self.db.get_table_info()
                except Exception as e:
                    log.error(f"API get_table_info: {e}")
            self._send_json(data)
            return

        if path == "/api/health":
            if not self._check_auth():
                return
            data = {"regions": []}
            if self.db and self.db.connected:
                try:
                    data["regions"] = self.db.get_region_health()
                except Exception as e:
                    log.error(f"API get_region_health: {e}")
            self._send_json(data)
            return

        if path == "/api/regions":
            if not self._check_auth():
                return
            data = {"regions": []}
            if self.db and self.db.connected:
                try:
                    data["regions"] = self.db.get_region_stats()
                except Exception as e:
                    log.error(f"API get_region_stats: {e}")
            self._send_json(data)
            return

        if path == "/api/monitor":
            if not self._check_auth():
                return
            data = fetch_monitor_metrics()
            self._send_json(data)
            return

        if path == "/" or path == "/stats" or path == "/stats/":
            if not self._check_auth():
                return
            try:
                dashboard = build_dashboard_html(self.db)
                self._send_html(dashboard)
            except Exception as e:
                log.error(f"Dashboard render error: {e}\n{traceback.format_exc()}")
                self._send_error_page(500, "Dashboard Error",
                                       "Failed to render dashboard: {}".format(html_module.escape(str(e))))
            return

        self.send_response(404)
        self.end_headers()

    # --- POST routes ---

    def do_POST(self):
        try:
            self._do_POST_inner()
        except Exception as e:
            log.error(f"Unhandled exception in do_POST: {e}\n{traceback.format_exc()}")
            try:
                self._send_error_page(500, "Internal Server Error",
                                       "An unexpected error occurred.")
            except Exception:
                pass

    def _do_POST_inner(self):
        path = urlparse(self.path).path

        if path == "/login":
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length).decode("utf-8", errors="replace")
            params = dict(parse_qs(body))
            u = params.get("username", [""])[0]
            p = params.get("password", [""])[0]
            ip = self._get_ip()

            if not check_rate_limit(ip):
                self._send_login("Too many attempts.")
                return

            if u == PROXY_USERNAME and p == PROXY_PASSWORD:
                clear_failed_login(ip)
                token = generate_session_token(u)
                self.send_response(302)
                self.send_header("Location", "/stats/")
                self.send_header("Set-Cookie",
                    "sstats_session={}; Path=/; HttpOnly; SameSite=Lax; Max-Age={}".format(
                        token, SESSION_TIMEOUT))
                self.end_headers()
                return
            else:
                record_failed_login(ip)
                self._send_login("Invalid username or password.")
                return

        if path == "/api/stats":
            if not self._check_auth():
                return
            length = int(self.headers.get("Content-Length", 0))
            raw = self.rfile.read(length)
            try:
                body = json.loads(raw)
            except json.JSONDecodeError as e:
                self._send_json({"error": "Invalid JSON: {}".format(str(e))}, 400)
                return
            affected = self.db.insert_session(body) if self.db else 0
            self._send_json({"inserted": affected})
            return

        self.send_response(404)
        self.end_headers()


# =============================================================================
# Main
# =============================================================================
def main():
    db = SStatsDB()

    robust_ok = db.robust.connected if db else False

    print(f"""
    ======================================================
       SStats MySQL Dashboard v2.0
       Tabbed UI with Region Names + Monitor
    ======================================================
      URL:      http://{PROXY_HOST}:{PROXY_PORT}
      MySQL:    {MYSQL_HOST}:{MYSQL_PORT}/{MYSQL_DB}
      Robust:   {MYSQL_HOST}:{MYSQL_PORT}/robust {'(OK)' if robust_ok else '(unavailable - region names will be UUIDs)'}
      Table:    {MYSQL_TABLE}
      User:     {PROXY_USERNAME}
      print(f"  Metrics:  {len(METRICS_PORT_MAP)} sims mapped")
      DB Conn:  {'OK' if db.connected else 'FAILED (check credentials)'}
      Threads:  ThreadingHTTPServer (concurrent)
    ======================================================
    """)

    SStatsHandler.db = db

    server = ThreadingHTTPServer((PROXY_HOST, PROXY_PORT), SStatsHandler)
    log.info(f"SStats MySQL Dashboard v2.0 (threaded) on {PROXY_HOST}:{PROXY_PORT}")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        log.info("Shutting down...")
        server.shutdown()
        db.close()


if __name__ == "__main__":
    main()
