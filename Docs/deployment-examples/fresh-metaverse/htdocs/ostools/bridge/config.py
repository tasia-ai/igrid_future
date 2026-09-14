from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from typing import Any, Dict, Optional


@dataclass
class IMConfig:
    enabled: bool = False
    base_url: str = "https://os.tasia.work.gd"
    token_path: str = "/wowonder/oauth/token"
    send_path: str = "/api/v1/im/send"
    client_id: str = ""
    client_secret: str = ""
    username: str = ""
    password: str = ""
    scope: str = "im.write"
    from_agent_id: str = ""
    region_id: str = "00000000-0000-0000-0000-000000000000"
    offline: bool = True


@dataclass
class BridgeConfig:
    host: str = "127.0.0.1"
    port: int = 8015
    public_base_url: str = "https://os.tasia.work.gd/ostools/"
    mode: str = "xmlrpc_passthrough"  # xmlrpc_passthrough | rest_json | noop | custom
    upstream_xmlrpc_url: str = "http://127.0.0.1:1026/"
    request_timeout: float = 15.0
    helper_secret: str = "CHANGE_ME_LONG_RANDOM_SECRET"
    verify_confirm: bool = True
    include_legacy_landUse_key: bool = True
    default_estimated_cost: int = 0
    currency_ratio: float = 0.0
    allow_noop_buy_currency: bool = False
    allow_noop_land_buy: bool = False
    buy_redirect_url: str = "https://os.tasia.work.gd/tokens"
    error_url: str = "https://os.tasia.work.gd/"
    rest_quote_url: str = ""
    rest_buy_url: str = ""
    rest_preflight_land_url: str = ""
    rest_buy_land_url: str = ""
    rest_balance_url: str = ""
    rest_method: str = "POST"
    rest_headers: Dict[str, str] = field(default_factory=dict)
    rest_auth_bearer: str = ""
    notifier: IMConfig = field(default_factory=IMConfig)

    @staticmethod
    def from_mapping(data: Dict[str, Any]) -> "BridgeConfig":
        notifier_data = data.get("notifier", {}) or {}
        merged = dict(data)
        merged["notifier"] = IMConfig(**notifier_data)
        return BridgeConfig(**merged)


def _coerce_bool(value: str) -> bool:
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


def load_config() -> BridgeConfig:
    config_path = os.environ.get("OS_HELPER_CONFIG", "")
    if config_path:
        with open(config_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        return BridgeConfig.from_mapping(data)

    cfg = BridgeConfig()
    cfg.host = os.environ.get("OS_HELPER_HOST", cfg.host)
    cfg.port = int(os.environ.get("OS_HELPER_PORT", cfg.port))
    cfg.public_base_url = os.environ.get("OS_HELPER_PUBLIC_BASE_URL", cfg.public_base_url)
    cfg.mode = os.environ.get("OS_HELPER_MODE", cfg.mode)
    cfg.upstream_xmlrpc_url = os.environ.get("OS_HELPER_UPSTREAM_XMLRPC_URL", cfg.upstream_xmlrpc_url)
    cfg.request_timeout = float(os.environ.get("OS_HELPER_REQUEST_TIMEOUT", cfg.request_timeout))
    cfg.helper_secret = os.environ.get("OS_HELPER_SECRET", cfg.helper_secret)
    cfg.verify_confirm = _coerce_bool(os.environ.get("OS_HELPER_VERIFY_CONFIRM", cfg.verify_confirm))
    cfg.include_legacy_landUse_key = _coerce_bool(os.environ.get("OS_HELPER_INCLUDE_LEGACY_LANDUSE", cfg.include_legacy_landUse_key))
    cfg.default_estimated_cost = int(os.environ.get("OS_HELPER_DEFAULT_ESTIMATED_COST", cfg.default_estimated_cost))
    cfg.currency_ratio = float(os.environ.get("OS_HELPER_CURRENCY_RATIO", cfg.currency_ratio))
    cfg.allow_noop_buy_currency = _coerce_bool(os.environ.get("OS_HELPER_ALLOW_NOOP_BUY_CURRENCY", cfg.allow_noop_buy_currency))
    cfg.allow_noop_land_buy = _coerce_bool(os.environ.get("OS_HELPER_ALLOW_NOOP_LAND_BUY", cfg.allow_noop_land_buy))
    cfg.buy_redirect_url = os.environ.get("OS_HELPER_BUY_REDIRECT_URL", cfg.buy_redirect_url)
    cfg.error_url = os.environ.get("OS_HELPER_ERROR_URL", cfg.error_url)
    cfg.rest_quote_url = os.environ.get("OS_HELPER_REST_QUOTE_URL", cfg.rest_quote_url)
    cfg.rest_buy_url = os.environ.get("OS_HELPER_REST_BUY_URL", cfg.rest_buy_url)
    cfg.rest_preflight_land_url = os.environ.get("OS_HELPER_REST_PREFLIGHT_LAND_URL", cfg.rest_preflight_land_url)
    cfg.rest_buy_land_url = os.environ.get("OS_HELPER_REST_BUY_LAND_URL", cfg.rest_buy_land_url)
    cfg.rest_balance_url = os.environ.get("OS_HELPER_REST_BALANCE_URL", cfg.rest_balance_url)
    cfg.rest_method = os.environ.get("OS_HELPER_REST_METHOD", cfg.rest_method)
    if hdrs := os.environ.get("OS_HELPER_REST_HEADERS"):
        cfg.rest_headers = json.loads(hdrs)
    cfg.rest_auth_bearer = os.environ.get("OS_HELPER_REST_AUTH_BEARER", cfg.rest_auth_bearer)

    cfg.notifier.enabled = _coerce_bool(os.environ.get("OS_HELPER_IM_ENABLED", cfg.notifier.enabled))
    cfg.notifier.base_url = os.environ.get("OS_HELPER_IM_BASE_URL", cfg.notifier.base_url)
    cfg.notifier.token_path = os.environ.get("OS_HELPER_IM_TOKEN_PATH", cfg.notifier.token_path)
    cfg.notifier.send_path = os.environ.get("OS_HELPER_IM_SEND_PATH", cfg.notifier.send_path)
    cfg.notifier.client_id = os.environ.get("OS_HELPER_IM_CLIENT_ID", cfg.notifier.client_id)
    cfg.notifier.client_secret = os.environ.get("OS_HELPER_IM_CLIENT_SECRET", cfg.notifier.client_secret)
    cfg.notifier.username = os.environ.get("OS_HELPER_IM_USERNAME", cfg.notifier.username)
    cfg.notifier.password = os.environ.get("OS_HELPER_IM_PASSWORD", cfg.notifier.password)
    cfg.notifier.scope = os.environ.get("OS_HELPER_IM_SCOPE", cfg.notifier.scope)
    cfg.notifier.from_agent_id = os.environ.get("OS_HELPER_IM_FROM_AGENT_ID", cfg.notifier.from_agent_id)
    cfg.notifier.region_id = os.environ.get("OS_HELPER_IM_REGION_ID", cfg.notifier.region_id)
    cfg.notifier.offline = _coerce_bool(os.environ.get("OS_HELPER_IM_OFFLINE", cfg.notifier.offline))
    return cfg
