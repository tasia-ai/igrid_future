# MarketApp (Standalone)

Standalone TRON-style marketplace app cloned from current WordPress marketplace logic, without removing the existing plugin.

Location:

- `/home/marty/opensim/marketapp`

## What it does

- Lists marketplace items from WordPress tables (`opensim_item` posts).
- Avatar UUID login (session based).
- Full-screen gated login via Avatar UUID + password (no app access before auth).
- OTP over in-world IM for login (`otp_request` + `otp_verify`) when enabled.
- Shows avatar balance from money DB.
- Purchase item and optionally deliver as gift to another avatar UUID.
- Built-in admin panel (visible only for configured admin UUIDs).
- Same-origin texture proxy endpoint for more reliable thumbnail loading.

## Structure

- `public/index.php` - frontend UI (TRON style)
- `public/api.php` - JSON API router (`status`, `list`, `login`, `logout`, `purchase`)
- `src/MarketplaceApp.php` - app logic (DB access + purchase + delivery call)
- `config/config.php` - runtime configuration
- `config/config.example.php` - template configuration

## Run

Example local run:

```bash
cd /home/marty/opensim/marketapp/public
php -S 0.0.0.0:8099
```

Open:

- `http://SERVER_IP:8099`

## Config

Edit:

- `/home/marty/opensim/marketapp/config/config.php`

Key values:

- WordPress DB connection + table prefix
- OpenSim DB connection
- Money DB connection
- Delivery API URL/password
- Texture proxy source URL template
- Admin UUID allow-list
- IM/OTP settings (`im.*`, `otp.*`)

## Notes

- Existing WordPress plugin remains unchanged and active.
- This app uses direct DB reads and delivery API calls.
- Gift mode: fill `Gift recipient UUID` to deliver to another avatar.
- Password validation matches OpenSim auth logic: `md5(password + ":" + passwordSalt)` against `robust.auth`.
- Optional backup login is available via `auth_backup` config (standalone app only).
