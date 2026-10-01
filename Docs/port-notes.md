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

## TasiaAddons.Quic — ported, builds, not runtime-tested

QUIC is now in the build. It needed real core support, not just project-file
edits, which is why it was held back at first. What that support is:

| New/changed | Where |
|---|---|
| `IViewerTransport` — the transport seam | `Source/OpenSim.Framework/IViewerTransport.cs` |
| `QuicCircuitRegistry` — in-process circuit→endpoint rendezvous | `Source/OpenSim.Framework/QuicCircuitRegistry.cs` |
| `LLUDPClient.Transport` — optional per-circuit transport | `LLUDPClient.cs` |
| QUIC region: two events, `ProcessIncomingQuicPacket`, `HandleQuicUseCircuitCode`, `SendAckImmediate(IViewerTransport, uint)` | `LLUDPServer.cs` |
| `LLUDPServerShim.UdpServer` + self-registration on the scene | `LLUDPServer.cs` |
| `RegionInfo.QuicHost`/`QuicPort`, `[ClientStack.Quic]` read, pack/unpack/`ToKeyValuePairs` | `RegionInfo.cs` |
| `PacketFraming.cs`, `QuicViewerTransport.cs`, `PluginRegistration.cs` | `Addons/TasiaAddons.Quic/` |

The send side is ~10 lines because `LLUDPServer.SendPacketFinal` is the single
choke point every viewer packet already passes through — ACKs are appended and
the sequence number assigned above the hook. Inbound QUIC packets re-enter the
normal `PacketReceived` path via a synthesised `UDPPacketBuffer`, which is what
keeps appended-ACK handling, dedup, ping and the packet inbox unchanged.

Deliberately **not** ported, all separate features with their own risk:

- i-Grid's Quick-G brain/bridge lease mode. The newer addon copy removed it and
  hard-fails without a configured port.
- i-Grid's "crossing fix" (`TryRehomeClient`, `Scene.UpdateClientEndPoint`).
- i-Grid's "keep child presence on teleport" fix.

**Still not wired up:** `LLLoginService` does not populate
`SimQuicHost`/`SimQuicPort`, and does not serialise them into the login packet
either (i-Grid `LLLoginResponse.cs:503-506` and `:608-611`). `GridRegion` has no
`QuicHost`/`QuicPort`. So viewers cannot yet discover the endpoint through login
or the grid — nothing populates the login-side values, which stay null/0, and
`MACAudit` correctly reads that as "no QUIC".

**Not runtime-tested.** It compiles and the plain UDP path is provably
unaffected (`Transport` is null for every ordinary viewer), but the QUIC path
needs a QUIC viewer, which none of us has here.

## SmartNPC grid-wide teleport

Intra-region teleport already works. Cross-region and Hypergrid are new protocol
work, not a fix. See [smartnpc-grid-teleport.md](smartnpc-grid-teleport.md) —
including the `requirePresenceLookup` NPC escape hatch that Tranquillity already
declares and never uses.

## pr/180 must not be merged

`refs/heads/pr/180` is a single commit adding one comment line to
`OpenSimDefaults.ini`:

```ini
 ; OSAWS specific Capability
+; Only activate this when you are using OSAWS
 ExternalViewerAssetsURL = "http://viewerasset.yourgrid.com"
```

The change is harmless, but the **branch** is not mergeable: it descends from a
much older commit, so `git diff origin/develop pr/180` reports the entire tree
as different and a merge tries to reconcile hundreds of files (it conflicted in
`GetAssetsHandler.cs` and would have brought in deletions of the AIS subsystem,
Phlox, TrustedHypergrid, ~90 test projects and much else). If that comment is
wanted, apply it by hand.

## BinaryFormatter — already resolved upstream, nothing to do

An earlier draft of this file claimed `BinaryFormatter` was still present and
that OAR/IAR loading was broken on .NET 10. **That was wrong**, and worth
recording so nobody re-chases it. Tranquillity has no `BinaryFormatter` usage:

- No `new BinaryFormatter()` anywhere in the tree.
- No `EnableUnsafeBinaryFormatterSerialization` in any `.csproj`/`.props`/`.targets`.
- A full `Tranquillity.sln` Release build produces zero `SYSLIB0011` warnings.

All three places that used to hold it were rewritten upstream:

| File | Replacement |
|---|---|
| `FlotsamAssetCache.cs` | `XmlSerializer(typeof(AssetBase))`, with a `format2` subdirectory so legacy binary cache files are never read |
| `KeyframeMotion.cs` | an explicit length-prefixed binary "KFM1" format; the legacy path is refused as a graceful degradation |
| `XMRInstAbstract.cs` | explicit per-type opcodes. `SYSERIAL`/`THROWNEX` remain only as enum values for wire compatibility with old saved state, and `SYSUNSUP` marks a type that cannot be serialized — all three **throw on read**, caught by `LoadScriptState`, which resets the script and re-fires `state_entry` |

So the Linux failure
`BinaryFormatter serialization and deserialization have been removed` does not
come from Tranquillity's code. It is the i-Grid fork at
`H:\grid\work\fixtest` that still carries it — see `FlotsamAssetCache.cs`
around lines 529 and 1015 there. Porting *to* Tranquillity is what removes it.

## Not touched

- The 18th directory, `addon-modules/TasiaAddons/`, is a container with no
  csproj of its own.
