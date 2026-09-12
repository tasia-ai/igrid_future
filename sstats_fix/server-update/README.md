# Server Update Package (compiled)

## bin/ — Copy these DLLs to the server's bin/ folder

| DLL | What changed |
|-----|-------------|
| OpenSim.Region.CoreModules.dll | EstateManagersAllowed UUID whitelist |
| OpenSim.Region.Framework.dll | Core framework (dependency) |
| OpenSim.Region.OptionalModules.dll | SStats fixes (auth, path parsing, thread safety) |
| TasiaAddons.Abstractions.dll | Shared Tasia addons interface |
| TasiaAddons.LoginSecurity.dll | NEW: SQLiteAccessControlData (ban DB layer) |
| TasiaAddons.MACAudit.dll | Login response fix + ban check wiring |

## bin/config-include/regions/AccessControl.ini
Updated with `[AccessControlService]` section for ban DB storage.

## ngc-macaudit-sample.ini
Reference config showing all MACAudit options including AccessControlService.

## After copying
1. Copy `bin/*.dll` → server's `bin/` folder (overwrite)
2. Copy `bin/config-include/regions/AccessControl.ini` → server's config
3. Add to region INI:
```ini
[EstateManagement]
EstateManagersAllowed = your-uuid-here
```
4. Restart the grid server
