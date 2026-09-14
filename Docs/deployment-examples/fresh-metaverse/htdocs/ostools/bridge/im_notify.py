from __future__ import annotations

import json
import logging
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, Optional

from .config import IMConfig

log = logging.getLogger(__name__)


class IMNotifier:
    def __init__(self, config: IMConfig, timeout: float = 15.0) -> None:
        self.config = config
        self.timeout = timeout

    def _post_form(self, url: str, data: Dict[str, Any], headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        body = urllib.parse.urlencode(data).encode("utf-8")
        request = urllib.request.Request(url, data=body, method="POST")
        for k, v in (headers or {}).items():
            request.add_header(k, v)
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            raw = response.read().decode("utf-8")
            return json.loads(raw) if raw else {}

    def _post_json(self, url: str, data: Dict[str, Any], headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        body = json.dumps(data).encode("utf-8")
        request = urllib.request.Request(url, data=body, method="POST")
        request.add_header("Content-Type", "application/json")
        for k, v in (headers or {}).items():
            request.add_header(k, v)
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            raw = response.read().decode("utf-8")
            return json.loads(raw) if raw else {}

    def send(self, to_agent_id: str, message: str, *, session_id: str = "00000000-0000-0000-0000-000000000000") -> None:
        if not self.config.enabled:
            return
        token_url = self.config.base_url.rstrip("/") + self.config.token_path
        send_url = self.config.base_url.rstrip("/") + self.config.send_path
        try:
            token_response = self._post_form(
                token_url,
                {
                    "grant_type": "password",
                    "client_id": self.config.client_id,
                    "client_secret": self.config.client_secret,
                    "username": self.config.username,
                    "password": self.config.password,
                    "scope": self.config.scope,
                },
            )
            access_token = token_response.get("access_token", "")
            from_agent_id = self.config.from_agent_id or token_response.get("user_id") or token_response.get("agent_id")
            if not access_token or not from_agent_id:
                log.warning("IM token response missing fields: %s", token_response)
                return

            payload = {
                "from_agent_id": from_agent_id,
                "to_agent_id": to_agent_id,
                "message": message,
                "dialog": 0,
                "session_id": session_id,
                "region_id": self.config.region_id,
                "position": {"x": 128.0, "y": 128.0, "z": 25.0},
                "offline": self.config.offline,
            }
            response = self._post_json(send_url, payload, {"Authorization": f"Bearer {access_token}"})
            log.info("IM send response: %s", response)
        except urllib.error.URLError as exc:
            log.warning("IM notification failed: %s", exc)
