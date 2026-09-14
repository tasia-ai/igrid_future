from __future__ import annotations

import base64
import hashlib
import hmac
import time
from typing import Optional


def _sign(secret: str, purpose: str, agent_id: str, amount: int, remote_ip: str, ts: int) -> str:
    message = f"{purpose}|{agent_id}|{amount}|{remote_ip}|{ts}".encode("utf-8")
    return hmac.new(secret.encode("utf-8"), message, hashlib.sha256).hexdigest()


def make_confirm(secret: str, purpose: str, agent_id: str, amount: int, remote_ip: str, ttl_seconds: int = 900) -> str:
    ts = int(time.time())
    signature = _sign(secret, purpose, agent_id, amount, remote_ip, ts)
    raw = f"{ts}:{ttl_seconds}:{signature}".encode("utf-8")
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def verify_confirm(token: str, secret: str, purpose: str, agent_id: str, amount: int, remote_ip: str) -> bool:
    try:
        padded = token + "=" * (-len(token) % 4)
        decoded = base64.urlsafe_b64decode(padded).decode("utf-8")
        ts_s, ttl_s, signature = decoded.split(":", 2)
        ts = int(ts_s)
        ttl = int(ttl_s)
    except Exception:
        return False

    if int(time.time()) > ts + ttl:
        return False

    expected = _sign(secret, purpose, agent_id, amount, remote_ip, ts)
    return hmac.compare_digest(signature, expected)
