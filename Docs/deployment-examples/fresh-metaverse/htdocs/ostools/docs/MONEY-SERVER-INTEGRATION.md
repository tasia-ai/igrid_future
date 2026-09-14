# Money server integration guide

## Helper methods the bridge exposes

- `getCurrencyQuote`
- `buyCurrency`
- `preflightBuyLandPrep`
- `buyLandPrep`

## XML-RPC passthrough mode

Use this when your money server already handles helper messages.

```json
{
  "mode": "xmlrpc_passthrough",
  "upstream_xmlrpc_url": "http://127.0.0.1:1026/"
}
```

## REST mode

Use this when your money server only offers HTTP JSON.

```json
{
  "mode": "rest_json",
  "rest_quote_url": "http://127.0.0.1:1026/api/quote",
  "rest_buy_url": "http://127.0.0.1:1026/api/buy",
  "rest_preflight_land_url": "http://127.0.0.1:1026/api/preflight-land",
  "rest_buy_land_url": "http://127.0.0.1:1026/api/buy-land"
}
```

See `examples/rest-backend-payloads.md` for request/response shapes.

## Custom mode

Use this when your money server is not tidy.

```json
{
  "mode": "custom"
}
```

Then edit `bridge/custom_backend.py`.

## Secure session validation

The viewer request includes `agentId` and `secureSessionId`. Some legacy helper scripts validate those against grid data before proceeding. This starter pack does not assume a specific validation API because your backend is unknown.

You can add validation in one of two places:

1. inside your money server
2. inside `bridge/custom_backend.py`

## Land-sale flow reality check

`buyLandPrep` is only the helper side of the flow. The final region-side land purchase still depends on your money module and parcel sale path working correctly.

So a successful helper response alone does not magically transfer land if the region money module rejects the sale later.
