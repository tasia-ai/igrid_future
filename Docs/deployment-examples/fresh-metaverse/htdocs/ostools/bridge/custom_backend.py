from __future__ import annotations

import pathlib
import xmlrpc.client
from typing import Any, Dict

from .config import BridgeConfig


class CustomMoneyBackend:
    """
    Fill these methods with calls to your real money server on port 1026.

    Expected return shape:
      quote_currency -> {"success": True, "currency": {"estimatedCost": 10, "currencyBuy": 1000}}
      buy_currency   -> {"success": True} or {"success": False, "errorMessage": "...", "errorURI": "https://..."}
      preflight_buy_land -> {"success": True, "currency": {"estimatedCost": 5}}
      buy_land -> {"success": True}
    """

    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        self.currency_proxy, self.land_proxy = self._build_legacy_proxies()

    def _build_legacy_proxies(self) -> tuple[xmlrpc.client.ServerProxy, xmlrpc.client.ServerProxy]:
        web_root = pathlib.Path("/var/www/html/web")
        backups = sorted(
            [p for p in web_root.glob("ostools_backup_*") if p.is_dir()],
            key=lambda p: p.name,
            reverse=True,
        )
        if not backups:
            raise RuntimeError("No legacy ostools backup folder found for custom backend")

        legacy = backups[0]
        currency_url = f"http://127.0.0.1/{legacy.name}/currency.php"
        land_url = f"http://127.0.0.1/{legacy.name}/landtool.php"
        return (
            xmlrpc.client.ServerProxy(currency_url, allow_none=True, use_builtin_types=True),
            xmlrpc.client.ServerProxy(land_url, allow_none=True, use_builtin_types=True),
        )

    @staticmethod
    def _normalize(response: Any) -> Dict[str, Any]:
        if isinstance(response, dict):
            return response
        return {"success": True, "raw": response}

    def _inject_legacy_currency_confirm(self, request_data: Dict[str, Any]) -> Dict[str, Any]:
        payload = dict(request_data)
        try:
            quote = self._normalize(self.currency_proxy.getCurrencyQuote(payload))
            confirm = quote.get("confirm")
            if confirm is not None:
                payload["confirm"] = confirm
        except Exception:
            pass
        return payload

    def _inject_legacy_land_confirm(self, request_data: Dict[str, Any]) -> Dict[str, Any]:
        payload = dict(request_data)
        try:
            preflight = self._normalize(self.land_proxy.preflightBuyLandPrep(payload))
            confirm = preflight.get("confirm")
            if confirm is not None:
                payload["confirm"] = confirm
        except Exception:
            pass
        return payload

    def quote_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        try:
            return self._normalize(self.currency_proxy.getCurrencyQuote(request_data))
        except Exception as exc:
            return {
                "success": False,
                "errorMessage": f"legacy currency quote failed: {exc}",
                "errorURI": self.config.error_url,
            }

    def buy_currency(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        try:
            payload = self._inject_legacy_currency_confirm(request_data)
            return self._normalize(self.currency_proxy.buyCurrency(payload))
        except Exception as exc:
            return {
                "success": False,
                "errorMessage": f"legacy currency buy failed: {exc}",
                "errorURI": self.config.buy_redirect_url,
            }

    def preflight_buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        try:
            return self._normalize(self.land_proxy.preflightBuyLandPrep(request_data))
        except Exception as exc:
            return {
                "success": False,
                "errorMessage": f"legacy land preflight failed: {exc}",
                "errorURI": self.config.error_url,
            }

    def buy_land(self, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        try:
            payload = self._inject_legacy_land_confirm(request_data)
            return self._normalize(self.land_proxy.buyLandPrep(payload))
        except Exception as exc:
            return {
                "success": False,
                "errorMessage": f"legacy land buy failed: {exc}",
                "errorURI": self.config.error_url,
            }
