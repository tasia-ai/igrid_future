# TasiaNGC OpenSim — Tranquillity .NET 10

<img src="https://i.let-us.cyou/wp-content/uploads/2026/03/image-768x1152.png" alt="Tasia" height="180" align="right" />

**TasiaNGC** — OpenSim fork of [NGC Tranquillity](https://github.com/OpenSim-NGC/OpenSim-Tranquillity) with Tasia's addons and QUIC viewer transport, migrated to **.NET 10**.

> **Branch `tranquillity/hg-travel-net10`** — the .NET 10 migration of the i-Grid fork onto clean Tranquillity. Builds clean: 110 projects, 0 errors.

---

## License and Usage Approval (Important)

- See **[LICENSE.txt](./LICENSE.txt)** for full terms.
- Upstream/OpenSim components keep their original open-source licenses.
- **Tasia-authored additions/modifications require visible attribution, sharing modifications back, and prior written approval for usage (including public-source usage).**

---

## What is on this branch

Started from `OpenSim-NGC/OpenSim-Tranquillity` at `148896caec`. Your previous i-Grid
work is preserved on the `backup/develop-local-40commits` branch and on
`test-2026-09-28`.

| | |
|---|---|
| **Merged PRs** | #204 (map renderer), #173 (mesh name), #174 (appearance reset), #208 (`/homeagent` travel session/token) |
| **Addons ported** | 16, to `Addons/`, net10.0, registered in `Tranquillity.sln` |
| **QUIC** | ported with the core transport support it needed — see below |
| **Docs** | [Docs/port-notes.md](Docs/port-notes.md), [Docs/smartnpc-grid-teleport.md](Docs/smartnpc-grid-teleport.md) |

### QUIC

`IViewerTransport` and `QuicCircuitRegistry` are new in `OpenSim.Framework`;
`LLUDPClient` gained an optional per-circuit transport, and `LLUDPServer` a QUIC
region. The send path is ~10 lines because `SendPacketFinal` is the single choke
point every viewer packet already passes through.

**Not runtime-tested** — it compiles and the plain UDP path is provably
unaffected, but the QUIC path needs a QUIC viewer. Endpoint discovery is also
not wired up: `LLLoginService` does not populate `SimQuicHost`/`SimQuicPort`, and
`GridRegion` has no `QuicHost`/`QuicPort`, so viewers cannot yet find the
endpoint through login or the grid.

### SmartNPC grid teleport

Intra-region teleport works today with no core changes. Cross-region and
Hypergrid do not, and are not permission or flag problems — the destination only
creates a presence when a viewer sends `UseCircuitCode`. Full analysis, the
blockers with code references, and a build plan are in
[Docs/smartnpc-grid-teleport.md](Docs/smartnpc-grid-teleport.md).

---

## Tasia AI Addons

All addons build as DLLs under `Addons/` and register through
`IPluginRegistryProvider`. Region modules still need to be named in the region's
`[RegionModules]` config section, as with any Tranquillity module.

| Addon | What it does |
|---|---|
| **AccessLogger** | Login audit with IP, UUID, MAC, hardware ID, grid URI |
| **LoginSecurity** | IP/hardware banning, ToS prompt |
| **ChatAudit** | Full chat/IM logging |
| **MACAudit** | Hardware audit logging |
| **RemoteSound** | Play remote audio URLs via script |
| **Marketplace** | Prim delivery API |
| **WoWonder** | Social network integration |
| **AlertNotifications** | Push notifications |
| **RestartModule** | Region restart with dialogs |
| **AbuseReports** | In-world abuse reporting |
| **FriendConference** | Group/instance voice conferencing |
| **MetricsConnector** | Region metrics export API |
| **SharedInventory** | Shared inventory service |
| **JoinApi** | `/1-join` account registration endpoint (Robust `[ServiceList]`) |
| **Quic** | QUIC viewer transport with central circuit proxy |

`TasiaAddons.Abstractions` and `Source/Tasia.Extensions.SDK` are shared
libraries, not modules.

Full addon documentation: **[ADDONS.md](Addons/TasiaAddons/ADDONS.md)**

### Module registration matters

Tranquillity retired Mono.Addins — `PluginManager` is a stub. A `[Extension]`
attribute still compiles and is then silently ignored, so an addon that only has
that builds, ships, and never starts. Every region module here therefore carries
a `PluginRegistration.cs`; if you add one, do the same.

---

## Building

Needs the **.NET 10 SDK** (`global.json` pins `10.0.110`, `rollForward: latestMajor`):

```
dotnet build Tranquillity.sln -c Release
```

`Addons.slnx` builds just the addons without the ~90-project core, which is much
faster when iterating on an addon.

---

## Upstream Tranquillity

Tranquillity is a BSD-licensed OpenSimulator derivative using up-to-date C# and
.NET architectural patterns. See [Docs/BUILDING.md](Docs/BUILDING.md) for build
detail and <http://opensimulator.org> for the wider project.

*This is considered an alpha release. Some stuff works, a lot doesn't. If it
breaks, you get to keep both pieces.*
