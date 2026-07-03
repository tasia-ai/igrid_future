# WoWonder Service Connector

The WoWonder connector adds OAuth 2.0 style endpoints and helper APIs that
allow a WoWonder-based community site to authenticate against Robust, send
instant messages, and display wallet balances. The handlers plug into Robust's
HTTP server when the assembly is deployed and the connector is enabled in the
service list.

## Deployment steps

1. Copy `TasiaAddon.WoWonder.dll` and its add-in manifest into the Robust
   `bin` directory.
2. In `Robust.ini` (and `Robust.HG.ini`) uncomment the service entry:

   ```ini
   WoWonderServiceConnector = "${Const|PublicPort}/TasiaAddon.WoWonder.dll:WoWonderServiceConnector"
   ```

3. Configure the service:

   ```ini
    [WoWonderService]
    Enabled = true
    AllowedClients = client-id:client-secret
    ScopeID = 00000000-0000-0000-0000-000000000000
    UserAccountService = OpenSim.Services.UserAccountService.dll:UserAccountService
    AuthenticationService = OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService
    UserProfilesService = OpenSim.Services.UserAccountService.dll:UserProfilesService
    InstantMessageService = OpenSim.Server.Handlers.dll:InstantMessageServerConnector
    MoneyServerUrl = https://money.example.com/currency
    MoneyServerTimeout = 10000
    AllowOfflineDelivery = true
    DefaultScope = im im.write profile balance
    AccessTokenLifetime = 3600
    RefreshTokenLifetime = 604800
    AuthorizationCodeLifetime = 300
   ```

   * `AllowedClients` – comma- or semicolon-separated list of
     `<client-id>:<secret>` pairs that the connector accepts. At least one
     entry is required.
   * `UserAccountService` / `AuthenticationService` – the inner services used
     to resolve accounts and check passwords.
   * `UserProfilesService` – optional; enables the `/oauth/userinfo` endpoint
     to return profile data.
   * `InstantMessageService` – optional; enables the `/api/v1/im/send` route for
     outbound instant messages (also available under `/wowonder/send_im`).
   * `MoneyServerUrl` – optional; enables `/api/v1/money/balance` for wallet
     lookups. Provide the base URL of the existing money server.
   * Token lifetimes control how long issued OAuth tokens remain valid. Include
     `im.write` in `DefaultScope` (the default is `im im.write profile balance`)
     for clients that post to `/api/v1/im/send`.

4. Restart Robust. The connector logs whether each endpoint is enabled and the
   public routes (`/oauth/*`, `/api/v1/*`) become available.

Refer to the PHP examples under `Docs/Patches/php/` for guidance on wiring the
WoWonder front-end to these endpoints, or the generic bearer-token sample in
`Docs/examples/send_im.php` for standalone integrations.
