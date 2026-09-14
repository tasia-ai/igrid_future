#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import sys
import threading
import time
import urllib.request
import xmlrpc.client
from http.server import ThreadingHTTPServer

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from bridge.server import Handler, RequestDispatcher
from bridge.config import BridgeConfig


def main() -> None:
    config = BridgeConfig(
        host="127.0.0.1",
        port=18015,
        mode="noop",
        verify_confirm=False,
        default_estimated_cost=0,
        allow_noop_buy_currency=True,
        allow_noop_land_buy=True,
        public_base_url="https://os.tasia.work.gd/ostools/",
    )
    handler_cls = type("ConfiguredHandler", (Handler,), {})
    handler_cls.dispatcher = RequestDispatcher(config)
    handler_cls.config = config
    server = ThreadingHTTPServer((config.host, config.port), handler_cls)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    time.sleep(0.25)

    try:
        body = xmlrpc.client.dumps(({"agentId": "a", "currencyBuy": 1000},), methodname="getCurrencyQuote", allow_none=True)
        req = urllib.request.Request(f"http://{config.host}:{config.port}/currency.php", data=body.encode("utf-8"), method="POST")
        req.add_header("Content-Type", "text/xml")
        with urllib.request.urlopen(req, timeout=5) as resp:
            xml_body = resp.read()
        params, method_name = xmlrpc.client.loads(xml_body)
        payload = params[0]
        assert payload["success"] is True
        assert payload["currency"]["currencyBuy"] == 1000

        body = xmlrpc.client.dumps(({"agentId": "a", "currencyBuy": 0},), methodname="preflightBuyLandPrep", allow_none=True)
        req = urllib.request.Request(f"http://{config.host}:{config.port}/landtool.php", data=body.encode("utf-8"), method="POST")
        req.add_header("Content-Type", "text/xml")
        with urllib.request.urlopen(req, timeout=5) as resp:
            xml_body = resp.read()
        params, method_name = xmlrpc.client.loads(xml_body)
        payload = params[0]
        assert payload["success"] is True
        print("Smoke test passed")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
