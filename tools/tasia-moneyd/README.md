# Tasia MoneyD (Python XML-RPC money server)

Production-ready replacement for legacy NSL MoneyServer, with compatible XML-RPC method names used by OpenSim DTLNSLMoneyModule and viewer helper flows.

## Features

- XML-RPC methods for region money module:
  - `ClientLogin`, `ClientLogout`, `GetBalance`, `GetTransaction`
  - `TransferMoney`, `ForceTransferMoney`, `PayMoneyCharge`
  - `AddBankerMoney`, `SendMoney`, `MoveMoney`, `CancelTransfer`
- XML-RPC methods for viewer helper compatibility:
  - `getCurrencyQuote`, `buyCurrency`, `preflightBuyLandPrep`, `buyLandPrep`
- Session verification against robust `Presence`
- ACID transfers in MySQL (`money.balances`, `money.transactions`)
- Region callbacks:
  - `UpdateBalance`
  - `OnMoneyTransfered` (for `PayObject` flow)

## Install

```bash
python3 -m pip install -r /build/tasia_release/tools/tasia-moneyd/requirements.txt
```

## Run

Uses existing `MoneyServer.ini` directly.

```bash
python3 /build/tasia_release/tools/tasia-moneyd/tasia_moneyd.py --config /home/marty/opensim/MoneyServer.ini
```

Optional env overrides:

- `TASIA_MONEYD_HOST` (default: `0.0.0.0`)
- `TASIA_MONEYD_PORT` (default from INI `MoneyServer.ServerPort`)
- `TASIA_MONEYD_CONFIRM_SECRET` (HMAC secret for helper confirm tokens)

## Notification filter config (auto-reload)

MoneyD reads `[MoneyDNotifications]` from the same INI file and auto-reloads the rules when file timestamp changes.

Example:

```ini
[MoneyDNotifications]
Enabled = true
AutoReloadSeconds = 3
MuteAll = false

; Only these tx types will show popup messages (empty = all)
; OnlyTypes = 5001,5008,5009,5010

; Suppress popup messages for these types
; SuppressTypes = 5009

; Suppress by object name substring
; SuppressObjectNameContains = Vendor,Tip Jar

; Suppress by object name regex
; SuppressObjectNameRegex = ^\[AV\].*,^Clubmaster .*

; Suppress when sender == receiver
SuppressWhenSenderEqualsReceiver = false
```

Notes:

- Filtering affects popup text messages, not balance value updates.
- `Allow*` rules override suppress rules.

## Cutover checklist

1. Start on alternate port first (e.g. 1027).
2. Validate all critical methods with test avatars:
   - balance, avatar transfer, pay object, buy currency, land preflight
3. Switch `CurrencyServer` to MoneyD endpoint.
4. Keep old server available for rollback during first verification window.
