# Shared Inventory (Planned + Changed)

## What is implemented now

A new addon module was added:

- `addon-modules/TasiaAddons.SharedInventory/SharedInventoryModule.cs`
- `addon-modules/TasiaAddons.SharedInventory/TasiaAddons.SharedInventory.csproj`
- config example: `bin/config-include/SharedInventory.ini.example`

Current behavior:

1. Uses a configured **shared avatar UUID** as the backend owner of the master shared folder.
2. Creates/uses a master folder under shared avatar inventory:
   - `RootFolderName` (default: `Shared Inventory`)
3. For each online user, creates/uses local user folders in their own inventory:
   - `My Inventory/Shared RW/Upload`
   - `My Inventory/Shared RW/Shared`
4. On sync cycle:
   - Moves user contributions from `Upload` to shared avatar master folder.
   - Mirrors shared avatar master content into user `Shared` folder.
5. Sync runs:
   - every `SyncIntervalSeconds` (default `300` = 5 minutes)
   - on login when `SyncOnLogin = true`
6. Admin can trigger sync with region console commands:
   - `sharedinventory sync all`
   - `sharedinventory sync online`
   - `sharedinventory sync user <avatar-uuid>`


## How this is intended to work (design)

### Goal
Provide a shared inventory experience from viewer-side user inventory, without requiring a custom viewer.

### Practical model
- There is one backend source of truth (shared avatar inventory folder).
- Each user gets local synchronized folders under their own inventory.
- Upload path is writable by users (`Upload`).
- Shared view is synchronized into user inventory (`Shared`).

This gives a native viewer workflow (drag/drop in own inventory) while keeping centralized control.


## Configuration summary

`[SharedInventory]`

- `Enabled`
- `SharedAvatarUUID`
- `ManagerUUIDs` (currently reserved for future permission expansion)
- `RootFolderName`
- `UserRootFolderName`
- `UserUploadFolderName`
- `UserBrowseFolderName`
- `SyncIntervalSeconds`
- `SyncOnLogin`
- `RegionCommandEnabled`


## Completed in this cycle (production hardening)

1. **Manager moderation controls (implemented)**
   - manager UUID enforcement is active (`ManagerUUIDs`)
   - moderation commands implemented:
     - console: `sharedinventory moderate <manager-uuid> approve|reject item|folder <uuid>`
     - manager chat: `/77 sharedinv approve|reject item|folder <uuid>`
   - optional approval queue enabled by config:
     - `RequireManagerApproval = true`
     - uploads go to `PendingFolderName`

2. **Safer sync semantics (implemented)**
   - duplicate/conflict policy implemented:
     - `UploadDuplicatePolicy = skip|rename|replace`
     - `MirrorDuplicatePolicy = skip|rename|replace`
   - per-cycle conflict stats tracked (skip/replace/rename counters)

3. **Library integration path (implemented as operator-friendly publish snapshot)**
   - optional publish marker path under shared root:
     - `PublishLibrarySnapshot = true`
     - `PublishFolderName = Library Publish`
   - keeps RW upload flow in user inventory unchanged
   - note: direct runtime mutation of global Library tree is not provided by standard `ILibraryService` API, so snapshot/export path is used for safe operator workflows

4. **Operational controls (implemented)**
   - sync scopes implemented:
     - `full`, `uploads`, `mirror`
   - runtime controls implemented:
     - `sharedinventory set dryrun on|off`
     - `sharedinventory set verbose on|off`
   - optional manager in-world sync command:
     - `/77 sharedinv sync full|uploads|mirror [all]`

5. **Reliability + audit (implemented)**
   - cycle metrics logged:
     - duration, users processed, moved/copied counts, skip/replace/rename counts, errors, dry-run mode
   - cumulative stats command:
     - `sharedinventory stats`


## Notes / constraints

- A single literal globally shared RW folder object for all users is not native in stock viewers.
- Current solution uses synchronized per-user folders to achieve equivalent user experience.
- Sync currently processes online users (plus explicit targeted user command).


---

## Friends Conferences flow (check + plan to make it 100% reliable)

### Current flow status in this codebase

From code review:

- Basic IM delivery uses:
  - `OpenSim/Region/CoreModules/Avatar/InstantMessage/InstantMessageModule.cs`
  - `OpenSim/Region/CoreModules/Avatar/InstantMessage/MessageTransferModule.cs`
  - `OpenSim/Region/CoreModules/Avatar/InstantMessage/HGMessageTransferModule.cs`
- Group chat sessions use:
  - `OpenSim/Addons/Groups/GroupsMessagingModule.cs`
  - `OpenSim/Region/OptionalModules/Avatar/XmlRpcGroups/GroupsMessagingModule.cs`

Important finding:

- Core `InstantMessageModule` only forwards a limited dialog set (normal IM / typing / object / busy).
- Session-style dialogs (`SessionGroupStart`, `SessionAdd`, `SessionDrop`, `SessionSend`) are handled by **groups messaging modules**, not by core IM flow.
- Existing implementation is robust for **group conferences**, but not complete as a standalone **friends ad-hoc conference service**.


### Required config for stable group conference behavior

Use and verify:

1. `[Messaging]`
   - `InstantMessageModule = InstantMessageModule`
   - Hypergrid: `MessageTransferModule = HGMessageTransferModule`
   - Local-only: `MessageTransferModule = MessageTransferModule`
2. `[Groups]`
   - `Enabled = true`
   - `Module = GroupsModule`
   - `MessagingEnabled = true`
   - `MessagingModule = GroupsMessagingModule`
   - `MessageOnlineUsersOnly = true` (**required by current GroupsMessagingModule implementation**)
3. Hypergrid robust side
   - `[HGInstantMessageService]` must be active and correctly wired to Presence/Grid/UserAgent services.
4. `MessageKey`
   - if used, must match across all regions/services exchanging XMLRPC IM traffic.


### Why conferences fail in practice

Main failure points to eliminate:

1. Wrong module mix (e.g. groups enabled but messaging module not active).
2. `MessageOnlineUsersOnly` false with V2 groups messaging (module self-disables).
3. IM transfer mismatch (wrong transfer module for HG vs local).
4. Presence stale/missing (user moves region, stale cache).
5. Session tracking resets on crossings/relog causing delayed re-invite behavior.


### Implemented now

New addon module implemented:

- `addon-modules/TasiaAddons.FriendConference/FriendConferenceModule.cs`
- `addon-modules/TasiaAddons.FriendConference/TasiaAddons.FriendConference.csproj`
- config example: `bin/config-include/FriendConference.ini.example`

Implemented behavior:

1. Ad-hoc friend conference session broker (in-memory)
   - tracks session owner/members/last activity
2. Dialog handling wired
   - `SessionGroupStart`, `SessionAdd`, `SessionDrop`, `SessionSend`
3. Membership rules
   - optional friendship gate (`RequireFriendship = true`)
4. Delivery strategy
   - local client delivery when present
   - fallback through `IMessageTransferModule` for cross-region/grid route
5. Safety limits
   - `MaxParticipants`
   - `SessionIdleSeconds` cleanup
   - `InviteThrottleMs` anti-spam


### Plan to make friends conferences 100%

Implement **FriendConferenceModule** (new addon), separate from group sessions:

1. Session broker
   - track ad-hoc sessions (`session_id`, owner, members, last activity)
   - store in memory + optional Redis/DB backend for cross-region resilience
2. Dialog handling
   - subscribe `OnInstantMessage` and explicitly process:
     - `SessionGroupStart` (for ad-hoc creation)
     - `SessionAdd` (invite)
     - `SessionDrop` (leave/kick)
     - `SessionSend` (message fan-out)
3. Membership rules
   - optional policy: only friends can invite/add
   - optional blocklist/mute checks
4. Delivery strategy
   - local root/child delivery first
   - fallback via `IMessageTransferModule`
   - retry when stale presence is detected
5. HG compatibility
   - include home URI/UUI metadata where needed
   - preserve standard OpenMetaverse IM dialog semantics
6. Safety limits
   - max participants per conference
   - idle timeout cleanup
   - anti-spam throttle per inviter/session


### Test matrix to call it “100%”

Must pass all:

1. Same region: 2-20 users join/send/leave.
2. Multi-region same grid: crossing while conference active.
3. Offline/online transitions during active conference.
4. Hypergrid guest + local users mixed conference.
5. Restart of one region while session survives (if persistent backend enabled).
6. Wrong/injected session IDs are rejected.


---

## Script protection plan (updated to current decision)

### Final decision from discussion

Goal: script should work cross-grid, but source should not be easy to steal.

Final constraints accepted:

1. If a foreign grid does not run our module/decryptor, encrypted script assets will not be portable.
2. If someone has true grid admin/root access, perfect source secrecy cannot be guaranteed.
3. "Weird characters" (unicode/gibberish style) is only obfuscation, not strong protection.


### What we will do instead (practical + portable)

1. **Portable script mode (default)**
   - keep distributed scripts standards-compatible `LSLText`
   - script remains runnable on other OpenSims/Hypergrid
2. **Protection by architecture**
   - keep in-world LSL as thin client/stub
   - move sensitive/business logic to external service we control
   - script calls signed API endpoints for protected operations
3. **Permissions hardening**
   - enforce no-mod/no-copy/no-transfer policies where possible
   - keep server-side checks for view/edit paths
4. **Optional obfuscation (soft deterrent only)**
   - safe ASCII obfuscation allowed for friction
   - do not rely on it for admin-level threat protection


### Explicit non-goals

We do **not** claim this is possible in stock OpenSim:

- "unreadable to foreign grid admins" + "runs there without our module"

That combination is not realistic for fully portable scripts.


### Planned implementation changes

1. Add policy toggle:
   - `ScriptProtectionMode = portable|local-only`
2. `portable` mode:
   - standard LSL assets only
   - optional obfuscation pass (non-breaking)
3. `local-only` mode:
   - protected/encrypted script format allowed
   - block HG export/transfer of protected assets
4. Add remote-logic support package:
   - signed request format (per object owner/object id/expiry)
   - key rotation + replay protection + audit logs


### Implemented now (policy toggle)

`LSL_Api` now reads:

- `ScriptProtectionMode = off|portable|local-only`

Behavior:

- `off` => `llEncryptCode/llDecryptCode` pass-through
- `portable` => `TASIA2:` payload
- `local-only` => `TASIA2L:<owner-uuid>:<payload>` and owner mismatch is rejected on decrypt


### Implemented now (LL function path)

Added new LSL API functions for portable obfuscation workflow:

1. `llEncryptCode(string pass, string notecard)`
2. `llDecryptCode(string pass, string notecard)`

Implementation details:

- `notecard` is resolved from prim inventory by name (or UUID).
- notecard content is used as secret material for key derivation (SHA-256 based key).
- payload is XOR-obfuscated and encoded to unusual CJK-range characters.
- encrypted format is prefixed with `TASIA2:`.

Important notes:

- this is intentionally **obfuscation/fake-encryption**, not strong cryptographic secrecy against grid admins.
- it is designed to avoid requiring custom module support on remote grids for simple text transformation workflows.
- both encrypt and decrypt require access to the same notecard secret content.


### Example usage pattern

1. Creator places a secret notecard in script inventory (example: `ENC_KEY`).
2. Creator encodes source/text:
   - `string enc = llEncryptCode(myText, "ENC_KEY");`
3. Authorized user with same key notecard decodes:
   - `string plain = llDecryptCode(enc, "ENC_KEY");`


### Bottom line

- For Hypergrid compatibility: use **portable mode** + architecture-based protection.
- For strongest secrecy: use **local-only mode** and accept non-portability.
