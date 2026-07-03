# Alert Notification Service Connector

The alert notification connector exposes a simple `/alert` HTTP endpoint on
Robust that estates and automation tooling can use to push broadcast messages
to residents. It wraps the existing estate and grid services to deliver the
alerts with the correct scope, origin avatar, and region targeting.

## Deployment

1. Build the project or copy `TasiaAddon.AlertNotifications.dll` into the
   Robust `bin` directory.
2. In `Robust.ini` (and `Robust.HG.ini` if you ship the hypergrid variant)
   extend the `[ServiceList]` with:

   ```ini
   ; AlertServiceConnector = "${Const|PublicPort}/TasiaAddon.AlertNotifications.dll:AlertNotificationConnector"
   ```

   Uncomment the line when you are ready to enable the endpoint.

3. Define the service configuration:

   ```ini
   [AlertService]
   Enabled = true
   EstateService = OpenSim.Data.MySQL.dll:MySqlEstateData
   GridService = OpenSim.Services.GridService.dll:GridService
   EstateToken = change-me
   DefaultFromName = System
   DefaultFromID = 00000000-0000-0000-0000-000000000000
   DefaultScopeID = 00000000-0000-0000-0000-000000000000
   RequireAuthentication = true
   AuthKey = change-me
   ```

   * `EstateService` – plugin used to resolve estate owners. The built-in
     MySQL and SQLite providers implement `IEstateDataService`.
   * `GridService` – the grid service implementation used to locate regions.
   * `EstateToken` – optional shared secret for cross-grid estate requests.
   * `DefaultFromName` / `DefaultFromID` – sender details used when the
     request does not provide overrides.
   * `DefaultScopeID` – scope UUID assigned to alerts without an explicit
     scope.
   * `RequireAuthentication` / `AuthKey` – standard Robust service
     authentication helper used to protect the endpoint.

## Request format

The endpoint accepts either URL-encoded form data or JSON. Field names mirror
the `AlertNotificationRequest` struct:

- `EstateID` (or `estate_id`)
- `RegionID` (or `region_id`)
- `FromID` / `from_id`
- `FromName` / `from_name`
- `Message` / `message`
- `ScopeID` / `scope_id`

Missing fields fall back to the defaults configured above. The service returns
`200 OK` on success and `400`/`502` when validation or delivery fails.
