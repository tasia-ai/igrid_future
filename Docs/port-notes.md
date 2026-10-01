# Porting the Tasia addons from i-Grid to Tranquillity

Branch: `develop` (pushed as `tranquillity/hg-travel-net10`)
Baseline: `origin/develop` @ `148896caec` of `OpenSim-NGC/OpenSim-Tranquillity`

## What the addons were in i-Grid

18 directories under `addon-modules/`, targeting **net8.0**, referencing the
already-built assemblies out of `$(SolutionDir)/bin/*.dll` with `<Private>False</Private>`.

## What they are now

16 addons under `Addons/`, targeting **net10.0** (inherited from
`Directory.Build.props`, no per-csproj `TargetFramework`), referencing OpenSim the
way the stock `Addons/OpenSimSearch` does: `<ProjectReference Include="..\..\Source\..."/>`.

`Tasia.Extensions.SDK` (5 files, the `IGridExtension`/`ISimRegionHooks` interfaces)
moved to `Source/Tasia.Extensions.SDK/`, which is a path i-Grid already used.

### Mechanical changes applied

| Change | Why |
|---|---|
| Dropped `<TargetFramework>net8.0</TargetFramework>` | `Directory.Build.props` sets `net10.0` |
| `$(SolutionDir)/bin/*.dll` `<Reference>` → `ProjectReference` | Tranquillity has no shared `bin/`; each project outputs to its own `bin/Release/net10.0/` |
| Dropped `Nini`, `OpenMetaverse*` references | `Directory.Build.props` already supplies them as `UtopiaSkye` packages |
| Dropped `OpenSim` reference | it is the `OpenSim.Server.RegionServer` **Exe**; no addon namespace needs it |
| Dropped `OpenSim.Region.Framework.Interfaces` reference | the *namespace* survives, the assembly is `OpenSim.Region.Framework` |
| `XMLRPC` → `..\..\Library\xmlrpc.dll` | matches `Gloebit.GloebitMoneyModule` |
| `log4net` → `PackageReference` 3.2.0 → **3.3.2** | match `OpenSim.Framework` |
| `MySqlConnector` 2.4.0 → **2.6.1** in `TasiaAddons.MACAudit` | NU1605 downgrade: `OpenSim.Data` requires 2.6.1 |
| `addon-modules/` → `Addons/` in cross-addon refs | new layout |
| `Mono.Addins` → `PackageReference` 1.4.1 | Tranquillity keeps no `Mono.Addins.dll`; the addins carry `[Addin]`, `[AddinDescription]`, `[AddinDependency]`, `[Extension]` |
| `MainServer.Instance.AddSimpleStreamHandler(...)` → `MainServer.Instance.DefaultServer.AddSimpleStreamHandler(...)` (8 sites) | i-Grid put the handler registration on `IMainServer`; Tranquillity keeps it on `IHttpServer`. `DefaultServer` is what the stock addons use |
| `TasiaAddons.JoinApi/AssemblyInfo.cs` reduced to `[AssemblyDescription]` | CS0579: Nerdbank.GitVersioning already generates Title/Product/Company/Configuration and the version attributes into `obj\` |

### One core change

`Source/OpenSim.Services.LLLoginService/LLLoginResponse.cs` gained
`SimQuicHost` (string) and `SimQuicPort` (uint), with backing fields.

Reason: `TasiaAddons.MACAudit` reads them, and the i-Grid fork had them under
exactly these names. Keeping the names means the already-deployed i-Grid DLLs
and any caller compiled against them keep working.

**While QUIC is disabled nothing populates them**: `SimQuicHost` is null and
`SimQuicPort` is 0, which is precisely how `MACAudit` detects "no QUIC on this
sim" and degrades gracefully. This is additive; it changes no existing behaviour.

## TasiaAddons.Quic — excluded from the build

`TasiaAddons.Quic` is **not** in `Addons.slnx`. It is the one addon that cannot
be ported by rewriting project files, because in the i-Grid fork it depended on
changes made to core that Tranquillity does not have:

1. **`LLLoginResponse.SimQuicHost` / `SimQuicPort`** — addressed above, the
   properties now exist. Population still needs the QUIC server module.

2. **`IMainServer.AddSimpleStreamHandler` / `RemoveSimpleStreamHandler` /
   `AddHTTPHandler` / `RemoveHTTPHandler`** — i-Grid put these on `IMainServer`.
   Tranquillity has them on `IHttpServer`. Same shape as the fix applied to the
   other 4 addons, so this is mechanical once QUIC is being worked on.

3. **`IViewerTransport` — does not exist in Tranquillity at all.** This is the
   hook that lets the viewer connection be carried over QUIC instead of UDP, and
   it is the real work: Tranquillity's `LLClientView`/`LLUDPServer` have no
   transport abstraction, so QUIC needs a genuine design plus core changes.

Its own sources are present and complete (`QuicClientConnection.cs` 431 lines,
`QuicServerConfig.cs` 288 lines, `QuicProxyConnector.cs` 1613 lines,
`QuicServerModule.cs` 661 lines). In i-Grid, only 2 of the 4 were compiled by
the addon — the other two came from a patched core assembly. Both have been added
to `<Compile Include>`, so the addon is self-contained on that point.

`System.Net.Quic` is present in the .NET 10 runtime; `QuicConnection.IsConnected`
still does not exist, which the code already works around with a manual flag.

## Not touched

- **`BinaryFormatter`** is still in `origin/develop` in
  `FlotsamAssetCache.cs`, `KeyframeMotion.cs` and `XMRInstAbstract.cs`.
  This is the same failure that shows on Linux:
  `BinaryFormatter serialization and deserialization have been removed`.
  `XMRInstAbstract` handles OAR/IAR, so **OAR loading is broken on .NET 10 until
  this is replaced.** Migrating to Tranquillity does not fix it; it is separate work.
- The 18th directory, `addon-modules/TasiaAddons/`, is a container with no
  csproj of its own.
