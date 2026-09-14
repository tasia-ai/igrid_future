# REST backend payload shapes

When `mode = rest_json`, the bridge sends JSON like this to your money server:

## getCurrencyQuote

```json
{
  "method": "getCurrencyQuote",
  "remote_ip": "203.0.113.10",
  "agentId": "uuid",
  "secureSessionId": "uuid",
  "currencyBuy": 1000
}
```

Expected response:

```json
{
  "success": true,
  "currency": {
    "estimatedCost": 5,
    "currencyBuy": 1000
  }
}
```

## buyCurrency

Request is the same body plus `confirm` and maybe `estimatedCost`.

Expected success response:

```json
{
  "success": true
}
```

Expected failure response:

```json
{
  "success": false,
  "errorMessage": "Card declined",
  "errorURI": "https://os.tasia.work.gd/tokens"
}
```

## preflightBuyLandPrep

```json
{
  "method": "preflightBuyLandPrep",
  "remote_ip": "203.0.113.10",
  "agentId": "uuid",
  "secureSessionId": "uuid",
  "currencyBuy": 250,
  "billableArea": 512,
  "parcelName": "Shop Lot"
}
```

Expected response:

```json
{
  "success": true,
  "currency": {
    "estimatedCost": 2
  }
}
```

## buyLandPrep

```json
{
  "method": "buyLandPrep",
  "remote_ip": "203.0.113.10",
  "agentId": "uuid",
  "secureSessionId": "uuid",
  "currencyBuy": 250,
  "estimatedCost": 2,
  "confirm": "..."
}
```

Expected response:

```json
{
  "success": true
}
```
