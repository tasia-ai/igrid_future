# Addons integrated from the legacy Tranquillity fork

This repository includes the following add-on modules sourced from the legacy
`2-NGC_OS_Tranquillity_Fork` tree (no binaries committed).

## Add-on modules

### Tasia add-on pack
- `addon-modules/TasiaAddon.AlertNotifications`
- `addon-modules/TasiaAddon.ChatAudit`
- `addon-modules/TasiaAddon.MACAudit`
- `addon-modules/TasiaAddon.Marketplace`
- `addon-modules/TasiaAddon.RemoteSound`
- `addon-modules/TasiaAddon.WoWonder`
- `Source/OpenSim.ApplicationPlugins.TasiaAddonsCore`
- `Source/OpenSim.Server.TasiaAddonsRobust`
- `Source/TasiaAddon.Scripts`
- `Source/TasiaAddons.Abstractions`

### NGC add-on pack
- `Source/NGC.NetworkOverlay`
- `Tests/NGC.NetworkOverlay.Tests`
- `Docs/NGC.NetworkOverlay`

## Core hooks and runtime behavior

- **Rotating welcome message (LoginService)**  
  `OpenSim/Services/LLLoginService/LLLoginService.cs` pulls the welcome message
  from `MessageUrl` on each login and falls back to `WelcomeMessage`.

- **IM API endpoint in WoWonder**  
  `addon-modules/TasiaAddon.WoWonder` exposes `/api/v1/im/send` when
  `InstantMessageService` is configured (see the module README for details).

- **NPC permission handling for dance/animation invites**  
  `Source/OpenSim.Region.ScriptEngine.Shared/Api/LSL_Api.cs` honors:
  - `npc_auto_grant_anim_perms` (auto-grants animation perms for NPCs)
  - `npc_forward_perms_to_owner` (forwards NPC permission prompts to the owner)

## Notes

- These modules are wired into `Tranquillity.sln` so they can be built easily in
  future forks.
