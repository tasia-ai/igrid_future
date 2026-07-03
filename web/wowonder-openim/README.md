# WoWonder OpenIM Add-on Skeleton

This directory contains a barebones PHP implementation that demonstrates how to
connect a WoWonder community site to the `TasiaAddon.WoWonder`
add-on's `WoWonderServiceConnector`
endpoints.

## Files
- `config.php` – Loads client credentials and endpoint URLs.
- `OpenSimClient.php` – cURL-based helper for OAuth, IM, and wallet APIs.
- `login.php` – Starts the OAuth flow using PKCE and state tracking.
- `callback.php` – Exchanges the authorization code and persists tokens.
- `settings.php` – Minimal UI to show the linked avatar and balance.
- `cron.php` – Nightly task for refreshing tokens and updating cached balances.
- `install.php` – Schema helper for WoWonder's users table.

Integrate these pieces into your WoWonder installation by wiring the routes to
match your theme and hooking `cron.php` into your scheduled jobs. Extend the
placeholder functions (`Wo_GetPendingOpenSimOrders`, etc.) to suit your
site-specific data model.
