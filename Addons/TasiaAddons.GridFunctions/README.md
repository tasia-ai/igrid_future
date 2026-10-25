# Tasia Grid Functions

Addon providing MOD-invokable `maGridSay`, `maGridSayIM`, and
`maChangeGridProfile` operations, plus service adapters that make stored virtual
avatar profiles available through the existing profile and account-service
plugin seams. SmartNPC/AI remains provided by the existing `OpenSimAIModule` addon.

## Install

1. Build this project against the target grid's exact OpenSim assemblies and .NET runtime.
2. Copy `TasiaAddons.GridFunctions.dll` and its required provider assemblies to the
   Robust and region server plugin directories. Register the region plugin with the
   host's plugin registry. The service adapters are loaded by their configured plugin
   type names (see below).
3. Run the matching `database/TasiaVirtualAvatars.*.txt` against the **robust** database.
4. Merge the service settings from `config/TasiaGridFunctions.ini.txt` into the
   existing Robust and region configuration. In Robust, select the account adapter
   under `[UserAccountService]` and the profile adapter under `[UserProfilesService]`:

   ```ini
   [TasiaGridFunctions]
   Enabled = true
   DatabaseProvider = PostgreSQL ; or MySQL
   DatabaseName = robust
   MinimumUserLevel = 150

   [UserAccountService]
   LocalServiceModule = TasiaAddons.GridFunctions.dll:TasiaVirtualAvatarAccountService

   [UserProfilesService]
   Enabled = true
   LocalServiceModule = TasiaAddons.GridFunctions.dll:TasiaVirtualAvatarProfilesService
   ```

   Enable the region module in the region's existing `[RegionModules]` section:

   ```ini
   Setup_TasiaGridFunctionsModule = TasiaGridFunctionsModule
   ```

   Keep the existing profile `StorageProvider`, `Realm`, `UserAccountService`,
   region profile URL, and account-service URI settings. The adapters delegate
   ordinary accounts and profile requests to the configured stock services.
   They read the existing `[DatabaseService] ConnectionString`. Only script owners
   at or above `MinimumUserLevel` may write profiles or send grid-wide chat.
5. Set `AllowMODFunctions = true` in the script engine's `[OSSL]` configuration.
6. Add the definitions from `lsl/TasiaGridFunctions.lsl` to TalkerChat (or the
   calling script). They preserve its `ma...` call names while routing through
   `modInvokeN`. Script engines must support the MOD invocation API.

## Profile behavior

`maChangeGridProfile` writes to `TasiaVirtualAvatars`. The account adapter adds
virtual-avatar UUID/name lookups while delegating ordinary accounts. The profile
adapter returns standard viewer profile fields for virtual-avatar UUIDs and
delegates all other profiles. No `Source/` or core rebuild is needed, but Robust
and region service configuration must point to the adapters.

The module's script invocations are dynamic. They cannot make an unknown bare
function name compile by themselves; the provided LSL wrappers are required
unless the target grid has already built these functions into its script engine.
