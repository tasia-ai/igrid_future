# WoWonder Integration Proposal

## Background
WoWonder ships with a REST-style API and mobile clients that authenticate via short-lived access tokens before posting chat, wallet, and profile updates to the platform.[^wowonder-dev][^wowonder-messenger]
To let the OpenSimulator grid interoperate with WoWonder-based community portals, we need HTTP endpoints inside the Robust service stack that speak modern JSON over HTTPS and expose the specific data WoWonder expects (OAuth 2.0 tokens, instant message payloads, and wallet balances).

[^wowonder-dev]: https://www.wowonder.com/developers
[^wowonder-messenger]: https://raw.githubusercontent.com/ScriptSun/Xamarin_Forms_Wowonder-Messenger-v1.3/master/README.md

## Goals
- Deliver a JSON-based `send_im` endpoint under Robust so web properties can deliver instant messages to grid residents without XML-RPC dependencies.
- Provide OAuth 2.0 compliant authorize/token/userinfo endpoints that issue Bearer tokens for WoWonder add-ons and other third-party sites.
- Offer a secure money balance API so WoWonder-driven shops can display and reconcile avatar wallet balances.
- Supply a PHP add-on skeleton that demonstrates how a WoWonder module exchanges tokens and syncs balances with the new Robust APIs.

## Architectural Overview
The work extends Robust with three HTTP handler bundles and introduces a new shared OAuth/personal access token store:

```
+--------------------+     +-------------------+
| WoWonder Add-on    |<--->| Robust OAuth REST |
| (PHP)              |     +-------------------+
|                    |             |
|  OAuth 2.0         |             v
|  Send IM JSON      |     +-------------------+
|  Wallet Sync       |<--->| Robust Profile &  |
+--------------------+     | Money Services    |
                           +-------------------+
```

> **Implementation note:** The production code for these endpoints now ships as
> the `TasiaAddon.WoWonder` add-on project inside `addon-modules/`. Keeping
> the handlers in an add-on assembly makes it easier to re-apply the feature
> when rebasing onto future upstream releases. The legacy `/send` marketplace
> delivery endpoint lives alongside a toggleable startup plug-in in
> `TasiaAddon.Marketplace`.

### Data Storage
Create three new tables managed through `OpenSim.Data` connectors and migrations:

1. `oauth_clients`: `id` (PK), `secret` (hashed), `name`, `redirect_uris` (JSON array), `scopes`, `created`.
2. `oauth_codes`: `code`, `client_id`, `agent_id`, `expires`, `redirect_uri`, `scopes`, `code_challenge` (for PKCE), `created`.
3. `oauth_tokens`: `token_id` (UUID), `client_id`, `agent_id`, `scope`, `issued`, `expires`, `refresh_token`, `refresh_expires`, `revoked`.

Use the existing pattern from `OpenSim/Data/MySQL/*`/`SQLite`/`PGSQL` for migrations and connectors so all supported databases stay in sync. Tokens should be stored using SHA-256 hashed refresh tokens and AES-encrypted access token payloads (leveraging `OpenSim.Framework.Util` AES helpers) to limit disclosure if the database leaks.

## Robust OAuth Service

### Endpoints
| Verb | Path | Purpose |
| ---- | ---- | ------- |
| `GET` | `/oauth/authorize` | Interactive login + consent screen for human users. Supports `response_type=code`, `client_id`, `redirect_uri`, `scope`, `state`, and PKCE (`code_challenge`/`code_challenge_method`). |
| `POST` | `/oauth/token` | Exchanges authorization codes or refresh tokens for short-lived (e.g., 10 minute) Bearer tokens and long-lived refresh tokens. |
| `GET` | `/oauth/userinfo` | Returns avatar UUID, username, primary email, and granted scopes for display on WoWonder profiles. Requires Bearer token. |
| `POST` | `/oauth/introspect` | Optional endpoint for server-to-server token validation. |

### Implementation Notes
- Add a new `OpenSim.Server.Handlers.Authentication.OAuthServerConnector` that registers routes through `BaseHttpServer.AddHTTPHandler`. Reuse `PasswordAuthenticationService` to verify credentials when presenting the authorize form.
- Introduce `IOAuthStore` in `OpenSim/Framework` and provide MySQL/PGSQL/SQLite implementations parallel to other connectors.
- Implement PKCE and state verification per RFC 7636 and RFC 6749 to prevent interception.
- Expose configuration inside `Robust.HG.ini` and `Robust.ini`:

```ini
[OAuth]
    Enabled = true
    AccessTokenLifetime = 00:10:00
    RefreshTokenLifetime = 7.00:00:00
    AllowedOrigins = https://your-wowonder-site.example
```

- Hook the OAuth module into `Robust` by updating `OpenSim/Server/Robust.csproj` and `Robust.ini.example` with the new handler assembly.
- Use TLS termination in the front-end proxy (nginx/Apache) and require `X-Forwarded-Proto=https` before issuing tokens.

## Robust JSON `send_im` Endpoint

### Endpoint Shape
- **Path:** `POST /api/v1/im/send`
- **Authentication:** OAuth Bearer token with `im.write` scope (the legacy `im` scope continues to work for compatibility).
- **Request Body:**
  ```json
  {
    "from_agent_id": "${uuid}",
    "to_agent_id": "${uuid}",
    "message": "string",
    "dialog": 0,
    "session_id": "${uuid}",
    "position": { "x": 128.0, "y": 128.0, "z": 25.5 },
    "region_id": "${uuid}",
    "binary_bucket": "base64",
    "offline": false
  }
  ```
- **Response:**
  ```json
  { "delivered": true, "offline": false, "timestamp": 1713301923 }
  ```

`TasiaAddon.WoWonder` now exposes this JSON handler alongside the legacy `/wowonder/send_im` route, enforcing that `from_agent_id` matches the authenticated token subject and returning whether the message was delivered immediately or queued for offline delivery. Configuration retains the `AllowOfflineDelivery` toggle for operators who want to disable offline storage.

See `Docs/examples/send_im.php` for a standalone PHP script that exchanges a password grant for an `im.write` token and posts the above payload, handling online and offline responses.

### Server Flow
1. Resolve the Bearer token using `IOAuthStore` and verify `from_agent_id` matches the subject.
2. Deserialize into `GridInstantMessage` and pass it to `IMessageTransferModule` (`Scene.MessageTransferModule`) using the existing `HGMessageTransferModule`/`MessageTransferModule` connectors.
3. If the recipient is offline, persist via the existing offline messaging connector so it is delivered on next login.
4. Emit structured logging entries (correlation ID, agent IDs, success/failure) using `log4net`.
5. Rate-limit via `LeakyBucketLimiter` (new helper) tied to token + target to prevent abuse (e.g., 20 IMs / 10 seconds).

### Required Code Touch Points
- `OpenSim/Server/Handlers/Hypergrid/InstantMessageServerConnector`: extend to register the JSON handler alongside the legacy XML-RPC endpoint, using `IHttpServer.AddSimpleStreamHandler`.
- `OpenSim/Services/Interfaces/IInstantMessage`: add an overload returning richer status (delivered/offline).
- Update `OpenSim/Region/CoreModules/Avatar/InstantMessage/MessageTransferModule` to return offline/delivery state without breaking existing call sites.

## Money Balance REST Endpoint

### Endpoint Shape
| Verb | Path | Scope | Description |
| ---- | ---- | ----- | ----------- |
| `GET` | `/api/v1/money/balance` | `wallet.read` | Returns the avatar's grid currency balance and last transaction timestamp. |
| `POST` | `/api/v1/money/reconcile` | `wallet.write` | Accepts signed balance updates from WoWonder (e.g., purchases) and enqueues them for processing by the existing economy service. |

- **GET Response:**
  ```json
  {
    "agent_id": "${uuid}",
    "balance": 1250,
    "pending": 50,
    "currency": "G$",
    "updated_at": "2025-10-23T21:30:55Z"
  }
  ```
- **POST Request:**
  ```json
  {
    "agent_id": "${uuid}",
    "external_txn_id": "wowonder:order:982334",
    "amount": -199,
    "memo": "WoWonder marketplace order #982334",
    "nonce": "${uuid}",
    "signature": "base64-ed25519",
    "timestamp": "2025-10-23T21:31:33Z"
  }
  ```

### Implementation Notes
- Add `IMoneySyncService` interface in `OpenSim/Services/Interfaces` exposing `GetBalanceAsync` and `ApplyExternalTransactionAsync`.
- Reuse the existing grid economy service connectors (`OpenSim/Services/MoneyServer`) to apply credits/debits. Record `external_txn_id` and `nonce` in a new table `money_external_transactions` to prevent replay.
- Validate Ed25519 signatures using libsodium via P/Invoke (already bundled for `OpenSim.Services.LLLoginService` in some grids) or fallback to HMAC-SHA256 with per-client secrets.
- Publish balance change events through the event queue so in-world viewers see updates immediately.

## PHP WoWonder Add-on Skeleton

Place the add-on under `wowonder/requests/opentosim/` and register it in `wowonder/admin-panel/pages/addons`. Key components:

1. **Configuration (`openim/config.php`):**
   ```php
   return [
       'client_id' => getenv('OPENSIM_CLIENT_ID'),
       'client_secret' => getenv('OPENSIM_CLIENT_SECRET'),
       'redirect_uri' => $wo['config']['site_url'] . '/openim/callback',
       'scopes' => 'im.write wallet.read wallet.write profile.read'
   ];
   ```

2. **Login Controller (`openim/login.php`):** Redirect users to `/oauth/authorize` with `state` stored in WoWonder session and PKCE verifier saved in `$_SESSION['openim_pkce']`.

3. **Callback Handler (`openim/callback.php`):**
   ```php
   $token = OpenSimClient::exchangeCode($_GET['code'], $_SESSION['openim_pkce']);
   Wo_SetUserSession('opensim_token', $token);
   $profile = OpenSimClient::getUserInfo($token['access_token']);
   Wo_UpdateUserData($wo['user']['user_id'], [
       'opensim_avatar' => $profile['agent_id'],
       'opensim_balance' => OpenSimClient::getBalance($token['access_token'])
   ]);
   ```

4. **Background Cron (`openim/cron.php`):** Refresh tokens nightly, push wallet transactions to `/api/v1/money/reconcile`, and store WoWonder order IDs mapped to `external_txn_id`.

5. **Client Helper (`openim/OpenSimClient.php`):** Thin Guzzle-based wrapper that signs requests with Bearer tokens, retries on HTTP 429, and surfaces JSON decoding errors.

Expose a settings page in the WoWonder admin panel so operators can paste the Robust base URL, client credentials, and choose which scopes to enable. Use WoWonder's `Wo_CreateSession()` to protect routes and respect CSRF tokens when posting settings.

## Deployment Considerations
- Run the new OAuth and REST handlers behind the same `Robust.exe` host to reuse logging and metrics; confirm the port block in `Robust.ini` is exposed through nginx.
- Update firewall rules to allow only the WoWonder host IPs to hit `/api/v1/money/reconcile`; other endpoints require OAuth tokens anyway.
- Document the migration path: stop Robust, apply database migrations, restart Robust, then install the WoWonder add-on.
- Add integration tests under `Tests/OpenSim.Server.Handlers.Tests` verifying OAuth flows and IM delivery using the `TestClient` harness.

## Next Steps
1. Implement the database migrations and connector interfaces for `IOAuthStore` and `IMoneySyncService`.
2. Build the OAuth HTTP handlers and Razor-lite templates for the authorization UI.
3. Extend the instant message service pipeline to surface delivery metadata.
4. Add REST controllers, integrate logging/metrics, and document the APIs via OpenAPI 3.0 under `Docs/api/robust-wowonder.yaml`.
5. Publish the WoWonder add-on with packaging instructions and automated tests (PHPUnit) covering token refresh and wallet reconciliation.
