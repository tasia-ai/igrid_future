from __future__ import annotations

import importlib
import json
import logging
import urllib.error
import urllib.parse
import urllib.request
import xmlrpc.client
from abc import ABC, abstractmethod
from typing import Any, Dict, Optional

from .config import BridgeConfig

log = logging.getLogger(__name__)


class BackendError(RuntimeError):
    pass


class MoneyBackend(ABC):
    @abstractmethod
    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        raise NotImplementedError

    @abstractmethod
    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        raise NotImplementedError

    @abstractmethod
    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        raise NotImplementedError

    @abstractmethod
    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        raise NotImplementedError


class XmlRpcPassthroughBackend(MoneyBackend):
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        transport = xmlrpc.client.Transport()
        transport.user_agent = "OpenSimHelperBridge/1.0"
        self.proxy = xmlrpc.client.ServerProxy(config.upstream_xmlrpc_url, allow_none=True, use_builtin_types=True, transport=transport)

    def _call(self, method_name: str, payload: Dict[str, Any]) -> Dict[str, Any]:
        try:
            method = getattr(self.proxy, method_name)
            response = method(payload)
            if isinstance(response, dict):
                return response
            return {"success": True, "raw": response}
        except Exception as exc:
            raise BackendError(f"upstream XML-RPC call failed for {method_name}: {exc}") from exc

    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._call("getCurrencyQuote", request_data)

    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._call("buyCurrency", request_data)

    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._call("preflightBuyLandPrep", request_data)

    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._call("buyLandPrep", request_data)


class RestJsonBackend(MoneyBackend):
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config

    def _headers(self) -> Dict[str, str]:
        headers = {"Content-Type": "application/json", **self.config.rest_headers}
        if self.config.rest_auth_bearer:
            headers["Authorization"] = f"Bearer {self.config.rest_auth_bearer}"
        return headers

    def _request(self, url: str, payload: Dict[str, Any]) -> Dict[str, Any]:
        if not url:
            raise BackendError("REST backend URL is empty")
        data = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=self.config.rest_method.upper())
        for key, value in self._headers().items():
            req.add_header(key, value)
        try:
            with urllib.request.urlopen(req, timeout=self.config.request_timeout) as response:
                body = response.read().decode("utf-8")
                if not body:
                    return {"success": True}
                return json.loads(body)
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise BackendError(f"REST backend error {exc.code}: {body}") from exc
        except urllib.error.URLError as exc:
            raise BackendError(f"REST backend connection failed: {exc}") from exc

    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._request(self.config.rest_quote_url, {"method": "getCurrencyQuote", "remote_ip": remote_ip, **request_data})

    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._request(self.config.rest_buy_url, {"method": "buyCurrency", "remote_ip": remote_ip, **request_data})

    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._request(self.config.rest_preflight_land_url, {"method": "preflightBuyLandPrep", "remote_ip": remote_ip, **request_data})

    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self._request(self.config.rest_buy_land_url, {"method": "buyLandPrep", "remote_ip": remote_ip, **request_data})


class NoopBackend(MoneyBackend):
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config

    def _estimated_cost(self, request_data: Dict[str, Any]) -> int:
        amount = int(request_data.get("currencyBuy", 0) or 0)
        if self.config.currency_ratio > 0:
            return int(round(amount * self.config.currency_ratio))
        return self.config.default_estimated_cost

    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        amount = int(request_data.get("currencyBuy", 0) or 0)
        return {
            "success": True,
            "currency": {
                "estimatedCost": self._estimated_cost(request_data),
                "currencyBuy": amount,
            },
        }

    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        if not self.config.allow_noop_buy_currency:
            return {
                "success": False,
                "errorMessage": "buyCurrency is disabled in noop mode until you wire a real backend.",
                "errorURI": self.config.buy_redirect_url,
            }
        return {"success": True}

    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return {
            "success": True,
            "currency": {"estimatedCost": self._estimated_cost(request_data)},
            "membership": {"upgrade": False, "action": self.config.public_base_url},
            "landuse": {"upgrade": False, "action": self.config.public_base_url},
        }

    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        if not self.config.allow_noop_land_buy:
            return {
                "success": False,
                "errorMessage": "buyLandPrep is disabled in noop mode until you wire a real backend.",
                "errorURI": self.config.error_url,
            }
        return {"success": True}


class CustomBackend(MoneyBackend):
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        module = importlib.import_module("bridge.custom_backend")
        self.impl = module.CustomMoneyBackend(config)

    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self.impl.quote_currency(request_data, remote_ip)

    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self.impl.buy_currency(request_data, remote_ip)

    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self.impl.preflight_buy_land(request_data, remote_ip)

    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        return self.impl.buy_land(request_data, remote_ip)



def build_backend(config: BridgeConfig) -> MoneyBackend:
    mode = config.mode.strip().lower()
    if mode == "xmlrpc_passthrough":
        return XmlRpcPassthroughBackend(config)
    if mode == "rest_json":
        return RestJsonBackend(config)
    if mode == "noop":
        return NoopBackend(config)
    if mode == "custom":
        return CustomBackend(config)
    raise ValueError(f"Unsupported backend mode: {config.mode}")
