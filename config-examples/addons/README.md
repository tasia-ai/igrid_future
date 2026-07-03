# Add-on modules overview

This repo ships several optional add-on modules under `addon-modules/`. Enable them by copying the compiled DLL and
its `*.addin.xml` manifest into `bin/addon-modules/<ModuleName>` and adding the corresponding config block to your
`OpenSim.ini`/`Robust.ini` (as documented in each add-on README).

## Shipped add-ons

- **TasiaAddon.Marketplace** (`addon-modules/TasiaAddon.Marketplace`)
  - Prim delivery endpoint `/send`.
  - Enable in `OpenSim.ini` with `[Marketplace] Enabled = true` and a required `Password`.
- **TasiaAddon.ChatAudit** (`addon-modules/TasiaAddon.ChatAudit`)
  - Chat audit logging. See module README for the config block.
- **TasiaAddon.AlertNotifications** (`addon-modules/TasiaAddon.AlertNotifications`)
  - Alert notification forwarding. See module README for the config block.
- **TasiaAddon.MACAudit** (`addon-modules/TasiaAddon.MACAudit`)
  - MAC address audit logging. See module README for the config block.
- **TasiaAddon.RemoteSound** (`addon-modules/TasiaAddon.RemoteSound`)
  - Remote sound streaming support. See module README for the config block.
- **TasiaAddon.WoWonder** (`addon-modules/TasiaAddon.WoWonder`)
  - WoWonder integration endpoints. See module README for configuration and API routes.
- **Gloebit** (`addon-modules/Gloebit/GloebitMoneyModule`)
  - Gloebit currency integration. See the Gloebit README for installation and credentials.
- **OpenSim.Region.OptionalModules.Currency** (`addon-modules/OpenSim.Region.OptionalModules.Currency`)
  - Optional currency modules. See the module README and `MoneyServer.ini.example`.
- **OpenSim.Server.MoneyServer** (`addon-modules/OpenSim.Server.MoneyServer`)
  - Legacy money server. See the module documentation for setup.
- **OpenSimSearch** (`addon-modules/OpenSimSearch`)
  - Search service modules. See the module README for configuration.
- **OpenSimMutelist** (`addon-modules/OpenSimMutelist`)
  - Mute list service modules. See the module README for configuration.
- **os-webrtc-janus** (`addon-modules/os-webrtc-janus`)
  - WebRTC/Janus integration. See the module README for configuration.
