# Crash Recovery Journal — Addon Module Plan

## Problem
Objects rezzed/modified in-world can be lost during hot restart or crash because the normal
OpenSim persistence timer (60s min / 10min max) hasn't fired yet.

## Solution
A self-contained addon module that journals object changes to local files immediately.
On crash recovery, the operator manually restores from the journal via console commands.

**Zero core OpenSim changes.** 100% addon APIs.

---

## Architecture

### Event Hooks (all public, no core changes)

| Event | Purpose |
|---|---|
| `OnObjectAddedToScene` | Detect new objects entering the scene |
| `OnObjectBeingRemovedFromScene` | Detect deleted objects |
| `OnSceneObjectPartUpdated` | Detect property changes (position, name, etc.) |
| `OnSceneObjectPreSave` | Detect pre-DB-save (copy + original provided) |
| `OnPrimsLoaded` | Know when initial DB load is complete |
| `OnRegionReadyStatusChange` | Know when region is fully ready |

### Serialization (all public)

| Method | Purpose |
|---|---|
| `SceneObjectGroup.ToXml2()` | Serialize object to XML string |
| `SceneObjectSerializer.FromXml2Format(xml)` | Deserialize object from XML |
| `Scene.AddRestoredSceneObject(sog, true, false)` | Restore object to scene |
| `Scene.DeleteSceneObject(sog, true)` | Remove object from scene |

---

## Console Commands

| Command | Description |
|---|---|
| `journal status` | Show count of pending objects per region |
| `journal preview` | List pending objects with name, position, creator |
| `journal restore` | Restore missing objects from journal to scene + DB |
| `journal flush` | Force save all journal entries to DB immediately |
| `journal clear` | Discard journal entries without restoring |

## Config (INI section)

```ini
[CrashJournal]
; Master toggle — set to false to completely disable
Enabled = true

; Directory for journal files (relative to sim bin/)
JournalPath = Data/crash-journal

; How often to write journal (seconds) — debounces rapid changes
WriteInterval = 5

; Max journal file size per region (MB) — oldest entries pruned when exceeded
MaxJournalSizeMB = 50

; Auto-clear journal entries older than this (hours)
; 0 = never auto-clear
MaxJournalAgeHours = 24
```

## File Format

Each region gets its own journal file:
```
Data/crash-journal/{regionID}.journal
```

Journal entry format (JSON lines — one object per line):
```json
{
  "uuid": "object-uuid",
  "name": "Object Name",
  "position": "128,128,25",
  "creator": "User Name",
  "timestamp": "2026-07-16T01:30:00Z",
  "action": "update|create|delete",
  "xml": "<xml2>...</xml2>"
}
```

For `delete` actions, `xml` is null — just the UUID is enough.

## Flow

### Runtime (normal operation)
1. Object changes → `OnSceneObjectPartUpdated` fires → debounce timer starts
2. After `WriteInterval` seconds of no changes → write object XML to journal file
3. Normal OpenSim `OnBackup` fires → object persisted to MySQL → remove from journal
4. Object deleted → `OnObjectBeingRemovedFromScene` → write delete entry to journal
5. On successful DB persist of delete → remove delete entry from journal

### Crash Recovery
1. Sim crashes → journal files remain on disk
2. Sim restarts → addon loads, reads journal files
3. **Does NOT auto-restore** — sits quietly
4. Operator runs `journal status` → sees pending objects
5. Operator runs `journal preview` → inspects what's there
6. Operator runs `journal restore` → objects restored to scene + DB
7. Operator runs `journal clear` → journal discarded

## Key Files to Create

| File | Purpose |
|---|---|
| `addon-modules/TasiaAddons.CrashJournal/CrashJournalModule.cs` | Main region module |
| `addon-modules/TasiaAddons.CrashJournal/CrashJournalWriter.cs` | Journal file I/O |
| `addon-modules/TasiaAddons.CrashJournal/CrashJournalConfig.cs` | Config loader |
| `addon-modules/TasiaAddons.CrashJournal/TasiaAddons.CrashJournal.csproj` | Project file |

## Project References (same as MACAudit addon)

- OpenSim.Framework.dll
- OpenSim.Region.Framework.dll
- OpenSim.Services.Interfaces.dll
- OpenMetaverse.dll
- OpenMetaverseTypes.dll
- log4net
- Mono.Addins

## Assembly Attributes

```csharp
[assembly: Addin("TasiaAddons.CrashJournal", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", "0.9")]
```

## Safety Considerations

- **Never auto-restore** — always requires manual console command
- **Debounce writes** — don't write on every tiny change, batch by WriteInterval
- **Journal size limits** — prune oldest entries when MaxJournalSizeMB exceeded
- **Age limits** — auto-clear entries older than MaxJournalAgeHours
- **Atomic writes** — write to temp file, then rename to avoid corruption
- **Thread-safe** — use ConcurrentDictionary for tracking dirty objects

## Deployment

1. Build `TasiaAddons.CrashJournal.dll`
2. Copy to all sim `bin/` directories
3. Add `[CrashJournal]` section to each sim's config (or shared config-include)
4. Restart sims (one-time, for addon loading)
5. Done — journal is active, no further restarts needed

---

## TODO

- [ ] Build CrashJournalModule.cs
- [ ] Build CrashJournalWriter.cs
- [ ] Build CrashJournalConfig.cs
- [ ] Build project file + assembly attributes
- [ ] Test locally
- [ ] Deploy to server
- [ ] Test hot restart recovery
