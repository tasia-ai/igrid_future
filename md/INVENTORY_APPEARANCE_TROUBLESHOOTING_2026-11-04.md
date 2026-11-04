# I-Grid inventory and appearance troubleshooting — 2026-11-04

**Status:** OPEN — live Firestorm/Tasia Viewer regression not passed.  
**Scope:** OpenSim-NGC/TasiaNGC, Robust, Dark_Secrets region, PostgreSQL, AIS v3, Current Outfit Folder (COF), avatar appearance, server-side baking (SSB).  
**Purpose:** A portable, evidence-based record so the same issue can be diagnosed on a second server without repeating exploratory changes.

> This is a living incident record, not proof that any proposed fix resolves the issue. Entries below distinguish observed behavior, source audit, and proposed tests. Do not add credentials, capability URLs/tokens, private avatar data, or raw database connection strings.

## Supported viewer policy — updated 2026-11-04

**Product decision: this I-Grid deployment is intended for Tasia Viewer only.** Firestorm was used to isolate server-versus-viewer symptoms and is **not** a supported production client or release acceptance target.

- **Release gate:** Tasia Viewer must load the correct shape/height, preserve clothing layers after relog, and successfully create/edit/move inventory items with durable PostgreSQL persistence.
- **Diagnostic reference only:** Firestorm's behavior can still reveal a server-side protocol or persistence defect. Do not spend work on Firestorm-specific UI/compatibility without a demonstrated impact on Tasia Viewer or shared backend correctness.
- **Still required:** standard OpenSim server-side UDP appearance pathways, AvatarFactoryModule, proper AIS handlers, valid CAPS negotiation, and correctness checks unless replaced with a tested end-to-end implementation. Viewer exclusivity alone is **not** a reason to remove them.
- **Deployment control:** any planned server-side restriction of other viewers is a separate security/compatibility change, not part of the inventory/appearance incident; test it without breaking legitimate Tasia Viewer sessions or Hypergrid.
- **Second server:** do not assume it has the same supported-viewer policy. Verify its requirements independently before reusing this restriction.

## User-visible symptoms

- Firestorm: original avatar shape loads correctly; inventory modifications reportedly fail.
- Tasia Viewer: clothing/wearables are mixed up after relog; avatar shape/size displays incorrectly outside Edit Appearance but looks correct during editing.
- The earlier COF-selection patch and derived-bake-index invalidation **did not** resolve the live symptoms.
- A complete before/after client operation and database persistence trace is **still missing**.

## Confirmed observations and source-review reports

| Area | Evidence / finding | Interpretation | Status |
| --- | --- | --- | --- |
| COF selection | Local COF under My Inventory: `df669dca-b4b0-4a63-9f6b-6e7aeaf175eb`, version 337; suitcase COF: `357ca534-179b-465e-aed6-36ca65da5ecb`, version 599. Earlier AIS chose highest-version COF from the whole skeleton while SSB used the active inventory root. | A real selector mismatch existed; selection was reportedly unified to active-root semantics. | Fix deployed earlier; **live symptoms persisted** |
| Orphaned link | Local COF contains a link named `marty` whose target was not in inventoryitems. | Missing link target needs separate handling; does **not** prove missing shape asset. | Left untouched; preservation behavior tested |
| Bake index | 13 derived `Bake*` rows for Cute Devil were removed after backup; saved wearable, visual-param, height, inventory data were retained. | Invalidated cached bake references, not a reset of the outfit. | Done earlier; no live improvement |
| AIS fetch | Firestorm log reports repeated HTTP 404 `no category` on `FetchCategoryChildren`; cap URL/path not captured. | Could be a library, user, or other requested folder. Cannot identify backend from this log alone. | Root cause **unknown** |
| AIS routing | Source review reports `AISv3Module.RegisterCaps` uses agent inventory for `InventoryAPIv3` and `LibraryServiceBackend` for `LibraryAPIv3`. | No demonstrated cross-wiring; a missing library category is not automatically damaged user inventory. | Audited, not proven for specific 404 |
| Inventory move acknowledgement | `XInventoryService.MoveItems` previously ignored storage-layer `MoveItem` return values; source change now reports false on failed moves. | Genuine false-success defect; does **not** prove the reported Firestorm edit failure is a move failure. | Source fix made, **not deployed** as last reported |
| AIS create behavior | Source audit reports AIS item creation with item entries returns 501; category/link creation handled separately. | Check actual operation and response before treating 501 as the failure cause. | Needs request capture |
| Event Queue | Repeated `/CE/...` HTTP 502 in viewer log; code review found `EventQueueGetModule.NoEvents` deliberately returns 502 for timed-out active long poll (404 for missing queue). | 502 alone is **not** proof of Pangolin trouble or lost events. | Explained; no change recommended absent event-loss evidence |
| COF stale updates | Earlier client/server COF version mismatch (e.g., 599 vs 337 and later 337 vs 336); stale-version rejection exists. | Rejection can be legitimate; do not bypass it. | Keep validation |
| COF anti-livelock | Source audit reports after repeated mismatches `LivelockBake` can be treated as success and apply COF wearables despite version mismatch. | Potential unsafe application of unconfirmed outfit state. | Proposed safety fix; not deployed |
| SSB effective setting | Global `[Appearance] ServerSideBaking = true`; Dark_Secrets region override reportedly `ServerSideBaking = false`. Startup evidence only showed global default. | Expected effective region mode is OFF if override is honored, but runtime value not conclusively logged. | **Verify first** |
| Appearance handlers | Source review found only one production `UpdateAvatarAppearance` CAPS registration, in `ServerSideBakingModule`; standard UDP `AgentSetAppearance` and `AgentIsNowWearing` remain required. | No evidence that disabling legacy UDP handlers would help. | Do **not** remove standard handlers |
| LLSD validation | Existing AIS rejects malformed LLSD; audit found some valid-but-wrong top-level shapes can become empty maps, and Create can partially accept invalid entries. SSB cap lacks strict typed/ranged `cof_version` validation. | Reject wrong-shaped/typed mutation requests before any backend write. | Proposed safety fix |
| Cache scope | `HGInventoryBroker.InvalidateCacheOnSuccess` clears that instance's owner caches after successful writes, not necessarily brokers in other region processes. | Cross-process staleness is possible, **not yet proven** as root cause. | Test across regions |

## Upstream design references

- [SSB design brief](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/blob/develop/Docs/feature/ssb-appearance/DESIGN-BRIEF-ssb-appearance.md)
  - SSB augments the standard appearance pipeline, not replaces UDP appearance handlers.
  - Region flag `[Appearance] ServerSideBaking`; override may be set in the named region section.
  - `[AIS] Enabled` enables modern inventory/outfit changes; AIS is distinct from the bake engine.
  - Wearable asset resolution and appearance persistence must complete **before** baking.
  - COF membership/version and ScenePresence resolved wearables have distinct roles.
- [AIS malformed LLSD audit](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/blob/develop/Docs/feature/ais-v3/AUDIT-1-MALFORMED-LLSD.md)
  - LLSD parser may yield `OSDType.Unknown` rather than throw.
  - Do not turn parsing errors into empty valid maps.
  - Reject invalid top-level types and mandatory-field types at the HTTP boundary before writes.

## Test results reported (not equivalent to live validation)

- AIS: **198 passed**.
- Event Queue: **20 passed**.
- Server-side baking: **136 passed, 3 skipped**.
- Existing InventoryService test project had package/project-reference restore problems in an earlier run; production project reportedly built.

## Next actions (one change per experiment)

1. **Instrument and reproduce one inventory mutation.** Server-side log: timestamp, operation, cap identity (not token/URL), agent ID, inventory root, category/item ID, backend, storage result/affected rows, HTTP result, and sanitized error. Match a Firestorm create-folder or rename attempt to database state. Capture one AIS 404 with method, route template and selected backend.
2. **Check effective SSB state at region initialization.** Log resolved per-region setting and whether `UpdateAvatarAppearance` is registered; confirm `RegionProtocols` advert. Do not infer mode solely from the global default.
3. **Controlled SSB comparison only after logout and config backup.** Keep AIS and standard UDP appearance handlers enabled; change only Dark_Secrets SSB override; restart the affected region; test Firestorm and Tasia Viewer separately. Revert immediately if worse. Do not combine this toggle with the pending LLSD/anti-livelock code changes.
4. **Deploy reviewed persistence acknowledgement fix independently**, with regression test evidence and rollback build.
5. **Safety hardening separately:** strict AIS/SSB LLSD schema validation, typed/nonnegative `cof_version`, and no applying COF wearables when mismatch persists. Cover zero writes on bad input and interleaved UDP/CAPS updates.
6. **Investigate cross-region cache coherency** with two simulator processes and controlled inventory mutations before selecting an invalidation strategy.
7. **Trace appearance end-to-end**: viewer edit -> region `AvatarFactoryModule` -> `AvatarService.SetAppearance` -> PostgreSQL -> relog `GetAppearance` -> ScenePresence -> SSB/UDP appearance -> viewer. Compare wearable item/asset UUIDs, visual-parameter hashes and height. Do not log sensitive inventory content.

### Acceptance criteria

- Tasia Viewer can create/rename/move a test inventory entry; writes persist after relog, and failed writes are not acknowledged. Firestorm may be used only for diagnostic comparison.
- Tasia Viewer restores the intended clothing layers and shape/height outside Edit Appearance after relog.
- Server and viewer agree on the active-root COF ID/version; no unsupported forced acceptance.
- No duplicate appearance CAPS registration or harmful UDP/CAPS ordering.
- A second server can reproduce the same regression suite using its own configuration, without destructive data surgery.

## Safety and rollback

- Do **not** delete or merge either COF, reset wearables, erase inventory, change PostgreSQL schema, disable UDP appearance handlers, or modify QUIC as part of this incident.
- A prior backup was reported at `H:\opensim-net-10\mess\backups\cof-authority-20261104\robust-before-cof-cache-invalidation.dump`; **verify it exists and restores before relying on it**.
- A pre-SSB-toggle configuration backup was reported at `H:\opensim-net-10\backups\ssb-off-dark-secrets-20261104-210035\OpenSim.ini`.
- All deployment statuses above reflect the latest *reported* state, not a fresh inspection of running binaries.
- Record future changes below with date, commit SHA, deployed build, tested viewer and result.

## Change log

- **2026-11-04** — Viewer policy clarified: Tasia Viewer is the only supported production viewer; Firestorm remains a diagnostic comparator.\n- **2026-11-04** — Initial incident documentation created from configuration review, Firestorm log findings, reported server-source audits, upstream SSB/AIS documentation, and user-reported live tests. Issue **OPEN**.
