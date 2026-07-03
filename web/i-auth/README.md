# i-auth (IM OTP auth + OIDC provider)

## What this does
- Sends OTP to avatar UUID using OpenSim `grid_instant_message` XML-RPC.
- Verifies OTP in PHP session.
- Supports three targets:
  - `target=panel` -> authenticate panel self-service and redirect back
  - `target=keycloak` -> continue with Keycloak SSO flow
  - `target=oidc` -> authenticate for OIDC authorize flow (external IdP mode)
- Creates/fetches user in Keycloak with profile mapping:
  - `email = <avatar_uuid>@i-grid.users`
  - `username = <first>_<last>_<3digits>`
  - `firstName/lastName` from `robust.UserAccounts`
- Starts OIDC browser login with `login_hint`.
- Exposes OIDC IdP endpoints for Keycloak broker:
  - `/i-auth/oidc/.well-known/openid-configuration`
  - `/i-auth/oidc/authorize`
  - `/i-auth/oidc/token`
  - `/i-auth/oidc/userinfo`
  - `/i-auth/oidc/jwks`

## Files
- `index.php` - OTP flow + provisioning + SSO launch
- `callback.php` - OIDC callback and token save in session
- `lib.php` - shared helpers
- `oidc/*` - external OIDC IdP endpoints

## Environment variables
- `IAUTH_DB_HOST` (default `i.let-us.cyou`)
- `IAUTH_DB_USER` (default `root`)
- `IAUTH_DB_PASS` (default ``)
- `IAUTH_DB_NAME` (default ``)
- `IAUTH_ROBUST_DB` (default ``)
- `IAUTH_IM_ROBUST_URL` (default `http://i.let-us.cyou:8002`)
- `IAUTH_IM_FROM_UUID` (default `00000000-0000-0000-0000-000000000000`)
- `IAUTH_IM_FROM_NAME` (default `I-Grid Security`)
- `IAUTH_OTP_TTL_SEC` (default `300`)
- `IAUTH_KEYCLOAK_BASE` (required in production)
- `IAUTH_KEYCLOAK_REALM` (default `i-grid`)
- `IAUTH_KEYCLOAK_CLIENT_ID` (default `i-auth-web`)
- `IAUTH_KEYCLOAK_CLIENT_SECRET` (required)
- `IAUTH_KEYCLOAK_REDIRECT_URI` (default auto `/i-auth/callback.php`)

OIDC IdP mode:
- `IAUTH_OIDC_ISSUER` (default auto `/i-auth/oidc`)
- `IAUTH_OIDC_CLIENT_ID` (default `keycloak-broker`)
- `IAUTH_OIDC_CLIENT_SECRET` (set strong secret)
- `IAUTH_OIDC_CODE_TTL_SEC` (default `180`)
- `IAUTH_OIDC_ACCESS_TTL_SEC` (default `3600`)
- `IAUTH_OIDC_KEYS_DIR` (default `/var/www/html/web/i-auth/.keys`)

## Note
For the most seamless UX (OTP directly in Keycloak login page), a Keycloak custom authenticator SPI is still the best option.
