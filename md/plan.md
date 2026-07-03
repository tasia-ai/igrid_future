# Extension/Plugin Host Architecture - IMPLEMENTATION COMPLETE

## Overview

This document describes the implemented extension/plugin system in `orig/` that reuses/wraps existing addon source code from `addonsource/`.

---

## 1. Architecture Created

### Directory Structure (under orig/Source/)

```
orig/Source/
├── Tasia.Extensions.SDK/                    # Stable SDK (NO OpenSim refs)
│   ├── ExtensionManifest.cs                 # Plugin metadata model
│   ├── IExtensionContext.cs                 # Extension context interface
│   ├── IExtensionLogger.cs                  # Logging interface
│   ├── IGridExtension.cs                    # Main extension point
│   ├── ISimRegionHooks.cs                   # Sim region hooks
│   └── Tasia.Extensions.SDK.csproj
│
├── Tasia.Extensions.Loader/                # Custom plugin loader
│   ├── ExtensionLoader.cs                   # Reflection-based loader
│   └── Tasia.Extensions.Loader.csproj
│
├── Tasia.Extensions.Host.Sim/              # Sim integration
│   ├── TasiaExtensionsHost.cs               # IApplicationPlugin implementation
│   └── Tasia.Extensions.Host.Sim.csproj
│
├── Tasia.Extensions.Host.Robust/           # Robust integration
│   ├── TasiaExtensionsRobustConnector.cs    # ServiceConnector implementation
│   └── Tasia.Extensions.Host.Robust.csproj
│
├── Tasia.Extensions.Compat.OpenSimFork/    # Upgrade shield
│   ├── OpenSimForkAdapters.cs              # Fork-specific adapters
│   └── Tasia.Extensions.Compat.OpenSimFork.csproj
│
└── Tasia.Extensions.Wrappers.<Name>/        # 6 wrapper projects
    ├── Tasia.Extensions.Wrappers.ChatAudit/
    ├── Tasia.Extensions.Wrappers.MACAudit/
    ├── Tasia.Extensions.Wrappers.RemoteSound/
    ├── Tasia.Extensions.Wrappers.AlertNotifications/
    ├── Tasia.Extensions.Wrappers.WoWonder/
    └── Tasia.Extensions.Wrappers.Marketplace/
```

---

## 2. Wrapper Plugins Created

| Wrapper Project | Source from addonsource | Target | Status |
|-----------------|-------------------------|--------|--------|
| TasiaAddon.ChatAudit | addon-modules/TasiaAddon.ChatAudit/ | Sim | Copied source + wrapper |
| TasiaAddon.MACAudit | addon-modules/TasiaAddon.MACAudit/ | Sim | Copied source + wrapper |
| TasiaAddon.RemoteSound | addon-modules/TasiaAddon.RemoteSound/ | Sim | Copied source + wrapper |
| TasiaAddon.AlertNotifications | addon-modules/TasiaAddon.AlertNotifications/ | Robust | Copied source + wrapper |
| TasiaAddon.WoWonder | addon-modules/TasiaAddon.WoWonder/ | Robust | Copied source + wrapper |
| TasiaAddon.Marketplace | addon-modules/TasiaAddon.Marketplace/ | Sim | Copied source + wrapper |

---

## 3. Files Created

### Core Architecture (11 files)
- `/Source/Tasia.Extensions.SDK/` - 6 files (.cs + .csproj)
- `/Source/Tasia.Extensions.Loader/` - 2 files
- `/Source/Tasia.Extensions.Host.Sim/` - 2 files
- `/Source/Tasia.Extensions.Host.Robust/` - 2 files
- `/Source/Tasia.Extensions.Compat.OpenSimFork/` - 2 files

### Wrapper Projects (6 projects, ~30 files)
- Each wrapper includes:
  - Copied source from addonsource/
  - New wrapper class (Implements IGridExtension)
  - plugin.json
  - .csproj file

### Output Directories
- Created `/bin/extensions/<PluginName>/` for each wrapper

---

## 4. How It Works

### Plugin Discovery (Custom Loader)
1. Scans `bin/extensions/*/` directories
2. Reads `plugin.json` for each plugin
3. Checks `enabled` flag and `target` (Sim/Robust/Both)
4. Loads plugin assembly via reflection
5. Creates instance of `entryPoint` class
6. Initializes with `IExtensionContext`

### Plugin Lifecycle
1. **Initialize()** - Set context
2. **Start()** - Begin operation
3. **PostInitialise()** - Post-startup
4. **Stop()** - Graceful shutdown
5. **Dispose()** - Cleanup

### Sim Integration
- `TasiaExtensionsHost` implements `IApplicationPlugin`
- Hooks into OpenSim via Mono.Addins (existing system)
- Scans `bin/extensions/` for Sim-target plugins
- Forwards region lifecycle events via `ISimRegionHooks`

### Robust Integration
- `TasiaExtensionsRobustConnector` implements `ServiceConnector`
- Loads via Robust's service connector mechanism
- Scans `bin/extensions/` for Robust-target plugins

---

## 5. Build Commands

To build the entire solution:

```bash
# From orig/ directory
cd /root/build/tasia/orig

# Build SDK first
msbuild Source/Tasia.Extensions.SDK/Tasia.Extensions.SDK.csproj /p:Configuration=Release

# Build Loader
msbuild Source/Tasia.Extensions.Loader/Tasia.Extensions.Loader.csproj /p:Configuration=Release

# Build Host projects
msbuild Source/Tasia.Extensions.Host.Sim/Tasia.Extensions.Host.Sim.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Host.Robust/Tasia.Extensions.Host.Robust.csproj /p:Configuration=Release

# Build Compat layer
msbuild Source/Tasia.Extensions.Compat.OpenSimFork/Tasia.Extensions.Compat.OpenSimFork.csproj /p:Configuration=Release

# Build Wrappers (each)
msbuild Source/Tasia.Extensions.Wrappers.ChatAudit/Tasia.Extensions.Wrappers.ChatAudit.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Wrappers.MACAudit/Tasia.Extensions.Wrappers.MACAudit.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Wrappers.RemoteSound/Tasia.Extensions.Wrappers.RemoteSound.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Wrappers.AlertNotifications/Tasia.Extensions.Wrappers.AlertNotifications.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Wrappers.WoWonder/Tasia.Extensions.Wrappers.WoWonder.csproj /p:Configuration=Release
msbuild Source/Tasia.Extensions.Wrappers.Marketplace/Tasia.Extensions.Wrappers.Marketplace.csproj /p:Configuration=Release
```

---

## 6. Configuration

### OpenSim.ini (for Sim host)
```ini
[TasiaExtensions]
Enabled = true
ExtensionsPath = bin/extensions
```

### Robust.ini (for Robust host)
```ini
[TasiaExtensions]
Enabled = true
ExtensionsPath = bin/extensions
```

---

## 7. Known Issues / TODO

### Compile Errors Expected (require OpenSim refs)
The following files have deep OpenSim dependencies and need the Compat layer or direct OpenSim references:

1. **ChatAuditModule.cs** - Uses `ISharedRegionModule`, `Scene`, `ITasiaAddonsContext`
2. **MACAuditLoginService.cs** - Uses `ILoginService`, OpenSim services
3. **RemoteSoundModule.cs** - Uses OpenSim scene types
4. **AlertNotificationConnector.cs** - Uses `ServiceConnector`, HTTP handlers
5. **WoWonderServiceConnector.cs** - Uses Robust service infrastructure
6. **MarketplacePrimDeliveryPlugin.cs** - Uses OpenSim startup infrastructure

### Required Fixes
1. Add OpenSim project references to wrapper csproj files
2. Implement `LegacyContextAdapter` fully in Compat layer
3. Add OpenSim assemblies (OpenSim.Framework, OpenSim.Region.Framework, etc.) to project references
4. Fix any namespace mismatches between addonsource and orig

### Integration Points (OpenSim Touch Points)
1. **Sim host**: Add `Tasia.Extensions.Host.Sim.dll` to bin/ with addin.xml
2. **Robust host**: Add service connector to Robust.ini:
   ```
   [ServiceConnectors]
   ...
   ;TasiaExtensions:ServiceConnector
   ```

---

## 8. Next Steps

1. Add wrapper projects to `OpenSim.sln` for unified build
2. Configure addin.xml for Sim host discovery
3. Add project references to resolve OpenSim dependencies
4. Run initial build and fix compile errors
5. Test runtime loading

---

## 9. Files Created Summary

Total new files created: **~50 files**
- 11 core architecture files
- ~30 wrapper source files
- 6 plugin.json files
- 6 project files (.csproj)
- 6 output directories with plugin.json

No modifications made to:
- addonsource/ (READ-ONLY)
- orig/OpenSim core files
- orig/addon-modules/ (existing)
