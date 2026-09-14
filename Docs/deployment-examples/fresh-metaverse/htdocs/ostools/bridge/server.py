from __future__ import annotations

import json
import logging
import sys
import xmlrpc.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, Tuple
from urllib.parse import urlparse

from .backends import BackendError, build_backend
from .config import BridgeConfig, load_config
from .confirm import make_confirm, verify_confirm
from .im_notify import IMNotifier

log = logging.getLogger(__name__)

SUPPORTED_METHODS = {
    "getCurrencyQuote": "currency",
    "buyCurrency": "currency",
    "preflightBuyLandPrep": "land",
    "buyLandPrep": "land",
}


def _xmlrpc_response(value: Any) -> bytes:
    return xmlrpc.client.dumps((value,), methodresponse=True, allow_none=True).encode("utf-8")


def _xmlrpc_fault(code: int, message: str) -> bytes:
    return xmlrpc.client.dumps(xmlrpc.client.Fault(code, message), allow_none=True).encode("utf-8")


def _success_or_error(resp: Dict[str, Any], *, default_error_url: str) -> Dict[str, Any]:
    success = bool(resp.get("success", False))
    if success:
        return resp
    return {
        "success": False,
        "errorMessage": str(resp.get("errorMessage", "Operation failed.")),
        "errorURI": str(resp.get("errorURI", default_error_url)),
    }


class RequestDispatcher:
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        self.backend = build_backend(config)
        self.notifier = IMNotifier(config.notifier, timeout=config.request_timeout)

    def _build_quote(self, request_data: Dict[str, Any], remote_ip: str, backend_response: Dict[str, Any]) -> Dict[str, Any]:
        amount = int(request_data.get("currencyBuy", 0) or 0)
        currency = dict(backend_response.get("currency", {}))
        currency.setdefault("estimatedCost", self.config.default_estimated_cost)
        currency.setdefault("currencyBuy", amount)
        response = {
            "success": True,
            "currency": currency,
            "confirm": make_confirm(self.config.helper_secret, "currency", str(request_data.get("agentId", "")), amount, remote_ip),
        }
        if "estimatedLocalCost" in backend_response:
            response["estimatedLocalCost"] = backend_response["estimatedLocalCost"]
        return response

    def _build_land_preflight(self, request_data: Dict[str, Any], remote_ip: str, backend_response: Dict[str, Any]) -> Dict[str, Any]:
        amount = int(request_data.get("currencyBuy", 0) or 0)
        currency = dict(backend_response.get("currency", {}))
        currency.setdefault("estimatedCost", self.config.default_estimated_cost)
        landuse = dict(backend_response.get("landuse") or backend_response.get("landUse") or {})
        landuse.setdefault("upgrade", False)
        landuse.setdefault("action", self.config.public_base_url)
        membership = dict(backend_response.get("membership") or {})
        membership.setdefault("upgrade", False)
        membership.setdefault("action", self.config.public_base_url)
        membership.setdefault("levels", [{"id": "00000000-0000-0000-0000-000000000000", "description": "default"}])
        response = {
            "success": True,
            "currency": currency,
            "membership": membership,
            "landuse": landuse,
            "confirm": make_confirm(self.config.helper_secret, "land", str(request_data.get("agentId", "")), amount, remote_ip),
        }
        if self.config.include_legacy_landUse_key:
            response["landUse"] = dict(landuse)
        return response

    def _verify(self, request_data: Dict[str, Any], remote_ip: str, purpose: str) -> Tuple[bool, Dict[str, Any]]:
        if not self.config.verify_confirm:
            return True, {}
        amount = int(request_data.get("currencyBuy", 0) or 0)
        token = str(request_data.get("confirm", ""))
        agent_id = str(request_data.get("agentId", ""))
        if verify_confirm(token, self.config.helper_secret, purpose, agent_id, amount, remote_ip):
            return True, {}
        return False, {
            "success": False,
            "errorMessage": "Confirm token mismatch or expired.",
            "errorURI": self.config.error_url,
        }

    def dispatch(self, method_name: str, request_data: Dict[str, Any], remote_ip: str) -> Dict[str, Any]:
        if method_name == "getCurrencyQuote":
            backend_response = self.backend.quote_currency(request_data, remote_ip)
            backend_response = _success_or_error(backend_response, default_error_url=self.config.error_url)
            if not backend_response.get("success"):
                return backend_response
            return self._build_quote(request_data, remote_ip, backend_response)

        if method_name == "buyCurrency":
            ok, err = self._verify(request_data, remote_ip, "currency")
            if not ok:
                return err
            backend_response = _success_or_error(self.backend.buy_currency(request_data, remote_ip), default_error_url=self.config.buy_redirect_url)
            if backend_response.get("success"):
                to_agent_id = str(request_data.get("agentId", ""))
                amount = int(request_data.get("currencyBuy", 0) or 0)
                self.notifier.send(to_agent_id, f"Currency purchase approved for {amount} units.")
            return backend_response

        if method_name == "preflightBuyLandPrep":
            backend_response = self.backend.preflight_buy_land(request_data, remote_ip)
            backend_response = _success_or_error(backend_response, default_error_url=self.config.error_url)
            if not backend_response.get("success"):
                return backend_response
            return self._build_land_preflight(request_data, remote_ip, backend_response)

        if method_name == "buyLandPrep":
            ok, err = self._verify(request_data, remote_ip, "land")
            if not ok:
                return err
            backend_response = _success_or_error(self.backend.buy_land(request_data, remote_ip), default_error_url=self.config.error_url)
            if backend_response.get("success"):
                agent_id = str(request_data.get("agentId", ""))
                parcel_price = request_data.get("parcelPrice", request_data.get("currencyBuy", 0))
                parcel_name = request_data.get("parcelName", "parcel")
                self.notifier.send(agent_id, f"Land purchase helper approved for {parcel_name} ({parcel_price}).")
            return backend_response

        return {
            "success": False,
            "errorMessage": f"Unsupported method: {method_name}",
            "errorURI": self.config.error_url,
        }


class Handler(BaseHTTPRequestHandler):
    dispatcher: RequestDispatcher
    config: BridgeConfig

    server_version = "OpenSimHelperBridge/1.0"

    def _json(self, status: int, payload: Dict[str, Any]) -> None:
        body = json.dumps(payload, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _xml(self, status: int, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/xml; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        if path in {"/healthz", "/status"}:
            self._json(200, {
                "ok": True,
                "mode": self.config.mode,
                "public_base_url": self.config.public_base_url,
                "currency_path": "/currency.php",
                "land_path": "/landtool.php",
            })
            return
        self._json(404, {"ok": False, "error": "Use POST /currency.php or POST /landtool.php"})

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        content_length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(content_length)
        remote_ip = self.headers.get("X-Forwarded-For", self.client_address[0]).split(",")[0].strip()

        if path not in {"/currency.php", "/landtool.php", "/RPC2"}:
            self._xml(404, _xmlrpc_fault(404, f"Unsupported path: {path}"))
            return

        try:
            params, method_name = xmlrpc.client.loads(body, use_builtin_types=True)
        except Exception as exc:
            self._xml(400, _xmlrpc_fault(400, f"Invalid XML-RPC body: {exc}"))
            return

        if method_name not in SUPPORTED_METHODS:
            self._xml(400, _xmlrpc_fault(400, f"Unsupported XML-RPC method: {method_name}"))
            return

        expected_kind = SUPPORTED_METHODS[method_name]
        if path == "/currency.php" and expected_kind != "currency":
            self._xml(400, _xmlrpc_fault(400, f"{method_name} should use /landtool.php"))
            return
        if path == "/landtool.php" and expected_kind != "land":
            self._xml(400, _xmlrpc_fault(400, f"{method_name} should use /currency.php"))
            return

        request_data = params[0] if params and isinstance(params[0], dict) else {}
        try:
            response_data = self.dispatcher.dispatch(method_name, request_data, remote_ip)
            self._xml(200, _xmlrpc_response(response_data))
        except BackendError as exc:
            log.exception("Backend error")
            response = {
                "success": False,
                "errorMessage": str(exc),
                "errorURI": self.config.error_url,
            }
            self._xml(200, _xmlrpc_response(response))
        except Exception as exc:  # pragma: no cover - last-resort guard
            log.exception("Unhandled server error")
            self._xml(500, _xmlrpc_fault(500, f"Unhandled server error: {exc}"))

    def log_message(self, fmt: str, *args: Any) -> None:
        log.info("%s - %s", self.address_string(), fmt % args)


def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s - %(message)s",
        stream=sys.stdout,
    )
    config = load_config()
    handler_cls = type("ConfiguredHandler", (Handler,), {})
    handler_cls.dispatcher = RequestDispatcher(config)
    handler_cls.config = config
    server = ThreadingHTTPServer((config.host, config.port), handler_cls)
    log.info("OpenSim helper bridge listening on %s:%s in %s mode", config.host, config.port, config.mode)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        log.info("Shutting down")
    finally:
        server.server_close()
