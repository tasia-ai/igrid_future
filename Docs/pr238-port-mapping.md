# Port of upstream PR #238 — mapping and status

Upstream PR #238 (5 commits, JohnLegionH, 2026-10-03) fixes Phlox grant/state
persistence. It cannot be merged: our consolidation (PR #5) renamed the
directories it edits. This documents the exact mapping so the port can be
completed without re-deriving it.

## Commit status

| Commit | Subject | Status |
|---|---|---|
| `24c2f06673` | retry state copy 10x | PORTED (adapted: our tree has no `Copy<T>` helper, so `Capture` retries `FromRuntimeState`) |
| `f55fbee745` | CaptureFailed + move aside | PORTED (adapted: uses our existing `MoveAside`/`RowsMovedAside` infrastructure) |
| `f3916f43b3` | grant survives derez | BLOCKED (see below) |
| `cc6fc1391a` | docs: waiting permissions, retries | PENDING (trivial, do last) |
| `30d68280c1` | docs: row keeps grant | PENDING (trivial, do last) |

## Why f3916f43b3 is blocked

It needs grant-model fields our tree never got (they came from upstream #226,
which our merge dropped):

| Upstream field | Ours | Notes |
|---|---|---|
| `RuntimeState.PermsGranter` (string) | HAVE (line 207) | same |
| `RuntimeState.GrantedPermsMask` (int) | HAVE (line 212) | same |
| `RuntimeState.PermsOwner` (string) | HAVE (line 219) | same |
| `RuntimeState.PermsUnverified` (bool) | MISSING | gate for clearing unverified claims |
| `RuntimeState.PermsExperience` (string) | MISSING | Experience grant source |
| `RuntimeState.ExperienceGranter` (string) | MISSING | used by NoteItemGrant |
| `RuntimeState.ExperienceGrant` (string) | MISSING | used by NoteItemGrant |
| `SerializedRuntimeState.*` mirrors | MISSING | serialize/deserialize + wire |

`NoteItemGrant` / `NoteGrant` / `ClearSavedGrant` exist in ours (LSLSystemAPI.cs
832/838/845) with fewer parameters. Upstream's versions take `experience` and
`unverified`. Only 2 callers of `NoteItemGrant`, 5 of `ClearSavedGrant`, 5 of
private `NoteGrant` — signature change is contained.

`GrantChanged` (ours LSLSystemAPI.cs:2548) and `NoteGrantForRow` (ours
PhloxEngine.cs:1101) both exist and match upstream's pre-#238 shape, so the
logic port is mechanical once the fields exist.

## Do NOT do a partial port

Porting `GrantChanged`/`NoteGrantForRow` without `PermsUnverified` creates a
third variant: rows would keep unverified carried claims as grants. That is
worse than the current behavior for that edge case. Either bring the full
grant model (fields + NoteGrant/ClearSavedGrant signatures + carry path at
LSLSystemAPI.cs:~946/955) or leave it.

## Test updates pending (from f3916f43b3)

`GrantRestoreTests.cs` (+136), `SchedulerHarness.cs` (+16),
`StateSaveThreadingTests.cs` (+2), `TerminatedScriptStaysStoppedTests.cs` (+2).
Apply after the source port, then run the full Phlox suite.
