#!/usr/bin/env python3
from __future__ import annotations

import argparse
import configparser
import contextlib
import dataclasses
import datetime as dt
import hashlib
import hmac
import logging
import re
import secrets
import signal
import socketserver
import threading
import time
import uuid
from typing import Any, Dict, Optional, Tuple
from xmlrpc.client import ServerProxy
from xmlrpc.server import SimpleXMLRPCRequestHandler, SimpleXMLRPCServer

import pymysql


LOG = logging.getLogger("tasia-moneyd")
UUID_ZERO = "00000000-0000-0000-0000-000000000000"

TXN_STATUS_SUCCESS = 0
TXN_STATUS_FAILED = 2

TXN_PAY_OBJECT = 5008
TXN_BUY_MONEY = 5010


def now_epoch() -> int:
    return int(time.time())


def as_bool(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    s = str(value).strip().lower()
    if s in {"1", "true", "yes", "on"}:
        return True
    if s in {"0", "false", "no", "off"}:
        return False
    return default


def as_int(value: Any, default: int = 0) -> int:
    try:
        return int(str(value).strip())
    except Exception:
        return default


def parse_csv_tokens(value: Any) -> list[str]:
    if value is None:
        return []
    out: list[str] = []
    raw = str(value).replace("\n", ",").replace(";", ",")
    for token in raw.split(","):
        t = token.strip()
        if t:
            out.append(t)
    return out


def ensure_uuid(text: str) -> str:
    return str(uuid.UUID(str(text))).lower()


@dataclasses.dataclass
class MoneydConfig:
    host: str
    port: int
    mysql_host: str
    mysql_port: int
    mysql_user: str
    mysql_password: str
    mysql_database: str
    robust_database: str
    default_balance: int
    enable_amount_zero: bool
    banker_avatar: str
    enable_force_transfer: bool
    enable_script_send_money: bool
    money_script_access_key: str
    money_script_ip_address: str
    trust_script_requests_without_remote_ip: bool
    buy_currency_cost_divisor: int
    helper_confirm_ttl_seconds: int
    helper_confirm_secret: str
    callback_timeout_seconds: int
    balance_message_buy_money: str
    balance_message_send_money: str
    balance_message_receive_money: str
    balance_message_buy_object: str
    balance_message_sell_object: str
    balance_message_pay_charge: str


@dataclasses.dataclass
class NotificationRules:
    enabled: bool = True
    mute_all: bool = False
    auto_reload_seconds: int = 3

    only_types: set[int] = dataclasses.field(default_factory=set)
    suppress_types: set[int] = dataclasses.field(default_factory=set)

    allow_sender_names: set[str] = dataclasses.field(default_factory=set)
    allow_receiver_names: set[str] = dataclasses.field(default_factory=set)
    allow_sender_uuids: set[str] = dataclasses.field(default_factory=set)
    allow_receiver_uuids: set[str] = dataclasses.field(default_factory=set)

    suppress_sender_names: set[str] = dataclasses.field(default_factory=set)
    suppress_receiver_names: set[str] = dataclasses.field(default_factory=set)
    suppress_sender_uuids: set[str] = dataclasses.field(default_factory=set)
    suppress_receiver_uuids: set[str] = dataclasses.field(default_factory=set)

    suppress_object_name_contains: list[str] = dataclasses.field(default_factory=list)
    suppress_object_name_regex: list[str] = dataclasses.field(default_factory=list)
    suppress_when_sender_equals_receiver: bool = False

    def should_suppress(
        self,
        *,
        txn_type: int,
        sender_uuid: str,
        receiver_uuid: str,
        sender_name: str,
        receiver_name: str,
        object_name: str,
    ) -> bool:
        if not self.enabled:
            return False
        if self.mute_all:
            return True

        su = (sender_uuid or "").strip().lower()
        ru = (receiver_uuid or "").strip().lower()
        sn = (sender_name or "").strip().lower()
        rn = (receiver_name or "").strip().lower()
        on = (object_name or "").strip()
        on_lower = on.lower()

        # Allowlist overrides all suppress rules.
        if su in self.allow_sender_uuids or ru in self.allow_receiver_uuids:
            return False
        if sn in self.allow_sender_names or rn in self.allow_receiver_names:
            return False

        if self.only_types and txn_type not in self.only_types:
            return True
        if txn_type in self.suppress_types:
            return True

        if self.suppress_when_sender_equals_receiver and su and su == ru:
            return True

        if su in self.suppress_sender_uuids or ru in self.suppress_receiver_uuids:
            return True
        if sn in self.suppress_sender_names or rn in self.suppress_receiver_names:
            return True

        for needle in self.suppress_object_name_contains:
            if needle and needle in on_lower:
                return True

        for rx in self.suppress_object_name_regex:
            try:
                if re.search(rx, on, flags=re.IGNORECASE):
                    return True
            except re.error:
                continue

        return False


def load_config(path: str) -> MoneydConfig:
    ini = configparser.ConfigParser(interpolation=None, inline_comment_prefixes=(";", "#"))
    with open(path, "r", encoding="utf-8") as f:
        ini.read_file(f)

    mysql = ini["MySql"] if ini.has_section("MySql") else {}
    money = ini["MoneyServer"] if ini.has_section("MoneyServer") else {}

    host = "0.0.0.0"
    port = as_int(money.get("ServerPort", "1026"), 1026)

    confirm_secret = (
        str(__import__("os").environ.get("TASIA_MONEYD_CONFIRM_SECRET", "")).strip()
        or secrets.token_hex(32)
    )

    return MoneydConfig(
        host=str(__import__("os").environ.get("TASIA_MONEYD_HOST", host)).strip() or host,
        port=as_int(__import__("os").environ.get("TASIA_MONEYD_PORT", str(port)), port),
        mysql_host=str(mysql.get("hostname", "127.0.0.1")).strip(),
        mysql_port=as_int(mysql.get("port", "3306"), 3306),
        mysql_user=str(mysql.get("username", "root")).strip(),
        mysql_password=str(mysql.get("password", "")).strip(),
        mysql_database=str(mysql.get("database", "money")).strip(),
        robust_database="robust",
        default_balance=as_int(money.get("DefaultBalance", "1000"), 1000),
        enable_amount_zero=as_bool(money.get("EnableAmountZero", "false"), False),
        banker_avatar=str(money.get("BankerAvatar", UUID_ZERO)).strip().strip('"') or UUID_ZERO,
        enable_force_transfer=as_bool(money.get("EnableForceTransfer", "true"), True),
        enable_script_send_money=as_bool(money.get("EnableScriptSendMoney", "false"), False),
        money_script_access_key=str(money.get("MoneyScriptAccessKey", "")).strip().strip('"'),
        money_script_ip_address=str(money.get("MoneyScriptIPaddress", "")).strip().strip('"'),
        trust_script_requests_without_remote_ip=True,
        buy_currency_cost_divisor=2,
        helper_confirm_ttl_seconds=300,
        helper_confirm_secret=confirm_secret,
        callback_timeout_seconds=5,
        balance_message_buy_money=str(money.get("BalanceMessageBuyMoney", "Bought {0}.")),
        balance_message_send_money=str(money.get("BalanceMessageSendMoney", "Paid {0} to {1}.")),
        balance_message_receive_money=str(money.get("BalanceMessageReceiveMoney", "Received {0} from {1}.")),
        balance_message_buy_object=str(money.get("BalanceMessageBuyObject", "Bought object by {0}.")),
        balance_message_sell_object=str(money.get("BalanceMessageSellObject", "Sold object by {0}.")),
        balance_message_pay_charge=str(money.get("BalanceMessagePayCharge", "Paid charge {0}.")),
    )


def load_notification_rules(path: str) -> NotificationRules:
    ini = configparser.ConfigParser(interpolation=None, inline_comment_prefixes=(";", "#"))
    with open(path, "r", encoding="utf-8") as f:
        ini.read_file(f)

    section = ini["MoneyDNotifications"] if ini.has_section("MoneyDNotifications") else {}

    def _lower_set(key: str) -> set[str]:
        return {t.strip().lower() for t in parse_csv_tokens(section.get(key, "")) if t.strip()}

    def _int_set(key: str) -> set[int]:
        values = set()
        for token in parse_csv_tokens(section.get(key, "")):
            try:
                values.add(int(token.strip()))
            except Exception:
                continue
        return values

    return NotificationRules(
        enabled=as_bool(section.get("Enabled", "true"), True),
        mute_all=as_bool(section.get("MuteAll", "false"), False),
        auto_reload_seconds=max(1, as_int(section.get("AutoReloadSeconds", "3"), 3)),
        only_types=_int_set("OnlyTypes"),
        suppress_types=_int_set("SuppressTypes"),
        allow_sender_names=_lower_set("AllowSenderNames"),
        allow_receiver_names=_lower_set("AllowReceiverNames"),
        allow_sender_uuids=_lower_set("AllowSenderUUIDs"),
        allow_receiver_uuids=_lower_set("AllowReceiverUUIDs"),
        suppress_sender_names=_lower_set("SuppressSenderNames"),
        suppress_receiver_names=_lower_set("SuppressReceiverNames"),
        suppress_sender_uuids=_lower_set("SuppressSenderUUIDs"),
        suppress_receiver_uuids=_lower_set("SuppressReceiverUUIDs"),
        suppress_object_name_contains=[t.lower() for t in parse_csv_tokens(section.get("SuppressObjectNameContains", ""))],
        suppress_object_name_regex=parse_csv_tokens(section.get("SuppressObjectNameRegex", "")),
        suppress_when_sender_equals_receiver=as_bool(section.get("SuppressWhenSenderEqualsReceiver", "false"), False),
    )


class DB:
    def __init__(self, cfg: MoneydConfig):
        self.cfg = cfg

    @contextlib.contextmanager
    def conn(self):
        c = pymysql.connect(
            host=self.cfg.mysql_host,
            port=self.cfg.mysql_port,
            user=self.cfg.mysql_user,
            password=self.cfg.mysql_password,
            database=self.cfg.mysql_database,
            charset="utf8mb4",
            autocommit=False,
            cursorclass=pymysql.cursors.DictCursor,
        )
        try:
            yield c
            c.commit()
        except Exception:
            c.rollback()
            raise
        finally:
            c.close()

    def _robust_conn(self):
        return pymysql.connect(
            host=self.cfg.mysql_host,
            port=self.cfg.mysql_port,
            user=self.cfg.mysql_user,
            password=self.cfg.mysql_password,
            database=self.cfg.robust_database,
            charset="utf8mb4",
            autocommit=True,
            cursorclass=pymysql.cursors.DictCursor,
        )

    def ensure_balance_row(self, user_id: str, initial: Optional[int] = None) -> None:
        if user_id == UUID_ZERO:
            return
        initial_balance = self.cfg.default_balance if initial is None else int(initial)
        with self.conn() as c, c.cursor() as cur:
            cur.execute(
                """
                INSERT INTO balances (`user`,`balance`,`status`,`type`)
                VALUES (%s,%s,%s,%s)
                ON DUPLICATE KEY UPDATE `user`=`user`
                """,
                (user_id, initial_balance, 0, 0),
            )

    def get_balance(self, user_id: str) -> int:
        if user_id == UUID_ZERO:
            return 0
        self.ensure_balance_row(user_id)
        with self.conn() as c, c.cursor() as cur:
            cur.execute("SELECT balance FROM balances WHERE `user`=%s", (user_id,))
            row = cur.fetchone()
            return int(row["balance"]) if row else self.cfg.default_balance

    def validate_presence_session(self, user_id: str, session_id: str, secure_session_id: str) -> bool:
        try:
            user_id = ensure_uuid(user_id)
            secure_session_id = ensure_uuid(secure_session_id)
            session_zero = str(session_id).strip().lower() == UUID_ZERO
            if not session_zero:
                session_id = ensure_uuid(session_id)
        except Exception:
            return False

        conn = self._robust_conn()
        try:
            with conn.cursor() as cur:
                if session_zero:
                    cur.execute(
                        "SELECT 1 FROM Presence WHERE UserID=%s AND SecureSessionID=%s LIMIT 1",
                        (user_id, secure_session_id),
                    )
                else:
                    cur.execute(
                        "SELECT 1 FROM Presence WHERE UserID=%s AND SessionID=%s AND SecureSessionID=%s LIMIT 1",
                        (user_id, session_id, secure_session_id),
                    )
                return cur.fetchone() is not None
        finally:
            conn.close()

    def get_presence_session(self, user_id: str) -> Optional[Tuple[str, str]]:
        try:
            uid = ensure_uuid(user_id)
        except Exception:
            return None

        conn = self._robust_conn()
        try:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT SessionID, SecureSessionID
                    FROM Presence
                    WHERE UserID=%s
                    ORDER BY LastSeen DESC
                    LIMIT 1
                    """,
                    (uid,),
                )
                row = cur.fetchone()
                if not row:
                    return None
                sid = str(row.get("SessionID") or "").strip().lower()
                ssid = str(row.get("SecureSessionID") or "").strip().lower()
                if not sid or not ssid:
                    return None
                return (sid, ssid)
        finally:
            conn.close()

    def resolve_region_server_uri_for_user(self, user_id: str) -> Optional[str]:
        conn = self._robust_conn()
        try:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT r.serverURI AS server_uri
                    FROM Presence p
                    JOIN regions r ON r.uuid = p.RegionID
                    WHERE p.UserID=%s
                    LIMIT 1
                    """,
                    (user_id,),
                )
                row = cur.fetchone()
                if row and row.get("server_uri"):
                    return str(row["server_uri"]).rstrip("/") + "/"

                cur.execute(
                    """
                    SELECT r.serverURI AS server_uri
                    FROM GridUser g
                    JOIN regions r ON r.uuid = g.LastRegionID
                    WHERE g.UserID=%s
                    LIMIT 1
                    """,
                    (user_id,),
                )
                row = cur.fetchone()
                if row and row.get("server_uri"):
                    return str(row["server_uri"]).rstrip("/") + "/"
                return None
        finally:
            conn.close()

    def get_user_name(self, user_id: str) -> str:
        if not user_id or user_id == UUID_ZERO:
            return "SYSTEM"
        try:
            uid = ensure_uuid(user_id)
        except Exception:
            return str(user_id)

        conn = self._robust_conn()
        try:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT FirstName, LastName
                    FROM UserAccounts
                    WHERE PrincipalID=%s
                    LIMIT 1
                    """,
                    (uid,),
                )
                row = cur.fetchone()
                if not row:
                    return uid
                first = str(row.get("FirstName") or "").strip()
                last = str(row.get("LastName") or "").strip()
                full = (first + " " + last).strip()
                return full or uid
        except Exception:
            return uid
        finally:
            conn.close()

    def _insert_transaction(
        self,
        cur,
        *,
        txn_uuid: str,
        sender: str,
        receiver: str,
        amount: int,
        sender_balance: int,
        receiver_balance: int,
        object_uuid: str,
        object_name: str,
        region_handle: str,
        region_uuid: str,
        txn_type: int,
        secure: str,
        status: int,
        common_name: str,
        description: str,
    ):
        cur.execute(
            """
            INSERT INTO transactions
            (`UUID`,`sender`,`receiver`,`amount`,`senderBalance`,`receiverBalance`,`objectUUID`,`objectName`,
             `regionHandle`,`regionUUID`,`type`,`time`,`secure`,`status`,`commonName`,`description`)
            VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
            """,
            (
                txn_uuid,
                sender,
                receiver,
                amount,
                sender_balance,
                receiver_balance,
                object_uuid,
                object_name,
                region_handle,
                region_uuid,
                txn_type,
                now_epoch(),
                secure,
                status,
                common_name,
                description,
            ),
        )

    def transfer(
        self,
        *,
        sender: str,
        receiver: str,
        amount: int,
        txn_type: int,
        object_uuid: str,
        object_name: str,
        region_handle: str,
        region_uuid: str,
        description: str,
        secure_code: Optional[str] = None,
        allow_zero_amount: bool = False,
    ) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
        if amount < 0:
            return False, "amount must be >= 0", None
        if amount == 0 and not allow_zero_amount and not self.cfg.enable_amount_zero:
            return False, "zero amount not allowed", None

        sender = ensure_uuid(sender)
        receiver = ensure_uuid(receiver)
        object_uuid = ensure_uuid(object_uuid) if object_uuid else UUID_ZERO
        region_uuid = ensure_uuid(region_uuid) if region_uuid else UUID_ZERO
        secure = secure_code or str(uuid.uuid4())
        txn_uuid = str(uuid.uuid4())

        with self.conn() as c, c.cursor() as cur:
            if sender != UUID_ZERO:
                cur.execute(
                    "INSERT INTO balances (`user`,`balance`,`status`,`type`) VALUES (%s,%s,%s,%s) ON DUPLICATE KEY UPDATE `user`=`user`",
                    (sender, self.cfg.default_balance, 0, 0),
                )
            if receiver != UUID_ZERO:
                cur.execute(
                    "INSERT INTO balances (`user`,`balance`,`status`,`type`) VALUES (%s,%s,%s,%s) ON DUPLICATE KEY UPDATE `user`=`user`",
                    (receiver, self.cfg.default_balance, 0, 0),
                )

            sender_balance = -1
            if sender != UUID_ZERO:
                cur.execute("SELECT balance FROM balances WHERE `user`=%s FOR UPDATE", (sender,))
                row = cur.fetchone()
                if not row:
                    return False, "sender not found", None
                sender_balance = int(row["balance"])
                if sender_balance < amount:
                    return False, "insufficient funds", None

            receiver_balance = -1
            if receiver != UUID_ZERO:
                cur.execute("SELECT balance FROM balances WHERE `user`=%s FOR UPDATE", (receiver,))
                row = cur.fetchone()
                if not row:
                    return False, "receiver not found", None
                receiver_balance = int(row["balance"])

            if sender != UUID_ZERO and amount > 0:
                sender_balance -= amount
                cur.execute("UPDATE balances SET balance=%s WHERE `user`=%s", (sender_balance, sender))

            if receiver != UUID_ZERO and amount > 0:
                receiver_balance += amount
                cur.execute("UPDATE balances SET balance=%s WHERE `user`=%s", (receiver_balance, receiver))

            self._insert_transaction(
                cur,
                txn_uuid=txn_uuid,
                sender=sender,
                receiver=receiver,
                amount=amount,
                sender_balance=sender_balance,
                receiver_balance=receiver_balance,
                object_uuid=object_uuid,
                object_name=object_name,
                region_handle=str(region_handle or "0"),
                region_uuid=region_uuid,
                txn_type=txn_type,
                secure=secure,
                status=TXN_STATUS_SUCCESS,
                common_name="MoneyD",
                description=description[:255],
            )

            return True, "ok", {
                "transaction_uuid": txn_uuid,
                "sender": sender,
                "receiver": receiver,
                "amount": amount,
                "sender_balance": sender_balance,
                "receiver_balance": receiver_balance,
                "transaction_type": txn_type,
                "object_uuid": object_uuid,
                "object_name": object_name,
                "region_handle": str(region_handle or "0"),
                "region_uuid": region_uuid,
                "secure": secure,
            }

    def rollback_transfer(self, tx: Dict[str, Any], reason: str) -> None:
        sender = tx["sender"]
        receiver = tx["receiver"]
        amount = int(tx["amount"])
        txn_uuid = tx["transaction_uuid"]
        with self.conn() as c, c.cursor() as cur:
            if sender != UUID_ZERO:
                cur.execute("SELECT balance FROM balances WHERE `user`=%s FOR UPDATE", (sender,))
                srow = cur.fetchone()
                if srow:
                    cur.execute("UPDATE balances SET balance=%s WHERE `user`=%s", (int(srow["balance"]) + amount, sender))

            if receiver != UUID_ZERO:
                cur.execute("SELECT balance FROM balances WHERE `user`=%s FOR UPDATE", (receiver,))
                rrow = cur.fetchone()
                if rrow:
                    new_bal = int(rrow["balance"]) - amount
                    if new_bal < 0:
                        new_bal = 0
                    cur.execute("UPDATE balances SET balance=%s WHERE `user`=%s", (new_bal, receiver))

            cur.execute(
                "UPDATE transactions SET status=%s, description=%s WHERE UUID=%s",
                (TXN_STATUS_FAILED, reason[:255], txn_uuid),
            )

    def get_transaction(self, txn_uuid: str) -> Optional[Dict[str, Any]]:
        with self.conn() as c, c.cursor() as cur:
            cur.execute(
                "SELECT UUID,sender,receiver,amount,type,description,status FROM transactions WHERE UUID=%s LIMIT 1",
                (txn_uuid,),
            )
            row = cur.fetchone()
            return row


@dataclasses.dataclass
class SessionRecord:
    user_id: str
    session_id: str
    secure_session_id: str
    sim_url: str
    user_name: str
    last_seen: float


@dataclasses.dataclass
class ConfirmRecord:
    token: str
    user_id: str
    secure_session_id: str
    amount: int
    kind: str
    expires_at: float


class MoneyService:
    def __init__(self, cfg: MoneydConfig, config_path: str):
        self.cfg = cfg
        self.config_path = config_path
        self.db = DB(cfg)
        self._sessions: Dict[str, SessionRecord] = {}
        self._session_lock = threading.Lock()
        self._confirms: Dict[str, ConfirmRecord] = {}
        self._confirm_lock = threading.Lock()
        self._rules_lock = threading.Lock()
        self._notification_rules = load_notification_rules(config_path)
        self._rules_mtime = self._safe_mtime(config_path)
        self._stop_event = threading.Event()
        self._rules_thread = threading.Thread(target=self._rules_reloader_loop, name="moneyd-rules-reloader", daemon=True)
        self._rules_thread.start()

    @staticmethod
    def _safe_mtime(path: str) -> float:
        try:
            return __import__("os").path.getmtime(path)
        except Exception:
            return 0.0

    def shutdown(self) -> None:
        self._stop_event.set()

    def _rules_reloader_loop(self) -> None:
        while not self._stop_event.is_set():
            with self._rules_lock:
                interval = max(1, int(self._notification_rules.auto_reload_seconds))
            if self._stop_event.wait(timeout=interval):
                return

            new_mtime = self._safe_mtime(self.config_path)
            if new_mtime <= 0 or new_mtime == self._rules_mtime:
                continue

            try:
                new_rules = load_notification_rules(self.config_path)
                with self._rules_lock:
                    self._notification_rules = new_rules
                    self._rules_mtime = new_mtime
                LOG.info("Notification rules reloaded from %s", self.config_path)
            except Exception as ex:
                LOG.warning("Notification rules reload failed: %s", ex)

    def _get_notification_rules(self) -> NotificationRules:
        with self._rules_lock:
            return self._notification_rules

    # ---------- helpers ----------
    def _ok(self, **data) -> Dict[str, Any]:
        payload = {"success": True}
        payload.update(data)
        return payload

    def _fail(self, message: str, **data) -> Dict[str, Any]:
        payload = {"success": False, "message": message}
        payload.update(data)
        return payload

    def _clean_old_confirms(self) -> None:
        now = time.time()
        with self._confirm_lock:
            stale = [k for k, v in self._confirms.items() if v.expires_at <= now]
            for k in stale:
                self._confirms.pop(k, None)

    def _make_confirm(self, user_id: str, secure_session_id: str, amount: int, kind: str) -> str:
        self._clean_old_confirms()
        nonce = secrets.token_hex(8)
        body = f"{user_id}|{secure_session_id}|{amount}|{kind}|{int(time.time())}|{nonce}"
        token = hmac.new(self.cfg.helper_confirm_secret.encode("utf-8"), body.encode("utf-8"), hashlib.sha256).hexdigest()
        rec = ConfirmRecord(
            token=token,
            user_id=user_id,
            secure_session_id=secure_session_id,
            amount=amount,
            kind=kind,
            expires_at=time.time() + self.cfg.helper_confirm_ttl_seconds,
        )
        with self._confirm_lock:
            self._confirms[token] = rec
        return token

    def _consume_confirm(self, token: str, user_id: str, secure_session_id: str, amount: int, kind: str) -> bool:
        self._clean_old_confirms()
        with self._confirm_lock:
            rec = self._confirms.pop(token, None)
        if not rec:
            return False
        return (
            rec.user_id == user_id
            and rec.secure_session_id == secure_session_id
            and rec.amount == amount
            and rec.kind == kind
            and rec.expires_at > time.time()
        )

    def _validate_session(self, user_id: str, session_id: str, secure_session_id: str) -> bool:
        try:
            uid = ensure_uuid(user_id)
            sid = str(session_id).strip().lower()
            ssid = ensure_uuid(secure_session_id)
        except Exception:
            return False

        with self._session_lock:
            rec = self._sessions.get(uid)
            if rec and rec.secure_session_id == ssid and (sid == UUID_ZERO or sid == rec.session_id):
                rec.last_seen = time.time()
                return True

        return self.db.validate_presence_session(uid, sid, ssid)

    def _remember_session(self, user_id: str, session_id: str, secure_session_id: str, sim_url: str, user_name: str) -> None:
        uid = ensure_uuid(user_id)
        with self._session_lock:
            self._sessions[uid] = SessionRecord(
                user_id=uid,
                session_id=str(session_id).strip().lower(),
                secure_session_id=str(secure_session_id).strip().lower(),
                sim_url=(sim_url or "").strip(),
                user_name=user_name or uid,
                last_seen=time.time(),
            )

    def _resolve_sim_url(self, user_id: str) -> Optional[str]:
        uid = ensure_uuid(user_id)
        with self._session_lock:
            rec = self._sessions.get(uid)
            if rec and rec.sim_url:
                return rec.sim_url
        return self.db.resolve_region_server_uri_for_user(uid)

    def _xmlrpc_call(self, endpoint: str, method: str, payload: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        if not endpoint:
            return None
        endpoint = endpoint.rstrip("/") + "/"
        try:
            with ServerProxy(endpoint, allow_none=True, use_builtin_types=True) as proxy:
                fn = getattr(proxy, method)
                result = fn(payload)
                return result if isinstance(result, dict) else None
        except Exception as ex:
            LOG.warning("xmlrpc callback failed method=%s endpoint=%s err=%s", method, endpoint, ex)
            return None

    def _update_balance_callback(self, user_id: str, message: str) -> None:
        if user_id == UUID_ZERO:
            return
        balance = self.db.get_balance(user_id)
        with self._session_lock:
            rec = self._sessions.get(user_id)
        if rec:
            endpoint = rec.sim_url
            session_id = rec.session_id
            secure_session_id = rec.secure_session_id
        else:
            endpoint = self._resolve_sim_url(user_id)
            session_pair = self.db.get_presence_session(user_id)
            if session_pair:
                session_id, secure_session_id = session_pair
            else:
                session_id = UUID_ZERO
                secure_session_id = UUID_ZERO

        if not endpoint:
            return

        self._xmlrpc_call(
            endpoint,
            "UpdateBalance",
            {
                "clientUUID": user_id,
                "clientSessionID": session_id,
                "clientSecureSessionID": secure_session_id,
                "Balance": int(balance),
                "Message": message,
            },
        )

    def _format_message(self, tmpl: str, amount: int, other_name: str, object_name: str = "") -> str:
        try:
            return tmpl.format(amount, other_name, object_name)
        except Exception:
            return f"{amount}"

    def _build_balance_messages(
        self,
        *,
        txn_type: int,
        amount: int,
        sender_name: str,
        receiver_name: str,
        object_name: str,
    ) -> Tuple[str, str]:
        # Returns (sender_message, receiver_message)
        if txn_type == TXN_PAY_OBJECT:
            sender_msg, receiver_msg = (
                self._format_message(self.cfg.balance_message_buy_object, amount, receiver_name, object_name),
                self._format_message(self.cfg.balance_message_sell_object, amount, sender_name, object_name),
            )
            return sender_msg, receiver_msg

        if txn_type == TXN_BUY_MONEY:
            sender_msg, receiver_msg = (
                "",
                self._format_message(self.cfg.balance_message_buy_money, amount, "SYSTEM", object_name),
            )
            return sender_msg, receiver_msg

        if txn_type == 5009:  # ObjectPays (script/object payout)
            # Do not notify payer side to avoid duplicate popups on scripted vendor flows.
            sender_msg, receiver_msg = (
                "",
                self._format_message(self.cfg.balance_message_receive_money, amount, sender_name, object_name),
            )
            return sender_msg, receiver_msg

        if txn_type == 5001:  # Gift
            sender_msg, receiver_msg = (
                self._format_message(self.cfg.balance_message_send_money, amount, receiver_name, object_name),
                self._format_message(self.cfg.balance_message_receive_money, amount, sender_name, object_name),
            )
            return sender_msg, receiver_msg

        if txn_type in {1100, 1101, 1102, 1103}:  # charge-ish
            sender_msg, receiver_msg = (
                self._format_message(self.cfg.balance_message_pay_charge, amount, "SYSTEM", object_name),
                "",
            )
            return sender_msg, receiver_msg

        sender_msg, receiver_msg = (
            self._format_message(self.cfg.balance_message_send_money, amount, receiver_name, object_name),
            self._format_message(self.cfg.balance_message_receive_money, amount, sender_name, object_name),
        )
        return sender_msg, receiver_msg

    # ---------- region money rpc ----------
    def ClientLogin(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("clientUUID", "")).strip().lower()
        session_id = str(params.get("clientSessionID", "")).strip().lower()
        secure_session_id = str(params.get("clientSecureSessionID", "")).strip().lower()
        sim_url = str(params.get("openSimServIP", "")).strip()
        user_name = str(params.get("userName", user_id)).strip()

        try:
            user_id = ensure_uuid(user_id)
            session_id = ensure_uuid(session_id)
            secure_session_id = ensure_uuid(secure_session_id)
        except Exception:
            return self._fail("Invalid login parameters")

        # Login must be permissive enough to bootstrap session cache.
        # Validate against Presence when possible, but do not hard-fail if Presence
        # is not yet updated at exact login timing.
        if not self.db.validate_presence_session(user_id, session_id, secure_session_id):
            LOG.warning("ClientLogin accepted without Presence match user=%s", user_id)

        self.db.ensure_balance_row(user_id)
        self._remember_session(user_id, session_id, secure_session_id, sim_url, user_name)
        bal = self.db.get_balance(user_id)
        return self._ok(clientBalance=int(bal))

    def ClientLogout(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("clientUUID", "")).strip().lower()
        with self._session_lock:
            self._sessions.pop(user_id, None)
        return self._ok()

    def GetBalance(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("clientUUID", "")).strip().lower()
        session_id = str(params.get("clientSessionID", "")).strip().lower()
        secure_session_id = str(params.get("clientSecureSessionID", "")).strip().lower()

        if not self._validate_session(user_id, session_id, secure_session_id):
            return self._fail("Session check failure, please re-login later!")

        bal = self.db.get_balance(user_id)
        return self._ok(clientBalance=int(bal))

    def GetTransaction(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("clientUUID", "")).strip().lower()
        session_id = str(params.get("clientSessionID", "")).strip().lower()
        secure_session_id = str(params.get("clientSecureSessionID", "")).strip().lower()
        txn_id = str(params.get("transactionID", "")).strip().lower()

        if not self._validate_session(user_id, session_id, secure_session_id):
            return self._fail("Session check failure, please re-login later!")
        if not txn_id:
            return self._fail("transactionID required")

        row = self.db.get_transaction(txn_id)
        if not row:
            return self._fail("transaction not found")
        return self._ok(
            sender=row["sender"],
            receiver=row["receiver"],
            amount=int(row["amount"]),
            type=int(row["type"]),
            description=str(row.get("description") or ""),
            status=int(row.get("status") or 0),
        )

    def _do_transfer(
        self,
        *,
        sender: str,
        receiver: str,
        amount: int,
        txn_type: int,
        object_id: str,
        object_name: str,
        region_handle: str,
        region_uuid: str,
        description: str,
    ) -> Dict[str, Any]:
        ok, msg, tx = self.db.transfer(
            sender=sender,
            receiver=receiver,
            amount=amount,
            txn_type=txn_type,
            object_uuid=object_id,
            object_name=object_name,
            region_handle=region_handle,
            region_uuid=region_uuid,
            description=description,
        )
        if not ok or not tx:
            return self._fail(msg)

        # Object payment callback for give-item flow.
        if txn_type == TXN_PAY_OBJECT and sender != UUID_ZERO:
            endpoint = self._resolve_sim_url(sender)
            with self._session_lock:
                rec = self._sessions.get(sender)
            if rec:
                cb_session_id = rec.session_id
                cb_secure_session_id = rec.secure_session_id
            else:
                session_pair = self.db.get_presence_session(sender)
                if session_pair:
                    cb_session_id, cb_secure_session_id = session_pair
                else:
                    cb_session_id = UUID_ZERO
                    cb_secure_session_id = UUID_ZERO
            payload = {
                "clientUUID": sender,
                "receiverUUID": receiver,
                "clientSessionID": cb_session_id,
                "clientSecureSessionID": cb_secure_session_id,
                "transactionType": txn_type,
                "amount": amount,
                "objectID": object_id,
                "objectName": object_name,
                "regionHandle": str(region_handle or "0"),
            }
            callback = self._xmlrpc_call(endpoint or "", "OnMoneyTransfered", payload)
            if not callback or not bool(callback.get("success")):
                self.db.rollback_transfer(tx, "Buyer failed to get object, rollback")
                return self._fail("buyer failed to get object, rollback")

        # Balance callbacks
        sender_name = self.db.get_user_name(sender)
        receiver_name = self.db.get_user_name(receiver)

        sender_msg, receiver_msg = self._build_balance_messages(
            txn_type=txn_type,
            amount=amount,
            sender_name=sender_name,
            receiver_name=receiver_name,
            object_name=object_name,
        )

        rules = self._get_notification_rules()
        suppress = rules.should_suppress(
            txn_type=txn_type,
            sender_uuid=sender,
            receiver_uuid=receiver,
            sender_name=sender_name,
            receiver_name=receiver_name,
            object_name=object_name,
        )
        if suppress:
            sender_msg = ""
            receiver_msg = ""

        # Always push balance updates, but only show dialog popup when message is non-empty
        # (region BalanceUpdate handler already does that check).
        if sender != UUID_ZERO and sender != receiver:
            self._update_balance_callback(sender, sender_msg)
        if receiver != UUID_ZERO:
            self._update_balance_callback(receiver, receiver_msg)

        return self._ok(transactionUUID=tx["transaction_uuid"])

    def TransferMoney(self, params: Dict[str, Any]) -> Dict[str, Any]:
        sender = str(params.get("senderID", "")).strip().lower()
        receiver = str(params.get("receiverID", "")).strip().lower()
        session_id = str(params.get("senderSessionID", "")).strip().lower()
        secure_session_id = str(params.get("senderSecureSessionID", "")).strip().lower()

        if not self._validate_session(sender, session_id, secure_session_id):
            return self._fail("Session check failure, please re-login later!")

        return self._do_transfer(
            sender=sender,
            receiver=receiver,
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", 0), 0),
            object_id=str(params.get("objectID", UUID_ZERO)),
            object_name=str(params.get("objectName", "")),
            region_handle=str(params.get("regionHandle", "0")),
            region_uuid=str(params.get("regionUUID", UUID_ZERO)),
            description=str(params.get("description", "TransferMoney")),
        )

    def ForceTransferMoney(self, params: Dict[str, Any]) -> Dict[str, Any]:
        if not self.cfg.enable_force_transfer:
            return self._fail("force transfer disabled")
        return self._do_transfer(
            sender=str(params.get("senderID", UUID_ZERO)).strip().lower(),
            receiver=str(params.get("receiverID", UUID_ZERO)).strip().lower(),
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", 0), 0),
            object_id=str(params.get("objectID", UUID_ZERO)),
            object_name=str(params.get("objectName", "")),
            region_handle=str(params.get("regionHandle", "0")),
            region_uuid=str(params.get("regionUUID", UUID_ZERO)),
            description=str(params.get("description", "ForceTransferMoney")),
        )

    def PayMoneyCharge(self, params: Dict[str, Any]) -> Dict[str, Any]:
        sender = str(params.get("senderID", "")).strip().lower()
        session_id = str(params.get("senderSessionID", "")).strip().lower()
        secure_session_id = str(params.get("senderSecureSessionID", "")).strip().lower()
        if not self._validate_session(sender, session_id, secure_session_id):
            return self._fail("Session check failure, please re-login later!")

        receiver = str(params.get("receiverID", UUID_ZERO)).strip().lower() or UUID_ZERO
        return self._do_transfer(
            sender=sender,
            receiver=receiver,
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", 1101), 1101),
            object_id=str(params.get("objectID", UUID_ZERO)),
            object_name=str(params.get("objectName", "")),
            region_handle=str(params.get("regionHandle", "0")),
            region_uuid=str(params.get("regionUUID", UUID_ZERO)),
            description=str(params.get("description", "PayMoneyCharge")),
        )

    def AddBankerMoney(self, params: Dict[str, Any]) -> Dict[str, Any]:
        banker_id = str(params.get("bankerID", "")).strip().lower()
        if not banker_id:
            return self._fail("bankerID required", banker=False)

        if self.cfg.banker_avatar != UUID_ZERO and banker_id != self.cfg.banker_avatar.lower():
            return self._fail("not allowed add money to avatar", banker=False)

        return self._do_transfer(
            sender=UUID_ZERO,
            receiver=banker_id,
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", TXN_BUY_MONEY), TXN_BUY_MONEY),
            object_id=UUID_ZERO,
            object_name="",
            region_handle=str(params.get("regionHandle", "0")),
            region_uuid=str(params.get("regionUUID", UUID_ZERO)),
            description=str(params.get("description", "AddBankerMoney")),
        )

    def _validate_script_secret(self, supplied_secret: str) -> bool:
        if not self.cfg.enable_script_send_money or not self.cfg.money_script_access_key:
            return False
        if self.cfg.trust_script_requests_without_remote_ip:
            return bool(supplied_secret)
        return False

    def SendMoney(self, params: Dict[str, Any]) -> Dict[str, Any]:
        if not self._validate_script_secret(str(params.get("secretAccessCode", ""))):
            return self._fail("script money disabled or invalid key")
        return self._do_transfer(
            sender=UUID_ZERO,
            receiver=str(params.get("receiverID", UUID_ZERO)).strip().lower(),
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", 5012), 5012),
            object_id=UUID_ZERO,
            object_name="",
            region_handle="0",
            region_uuid=UUID_ZERO,
            description=str(params.get("description", "SendMoney")),
        )

    def MoveMoney(self, params: Dict[str, Any]) -> Dict[str, Any]:
        if not self._validate_script_secret(str(params.get("secretAccessCode", ""))):
            return self._fail("script money disabled or invalid key")
        return self._do_transfer(
            sender=str(params.get("senderID", UUID_ZERO)).strip().lower(),
            receiver=str(params.get("receiverID", UUID_ZERO)).strip().lower(),
            amount=as_int(params.get("amount", 0), 0),
            txn_type=as_int(params.get("transactionType", 5011), 5011),
            object_id=UUID_ZERO,
            object_name="",
            region_handle="0",
            region_uuid=UUID_ZERO,
            description=str(params.get("description", "MoveMoney")),
        )

    def CancelTransfer(self, params: Dict[str, Any]) -> Dict[str, Any]:
        txn_id = str(params.get("transactionID", "")).strip().lower()
        if not txn_id:
            return self._fail("transactionID required")
        with self.db.conn() as c, c.cursor() as cur:
            cur.execute("UPDATE transactions SET status=%s, description=%s WHERE UUID=%s", (TXN_STATUS_FAILED, "Cancelled", txn_id))
        return self._ok()

    # ---------- helper rpc ----------
    def getCurrencyQuote(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("agentId", "")).strip().lower()
        secure_session_id = str(params.get("secureSessionId", "")).strip().lower()
        amount = as_int(params.get("currencyBuy", 0), 0)
        if amount <= 0:
            return self._fail("currencyBuy must be > 0", errorMessage="currencyBuy must be > 0", errorURI="")
        if not self._validate_session(user_id, UUID_ZERO, secure_session_id):
            return self._fail("Session check failure", errorMessage="Session check failure", errorURI="")

        estimated = max(0, amount // max(1, self.cfg.buy_currency_cost_divisor))
        confirm = self._make_confirm(user_id, secure_session_id, amount, "buy_currency")
        return {
            "success": True,
            "currency": {"estimatedCost": estimated, "currencyBuy": amount},
            "confirm": confirm,
        }

    def buyCurrency(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("agentId", "")).strip().lower()
        secure_session_id = str(params.get("secureSessionId", "")).strip().lower()
        amount = as_int(params.get("currencyBuy", 0), 0)
        confirm = str(params.get("confirm", "")).strip()

        if amount <= 0:
            return self._fail("currencyBuy must be > 0", errorMessage="currencyBuy must be > 0", errorURI="")
        if not self._validate_session(user_id, UUID_ZERO, secure_session_id):
            return self._fail("Session check failure", errorMessage="Session check failure", errorURI="")
        if not self._consume_confirm(confirm, user_id, secure_session_id, amount, "buy_currency"):
            return self._fail("Missmatch Confirm Value!!", errorMessage="Missmatch Confirm Value!!", errorURI="")

        resp = self._do_transfer(
            sender=UUID_ZERO,
            receiver=user_id,
            amount=amount,
            txn_type=TXN_BUY_MONEY,
            object_id=UUID_ZERO,
            object_name="",
            region_handle="0",
            region_uuid=UUID_ZERO,
            description="Buy Currency",
        )
        if not resp.get("success"):
            return self._fail(
                "Unable to process the transaction. The gateway denied your charge.",
                errorMessage="Unable to process the transaction. The gateway denied your charge.",
                errorURI="",
            )
        return {"success": True}

    def preflightBuyLandPrep(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("agentId", "")).strip().lower()
        secure_session_id = str(params.get("secureSessionId", "")).strip().lower()
        if not self._validate_session(user_id, UUID_ZERO, secure_session_id):
            return self._fail("Session check failure", errorMessage="Session check failure", errorURI="")

        area = as_int(params.get("billableArea", 0), 0)
        confirm = self._make_confirm(user_id, secure_session_id, area, "buy_land")
        out = {
            "success": True,
            "currency": {"estimatedCost": 0},
            "membership": {"upgrade": False, "action": ""},
            "landuse": {"upgrade": False, "action": ""},
            "confirm": confirm,
        }
        out["landUse"] = out["landuse"]
        return out

    def buyLandPrep(self, params: Dict[str, Any]) -> Dict[str, Any]:
        user_id = str(params.get("agentId", "")).strip().lower()
        secure_session_id = str(params.get("secureSessionId", "")).strip().lower()
        area = as_int(params.get("billableArea", 0), 0)
        confirm = str(params.get("confirm", "")).strip()

        if not self._validate_session(user_id, UUID_ZERO, secure_session_id):
            return self._fail("Session check failure", errorMessage="Session check failure", errorURI="")
        if not self._consume_confirm(confirm, user_id, secure_session_id, area, "buy_land"):
            return self._fail("Missmatch Confirm Value!!", errorMessage="Missmatch Confirm Value!!", errorURI="")
        return {"success": True}


class ThreadedXMLRPCServer(socketserver.ThreadingMixIn, SimpleXMLRPCServer):
    daemon_threads = True
    allow_reuse_address = True


class RequestHandler(SimpleXMLRPCRequestHandler):
    rpc_paths = ("/", "/RPC2")


def build_server(cfg: MoneydConfig, service: MoneyService) -> ThreadedXMLRPCServer:
    srv = ThreadedXMLRPCServer((cfg.host, cfg.port), requestHandler=RequestHandler, allow_none=True, logRequests=False)
    srv.register_introspection_functions()
    srv.register_function(service.ClientLogin, "ClientLogin")
    srv.register_function(service.ClientLogout, "ClientLogout")
    srv.register_function(service.GetBalance, "GetBalance")
    srv.register_function(service.GetTransaction, "GetTransaction")
    srv.register_function(service.TransferMoney, "TransferMoney")
    srv.register_function(service.ForceTransferMoney, "ForceTransferMoney")
    srv.register_function(service.PayMoneyCharge, "PayMoneyCharge")
    srv.register_function(service.AddBankerMoney, "AddBankerMoney")
    srv.register_function(service.SendMoney, "SendMoney")
    srv.register_function(service.MoveMoney, "MoveMoney")
    srv.register_function(service.CancelTransfer, "CancelTransfer")

    srv.register_function(service.getCurrencyQuote, "getCurrencyQuote")
    srv.register_function(service.buyCurrency, "buyCurrency")
    srv.register_function(service.preflightBuyLandPrep, "preflightBuyLandPrep")
    srv.register_function(service.buyLandPrep, "buyLandPrep")
    return srv


def main() -> int:
    parser = argparse.ArgumentParser(description="Tasia MoneyD")
    parser.add_argument("--config", default="/home/marty/opensim/MoneyServer.ini", help="Path to MoneyServer.ini")
    parser.add_argument("--log-level", default="INFO", help="Log level")
    args = parser.parse_args()

    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )

    cfg = load_config(args.config)
    service = MoneyService(cfg, args.config)
    srv = build_server(cfg, service)

    stop_event = threading.Event()

    def _stop(*_):
        if not stop_event.is_set():
            LOG.info("shutdown requested")
            stop_event.set()
            service.shutdown()
            srv.shutdown()

    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)

    LOG.info("MoneyD listening on %s:%s", cfg.host, cfg.port)
    try:
        srv.serve_forever(poll_interval=0.5)
    finally:
        srv.server_close()
        LOG.info("MoneyD stopped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
