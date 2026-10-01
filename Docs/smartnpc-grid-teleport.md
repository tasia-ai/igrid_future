# SmartNPC grid-wide teleport — findings and options

Status: **researched, not implemented.** Intra-region teleport already works
today with zero core changes. Cross-region and Hypergrid are both new protocol
work, documented here so the decision is informed rather than guessed at.

Source of the feature: the Amber/i-Grid fork at `H:\grid\work\fixtest`
(`OpenSim/Region/OptionalModules/AI/OpenSimAIModule.cs`). There is a second,
older copy at `addon-modules/OpenSimAIModule/src/OpenSimAIModule.cs` that has
**no** teleport function at all — the in-tree one is the newer copy and is what
the port should follow.

## Script surface today

| Function | Line | Notes |
|---|---|---|
| `osCreateSmartNPC` | 366 | |
| `osSetSmartNPC` | 449 | |
| `osSetSmartNPCProfile` | 477 | |
| `osSetSmartNPCAppearance` | 511 | |
| `osTeleportSmartNPC` | 526 | **same-region only** |
| `osNpcInstantMessage` | 552 | |
| `osAI` | 576 | |

A SmartNPC is an ordinary `ScenePresence` driven by a fake `IClientAPI`
(`NPCAvatar : IClientAPI, INPC`), created through `INPCModule.CreateNPC`.
i-Grid gives it a deterministic UUID
(`Util.ComputeASCIISHA1UUID("igrid-virtual-avatar:" + name)`), which is what
makes delete-and-recreate at the destination semantically safe.

## What works today, no changes needed

`osTeleportSmartNPC` calls `IEntityTransferModule.Teleport(...)`. Because the
destination handle equals the current region, `Teleport` always takes the
`TeleportAgentWithinRegion` branch, which only does `SendTeleportStart` /
`SendLocalTeleport` (both no-ops on `NPCAvatar`), `RotateToLookAt`, zero the
velocity and `sp.Teleport(position)`. Fully NPC-safe.

The same-region limit is a single line — `OpenSimAIModule.cs:538`:

```csharp
if (targetPresence.Scene != npcScene) return 0;
```

## Why cross-region does not just work

Not a permission or a flag. Three independent blockers, each with code:

**1. The destination only materialises an agent when a viewer sends
`UseCircuitCode` over UDP.** `Scene.WaitGetScenePresence` (`Scene.cs:4764`)
polls for 30 seconds and `Scene.cs:4678` says so in a comment. The only
`ScenePresence` factory is `SceneGraph.CreateAndAddChildScenePresence`, whose
only caller is `Scene.AddNewAgent`, which is reachable only from
`LLClientView.Start()`, `IRCClientView`, and local `NPCModule.CreateNPC`.
There is no wire path that puts an NPC in a remote region.

**2. Presence verification rejects the NPC first.** `NPCModule.CreateNPC`
builds an `AgentCircuitData` with `SessionID` left at `UUID.Zero`
(`NPCModule.cs:194-202`). `LocalSimulationConnector.CreateAgent` calls
`Scene.NewUserConnection(aCircuit, ...)` — and the 4-argument overload
(`Scene.cs:3981`) always passes `requirePresenceLookup: true`, so
`VerifyUserPresence` looks up `presencesvc.GetAgent(agent.SessionID)`, gets
null, and denies.

> **There is already a purpose-built escape hatch that nobody wired up.**
> `Scene.cs:4005`:
> ```csharp
> /// <param name="requirePresenceLookup">True for normal presence. False for NPC
> /// or other applications where a full grid/Hypergrid presence may not be required.</param>
> ```
> `public bool NewUserConnection(AgentCircuitData acd, uint teleportFlags,
>     GridRegion source, out string reason, bool requirePresenceLookup)`
> at `Scene.cs:4012`. Grepping `requirePresenceLookup` across `Source/` returns
> only its own declaration and definition — the 5-argument overload is dead
> code. The same dead code exists in i-Grid.

**3. `RequestClientInfo()` returns an empty circuit.** `NPCAvatar.cs:764`
returns `new AgentCircuitData()`, so `SessionID`, `IPAddress`, `Viewer` and
`ServiceURLs` are all null — and `DoTeleportInternal` trusts that empty object
rather than the real circuit (`EntityTransferModule.cs:800-820`).

## Hypergrid is further out

`HGEntityTransferModule.CreateAgent` requires `agentCircuit.ServiceURLs` to
contain `HomeURI` (`HGEntityTransferModule.cs:270`). The NPC circuit's
`ServiceURLs` is an **empty dictionary**, so the check fails and the transfer is
refused before it starts.

On top of that, `UserManagementModule.AddNPCUser` registers NPCs as local grid
users, so `IsLocalGridUser(npcUUID)` is true and `LevelHGTeleport` gating
applies to NPCs as well — an NPC with UserLevel 0 is refused with
"Hypergrid teleport not allowed".

There is also **no wire representation of "this agent is an NPC"**: neither
`AgentCircuitData`, `AgentData` (`ChildAgentDataUpdate.cs:327`) nor
`AgentDestinationData` carries an NPC flag, and every bit of `teleportFlags` is
already spoken for.

Landmark paths cannot be borrowed either — they explicitly exclude NPCs:
`EntityTransferModule.cs:1419` and `HGEntityTransferModule.cs:557` both start
with `if (sp == null || sp.IsDeleted || sp.IsInTransit || sp.IsChildAgent || sp.IsNPC) return;`.

## If it is to be built

The precedent to copy is prim crossing, which already solves the same problem
for objects: `ISimulationService.CreateObject` → `LocalSimulationConnector` /
`RemoteSimulationConnector` → `Scene.IncomingCreateObject`. There is no NPC
equivalent.

Ordered steps:

1. **Unblock `CreateAgent`.** Add `bool IsNpc` to `AgentCircuitData`, serialise it
   alongside `teleportFlags`, and have `LocalSimulationConnector` call the
   5-argument `NewUserConnection(..., requirePresenceLookup: !aCircuit.IsNpc)`.
   In `DoTeleportInternal`, build the outgoing circuit from
   `currentAgentCircuit` when `sp.IsNPC` instead of from `RequestClientInfo()`.
2. **Destination-side landing.** Add `Scene.IncomingCreateNpcAgent(...)` that
   calls `INPCModule.CreateNPC` with the same UUID, then `CompleteMovement`,
   `UpdateChildAgent`, and clears `IsChildAgent`.
3. **Transport.** A new `ISimulationService.CreateNpcAgent` plus the local and
   remote connectors and the HTTP handler in `Simulation/AgentHandlers.cs`.
4. **New entry point** `IEntityTransferModule.TeleportNpc(...)`, reusing
   `GetTeleportDestinationRegion`, `GetFinalDestination` and
   `checkAgentAccessToRegion` — the last of which touches no `ControllingClient`,
   which is why it is the right pre-flight for a server-side teleport. Delete at
   the source with `NPCModule.DeleteNPC` *before* creating at the destination, so
   one UUID is never live in two regions.
5. **Hypergrid override** in `HGEntityTransferModule`, using the existing
   `MakeGateKeeperRegion` + `GatekeeperServiceConnector.GetHyperlinkRegion`.
   Either mint a synthetic `HomeURI`/`ServiceSessionID` per SmartNPC and get the
   home grid to accept a session with no presence, or introduce an NPC-specific
   travel token — which is a breaking change on **both** grids.

Steps 1-3 are medium effort. Step 5 is a cross-grid protocol change and should
not be attempted without both grids' operators agreeing.

## Also note for the port

`INPCProfileMembership` (used by `ApplySmartNpcProfile`, i-Grid
`OpenSimAIModule.cs:1040`) **does not exist** in Tranquillity's `INPCModule` —
only `profileAbout`, `profileImage` and `Born`. `SceneManager.TryGetScenePresence`
does exist. `RegisterScriptInvocations` / `IScriptModuleComms` are unchanged.
