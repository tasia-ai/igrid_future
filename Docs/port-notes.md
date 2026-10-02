# Porting the Tasia addons from i-Grid to Tranquillity

Branch: `develop` (pushed as `tranquillity/hg-travel-net10`)
Baseline: `origin/develop` @ `148896caec` of `OpenSim-NGC/OpenSim-Tranquillity`

This file covers the addon port. The port was subsequently **cut over to
production** on .NET 10 — see
[Production cutover to .NET 10](#production-cutover-to-net-10) at the end,
which is the operational document. The two baseline references differ because
they answer different questions: `148896caec` is what this port was written
against, `cf6772b7d9` is where `develop` stood when the cutover shipped.

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

**Production status update (2026-10-03):** the module does load and attach to
the scene on the deployed net10 build, but **no UDP listener ever binds** — this
is an open bug, not a success. Worse, its log4net logger is silently discarded
under the net10 host, so it produces no output at all. The full diagnosis is in
[Open issue: QUIC transport](#open-issue-quic-transport--not-fixed) below; do
not conclude from the absence of errors that QUIC is working.

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

---

# Production cutover to .NET 10

Status: **cutover complete**. All 31 regions and Robust are live on the
self-contained .NET 10 region build. What follows is the operational record —
the runtime layout, the exact process launch contract, how to repeat the
cutover, how to verify one, and what is still open.

Everything below was verified against the running system on 2026-10-03.

## State at cutover

| | |
|---|---|
| Source repo | `H:\grid\tranquillity` |
| Base branch | `develop` @ `cf6772b7d9` |
| Local commits on top of base | `a4d0e7a7b1`, `309deb22e6`, `76db8deb96` |
| Uncommitted in tree | `Addons\TasiaAddons.Quic\QuicServerModule.cs` only |
| Product version string | `1.1.130-alpha+cf6772b7d9` (FileVersion `1.1.130.53095`) |
| Region server project | `OpenSim.Server.RegionServer`, now built as `OpenSim.exe` |
| Region host runtime | self-contained `win-x64`, `net10.0` / `Microsoft.NETCore.App 10.0.10` |
| Program home | `H:\grid_final\opensim-base` |
| Robust home | `H:\grid\robust-net8` (deliberately still net8) |
| Deployment supervisor | `H:\grid\FreshMetaverseManager` (WPF, net8), `MainWindow.xaml.cs` |
| Regions live | 31 |

The three local commits are, in order: replacing `$(SolutionDir)`
`ProjectReference` paths in the addons with relative ones; building the region
server as `OpenSim.exe` and wiring the Tasia addons in; and the daemon-mode
console guard described below.

**The version string is not a deployment identity.** It is
Nerdbank.GitVersioning output keyed to the merge height, so it reads
`cf6772b7d9` even though the deployed binaries include all three local
commits. Identify a deployed build by file hash or timestamp, never by the
version string.

### Symptom index

| Symptom | Actual cause | Section below |
|---|---|---|
| `Unrecognized command or argument` at region start | `-inifile=<path>` instead of `--inifile "<path>"` | Region process launch contract |
| `DllNotFoundException: ubode` | `lib64` not prepended to `PATH` | Region process launch contract |
| `IOException: The handle is invalid` at region startup | pre-fix `LocalConsole` setting `TreatControlCAsInput` | Daemon-mode console bug |
| Log still growing minutes after startup, millions of lines | crash loop from the `Console.KeyAvailable` poll | Daemon-mode console bug |
| `IndexOutOfRangeException: Field not found in row: experienceID` | column is spelled `experienceid` | Database migration applied |
| Viewer reports 502 / cannot log in | a region was down, so the proxy had no upstream | 502 / Bad Gateway |
| An addon logs nothing at all, with no error | log4net has zero appenders under the net10 host | Open issue: QUIC transport |
| No UDP listener on a region's `quic_port` | QUIC listener never binds — open bug, not a config error | Open issue: QUIC transport |

## Runtime layout

**The program home has no `bin\` layer.** `H:\grid_final\opensim-base` holds
`OpenSim.exe`, `OpenSim.dll`, `Robust.ini`, `OpenSimDefaults.ini`, `lib64\`,
`runtimes\`, the 13 satellite resource directories, and 584 root files
directly in its root, alongside the live `regions\` and `data\` trees. Verified
on disk: no `bin\` subdirectory, `OpenSim.exe` present, `Robust.ini` present,
31 directories under `regions\`, 31 under `data\`.

**Robust is not in the program home, on purpose.** It stays framework-dependent
on net8 at `H:\grid\robust-net8` and listens on `127.0.0.1:22000`. Because of
that split, the supervisor config carries two roots:

```ini
; H:\grid\FreshMetaverseManager\bin\Release\net8.0-windows\manager.ini
GridRoot=H:\grid_final
RobustRoot=H:\grid\robust-net8
```

The region list with all port assignments lives in
`H:\grid_final\generated\deploy.json`. Each entry of `sims` carries `name`,
`port`, `http_port`, `quic_port`, `region_uuid`, and also `database` (the
per-region PostgreSQL database name — see the migration section). The
`log_file` and `log_config` keys in that JSON are **stale**, pointing at an old
`H:\grid\igrid-package` tree; the real log location is decided by the `LOGDIR`
environment variable, not by this JSON.

## Region process launch contract

```
"<programhome>\OpenSim.exe" --inifile "<programhome>\regions\<name>\OpenSim.ini"
```

**Two dashes and a space.** The older `-inifile=<path>` form is wrong: the
.NET 10 region host parses its command line with `System.CommandLine`, which
rejects `-inifile=<path>` with `Unrecognized command or argument` and the
region never starts.

Note the asymmetry: Robust still takes `-inifile="<path>"` with a **single**
dash (`MainWindow.xaml.cs:741`), because Robust is still the old Nini-based
net8 host. Do not "correct" Robust's argument form.

Six requirements, all mandatory:

| Requirement | Value | Consequence if wrong |
|---|---|---|
| Argument form | `--inifile "<path>"` — two dashes, space, quoted | `Unrecognized command or argument`, region does not start |
| Working directory | **must** be `<programhome>` | relative asset/INI lookups resolve against the wrong tree |
| `PATH` | `<programhome>\lib64` **prepended**, not appended | `DllNotFoundException: ubode` — the physics natives `ubode.dll` and `BulletSim.dll` live in `lib64\` |
| `LOGDIR` | `<programhome>\data\<name>` | log written to the wrong place; per-region log separation lost |
| Standard streams | redirected (stdin, stdout, stderr) | without redirection the daemon-mode bug below fires |
| Console window | none (`CreateNoWindow`) | mandatory together with redirection |

Setting `LOGDIR` to `<programhome>\data\<name>` puts the Serilog log at:

```
<programhome>\data\<name>\OpenSim.Server.RegionServer<yyyyMMdd>.log
```

Verified live, e.g. `H:\grid_final\opensim-base\data\Fresh01\OpenSim.Server.RegionServer20261003.log`.
The date rolls over at midnight, so a region running for days has one file per
day and the current-day file is *not* where the startup banner is.

`Console.In.ReadLine()` is also the channel by which the manager feeds console
commands to a running region, which is why redirection is a feature and not
just hygiene.

## Repeating the cutover

Two scripts in the repo do this. `deploy-region-server.ps1` writes;
`verify-deployment.ps1` is read-only and never writes.

**Step 0 — health check before touching anything.**

```powershell
powershell -File H:\grid\tranquillity\tools\verify-deployment.ps1 `
  -ProgramHome H:\grid_final\opensim-base
```

**Step 1 — stop the regions.** Use the supervisor.
`deploy-region-server.ps1` aborts if *any* `OpenSim.exe` is running unless
`-AllowRunning` is passed. That abort is the safety interlock: do not pass
`-AllowRunning` to hot-swap DLLs underneath live regions.

**Step 2 — dry run. This is the default mode and it writes nothing.**

```powershell
powershell -File H:\grid\tranquillity\tools\deploy-region-server.ps1 `
  -BundlePath H:\grid\staging\tranquillity-net10-v3 `
  -TargetHome H:\grid_final\opensim-base
```

Read the plan before continuing. It lists every add/update/unchanged file, what
it would back up, and prints `DRY RUN: no backup was taken, no file was
written.`

**Step 3 — apply.**

```powershell
powershell -File H:\grid\tranquillity\tools\deploy-region-server.ps1 `
  -BundlePath H:\grid\staging\tranquillity-net10-v3 `
  -TargetHome H:\grid_final\opensim-base -Force
```

`-Force` is the **only** thing that enables writes. The script takes its own
timestamped backup before the first destructive write and verifies that
backup's file count and byte total before proceeding.

**Step 4 — start the regions** via the supervisor, then **Step 0** again.

Other parameters: `-BackupRoot` (default `H:\grid\backups`).

### What `deploy-region-server.ps1` will and will not touch

It owns a narrow allow-list: root-level `.dll`, `.exe`, `.pdb`, `.json`,
`.config`, `.xml`, `.dat`, `.html`, `.htm`, `.txt`, `.sh`, `.png`, plus the
directories `lib64`, `runtimes`, and the 13 satellite resource directories
(`cs`, `de`, `es`, `fr`, `it`, `ja`, `ko`, `pl`, `pt-BR`, `ru`, `tr`,
`zh-Hans`, `zh-Hant`).

Alongside that allow-list there is an **independent deny-list**, so a file has
to pass both. It never touches:

- directories matching `^(regions|data|databases?|assets|SSL|bin|config-include|robust-include)$`
  (any path segment, case-insensitive)
- any `*.ini` or `*.ini.*` — including `OpenSim.ini.example`
- `ossl*.ini` (`osslEnable.ini`, `osslDefaultEnable.ini`)
- `log.config` and any `*.log` or `*.stat`

Note the managed assembly `Microsoft.Extensions.Configuration.Ini.dll` is a
legitimate DLL and is exempt from the `*.ini` rule.

### Post-copy assertions

After writing, the script asserts these and fails the run on any miss:

| Must be present | Purpose |
|---|---|
| `OpenSim.exe` | the renamed region host |
| `lib64\ubode.dll` | physics native |
| `lib64\BulletSim.dll` | physics native |
| `TasiaAddons.Quic.dll` | QUIC transport addon |
| `Microsoft.Data.Sqlite.dll` | |
| `Microsoft.Extensions.Caching.Memory.dll` | |
| `Phlox.ScriptEngine.dll` | the post-consolidation script engine assembly |

| Must be absent | Reason |
|---|---|
| `InWorldz.Phlox.dll` | retired — consolidated into `Phlox.ScriptEngine.dll` |

It also prints the runtime identity parsed out of
`OpenSim.runtimeconfig.json`, distinguishing self-contained
(`includedFrameworks`) from framework-dependent (`framework`) — useful evidence
that the net10 build actually landed.

## Verifying a deployment

`H:\grid\tranquillity\tools\verify-deployment.ps1` is read-only and safe to run
against production at any time. `-ProgramHome` is mandatory; `-DeployJson`
defaults to `H:\grid_final\generated\deploy.json` and `-RobustHome` to
`H:\grid\robust-net8`.

For every region in `deploy.json` it reports: process up, TCP listening on
`http_port`, log exists, line count, whether the log is still growing,
critical-pattern count, plus a single Robust-on-22000 check.

Critical patterns: `FATAL`, `Unhandled`, `DllNotFound`, `FileNotFound`,
`IndexOutOfRange`.

### Two traps the script had to work around

**(a) Serilog holds its log file exclusively.** Both `Get-Content` and
`[IO.File]::OpenText` fail with a sharing violation while a region is running.
The log must be opened with sharing enabled:

```powershell
$fs = [IO.File]::Open($logPath, 'Open', 'Read',
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$sr = New-Object IO.StreamReader($fs)
$text = $sr.ReadToEnd()
$sr.Close()
```

**(b) Log growth alone is NOT a crash signal.** A healthy region serving
viewers appends DEBUG lines continuously, and every region emits the hourly
`[DIAGNOSTICS]` heartbeat regardless of load. Conversely an *idle* healthy
region settles at roughly 1200–1300 lines and then goes quiet. So neither
growth nor stillness proves anything on its own.

The unambiguous crash-loop signature is **growth combined with more than one
startup banner in the same log file**. The script counts the canonical banner
`[STARTUP]: Beginning startup processing`, which is emitted exactly once per
process lifetime — so a count above one means the region restarted into a
crash loop. Counting several banner strings and dividing would over-count,
because a single startup emits all of them.

**An unreadable log is reported `INCONCLUSIVE`, never a pass.** A region whose
log could not be sampled is excluded from the healthy count and listed
explicitly.

## Phlox consolidation (shipped)

`InWorldz.Phlox` and `Phlox.ScriptEngine` were consolidated into **one**
project. `InWorldz.Phlox.dll` no longer exists anywhere in the tree;
`Phlox.ScriptEngine.dll` does. `deploy-region-server.ps1` enforces this as a
post-copy assertion, so a stale bundle carrying the old assembly fails the
deploy rather than shipping silently.

Merged into `tasia-ai/igrid_future:develop`:

| PR | Merge commit | Contents |
|---|---|---|
| `#4` | `8136082f4d` | the upstream Phlox 3-part series plus fork fixes `#203`, `#225`, `#228`, `#229`, `#230`, `#231`, `#232`, `#233` |
| `#5` | `cf6772b7d9` | the rest, landing `develop` on `cf6772b7d9` |

Upstream relationship, which is easy to get wrong when re-reading the PR list:

- `#224` is **already included by `#226`**.
- From `#227` only **16 files of part 3** were taken:
  `Tests/InWorldz.Phlox.Tests/Golden/**`, `IwStringCodecGoldenTests.cs`,
  `IwStringCodecTests.cs`.
- `#227` was deliberately **not** merged wholesale.
- Fork PRs `#173` and `#174` were applied manually and deliberately skipped as
  PRs.

### Engine default: YEngine

**YEngine is the default engine.** `DefaultScriptEngine = "YEngine"` is set in
`OpenSimDefaults.ini` (line 304) and in all 31 `regions\<name>\OpenSim.ini`
files — verified 31 of 31. Phlox remains available and opt-in via
`//InWorldz.Phlox:` script directives. Fork PR `#233` provides the two-engine
state handoff that makes switching safe.

This supersedes the recommendation in
[PhloxYEngineConsolidation.md](PhloxYEngineConsolidation.md), which is a
2026-09-07 discussion note arguing the runtimes should not be merged and that
YEngine should eventually be retired in favour of Phlox. That analysis was
never implemented and is now contradicted on both counts: the assemblies were
consolidated, and the shipping default is YEngine. Read it as history, not as
guidance.

## Known duplicate history — do not "fix"

Upstream PR `#207` appears **twice** in history, as commits `92dbfbbb2a` and
`704127830c`. Identical `patch-id`, identical trees.

This is upstream history noise, not a duplicated change. Do not rewrite
history to remove it.

## Daemon-mode console bug (fixed, deployed)

This one cost hours. `Source/OpenSim.Framework.Console/LocalConsole.cs` crashed
whenever a supervisor redirected the region's standard streams — which is the
normal way the manager runs a region.

Two distinct failures:

1. `Console.TreatControlCAsInput = true` threw
   `IOException: The handle is invalid` during region startup.
2. The interactive prompt loop polled `Console.KeyAvailable`, which throws
   `InvalidOperationException` when input is redirected. The caller caught the
   exception and retried, so this produced **roughly 8 million log lines in
   about 10 minutes**.

The fix, all deployed in `OpenSim.Framework.Console.dll` on 2026-10-02 21:53:

- set `TreatControlCAsInput` only when `!Console.IsInputRedirected`
  (`LocalConsole.cs:178-179`)
- guard the cursor and buffer APIs behind a `HasConsole` property
  (`LocalConsole.cs:49`, used at `:213`, `:256`, `:286`, `:312`, `:335`,
  `:393`, `:502`, `:561`, `:563`)
- when there is no console, `ReadLine` reads `Console.In.ReadLine()` directly
  (`LocalConsole.cs:536-540`) — which is also how the manager feeds console
  commands to a region

**Diagnostic lesson:** a log that is *still growing minutes after startup*
means a crash loop, not a healthy region. Read this together with the
verification trap above: the growth is the hint, the repeated startup banner
is the proof. A healthy region reaches about 1200–1300 lines and then stops.

## Database migration applied

Each region has its own PostgreSQL database on `127.0.0.1:5433`, named
`sim_<region>` — 31 of them. The exact name for each region is the `database`
key of its `deploy.json` entry. Primitives live in the `[SimulationDataStore]`
database, **not** in the `robust` database.

Two schema changes were needed:

1. **`[EstateDataStore].primitems` was ADDED** — the column did not exist.
2. **`experienceID` case fix across all 31 `sim_*` databases.** The column
   existed, but spelled lowercase `experienceid`, while the code expects
   `experienceID`. This caused
   `IndexOutOfRangeException: Field not found in row: experienceID` on region
   load. Fixed by renaming to exact-case `experienceID` in all 31.

### Operational gotcha: PowerShell strips the quotes

PowerShell strips double quotes when passing `-c` to `psql`, which silently
breaks a quoted identifier. The migration therefore had to be run from a SQL
file:

```sql
-- H:\grid\staging\fix-experienceid-case.sql
ALTER TABLE primitems RENAME COLUMN "experienceid" TO "experienceID";
```

Verify it, and remember that `attname` comparison in PostgreSQL is
**case-sensitive** — which is exactly why this bug looked like "column
missing" rather than "column misnamed":

```sql
SELECT attname FROM pg_attribute
 WHERE attrelid = 'primitems'::regclass
   AND attname = 'experienceID';
```

That must return exactly one row.

## Backups

All still on disk.

| Path | What it is |
|---|---|
| `H:\grid\backups\programhome-bin-20261002-194326` | **the binary rollback path** — full pre-cutover program home, 611 files, 176,687,235 bytes, hashes verified |
| `H:\grid\backups\primitems-schema-alldb-20261002-202353` | schemas of all 31 `sim_*` databases (31 files) |
| `H:\grid\backups\scriptengine-yengine-20261002-190652` | the 31 region INIs from before YEngine was set as default (31 files) |

To roll back binaries: stop the regions and copy the backup tree back over
`H:\grid_final\opensim-base`. The backup mirrors the allow-listed relative
paths of the program home — it does **not** contain `regions\`, `data\`,
`assets\` or `SSL\`, and does not need to, because the deploy never wrote them.

Staging bundles:

- `H:\grid\staging\tranquillity-net10-v3` — the deployed bundle. v1 and v2 are
  obsolete.
- `H:\grid\staging\manager-v2` — the current manager build, **not yet swapped
  into** `H:\grid\FreshMetaverseManager`.

## Open issue: QUIC transport — NOT fixed

`TasiaAddons.Quic.QuicServerModule` loads and attaches to the scene, but on the
net10 build **no UDP listener appears on port 22208**. This is open. Do not
read anything below as a resolution.

### Why the silence is confusing

The deployed addon creates its logger with log4net — at commit `cf6772b7d9`,
`Addons/TasiaAddons.Quic/QuicServerModule.cs:66` is:

```csharp
private static readonly ILog m_log = LogManager.GetLogger(...);
```

The .NET 10 host **never configures log4net**.
`Source/OpenSim.Server.Base/Hosting/Log4NetBootstrapper.cs:29` calls
`XmlConfigurator.Configure(...)`, but that class has **no production caller** —
grepping the tree finds it referenced only by itself and by
`Tests/OpenSim.Server.Base.Tests/Hosting/Log4NetBootstrapperTests.cs`.

So log4net has zero appenders, and **every `m_log` call in the addon was
silently discarded** — including every failure path. The only wired-up sink is
Serilog via `Microsoft.Extensions.Logging`.

**A missing log line therefore proves nothing about configuration.** This is
the single most misleading thing about the bug.

It follows that every log4net-based addon in the tree is equally silent — 23
files under `Addons/` and 11 under `Source/` still call `LogManager.GetLogger`.
If an addon seems to be doing nothing, check which logging API it uses before
concluding anything.

The real fix is one of:

- call `Log4NetBootstrapper.Configure()` at host startup, or
- migrate the remaining addons to `ILogger`.

The working tree already carries the second option for the QUIC addon —
`QuicServerModule` now uses `LoggerProvider.CreateLogger` (`ILogger`) with a
comment recording this exact diagnosis. **It is uncommitted and not deployed.**
The deployed `TasiaAddons.Quic.dll` still uses log4net (built 2026-10-02
18:27, before the 23:56 source edit), so this change fixes the silence for
that one module only once built and deployed. It does not fix the missing UDP
listener.

### Region configuration

`[ClientStack.Quic]` in each `regions\<name>\OpenSim.ini`:

```ini
[ClientStack.Quic]
Enabled = true
Port = 22208
AdvertiseHost = "os.tasia.work.gd"
AdvertisePort = 22208
ALPN = opensim-ll/1
ProxyRegistrationURL = http://127.0.0.1:22001/admin/quic/circuit
BrainURL = http://127.0.0.1:19002
AllowLegacyLLUDP = true
```

Certificate: `SSL\quic\quic-cert.pem` (present). HTTPS on 443 has a valid
Let's Encrypt certificate for `CN=os.tasia.work.gd` expiring 2026-12-13, so
**TLS is not the problem** — do not go there first.

Two further red herrings:

- **`AdvertiseInEventQueue` is a dead key.** No code in the tree reads it, so
  changing it has no effect whatsoever. It was set to `false` temporarily on
  `Fresh01` while diagnosing; that change did nothing and can be reverted or
  left alone on its own merits.
- **`QUIC=` is always empty in `Robust.log`.**
  `LLLoginResponse.SimQuicHost` / `SimQuicPort` in Robust are populated by
  nothing — the source comment admits it — so the absence of a QUIC entry
  there is expected and is not evidence of a fault. (This matches the
  "Still not wired up" paragraph in the `TasiaAddons.Quic` section above.)

Meanwhile **LLUDP fallback (`AllowLegacyLLUDP = true`) is what carries
viewers**, so the grid is fully functional without QUIC.

## 502 / Bad Gateway — actual cause was regions being down

Viewers logged:

```
cannot POST url 'http://os.tasia.work.gd:22341/CE/...' because Bad Gateway
```

and could not log in.

**This was not a certificate or QUIC problem.** Port 22341 is the HTTP port of
region `Dark_Secrets`. The viewer was being handed a capability URL for a
region that was **down**, because 30 of the 31 regions were stopped for the
cutover. The 502 came from the reverse proxy having no upstream. Once all 31
regions were started, the symptom disappeared on its own.

**Lesson:** when a viewer reports 502 or a connection error, check which
regions are actually *listening* before suspecting TLS, certificates or QUIC.

## Test baseline

Measured on the merged PR #4/#5 tree by remote build, before the final
uncommitted fixes.

| Suite | Result |
|---|---|
| Remote build of PR #4/#5, Release | 0 Error(s) |
| Remote build of PR #4/#5, Debug | 0 Error(s) |
| Phlox | 5042 passed, 0 failed, 2 skipped |
| CoreModules | 214 passed, 0 failed |
| Framework | 161 passed, 0 failed |

Known-failing, and not regressions from the consolidation:

- Two Phlox tests (`GrantRestoreTests...`, `PermissionLifecycleTests...`) are
  flaky / order-dependent. Confirmed by A/B against the pre-consolidation
  tree.
- `OpenSim.Region.ScriptEngine.Tests` after `#231`: the NU1605 restore error is
  fixed, but 4 tests still fail — 2 network/region-dependent, and 2
  pre-existing LSL float-precision issues.
