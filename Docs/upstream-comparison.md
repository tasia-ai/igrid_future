# Upstream drift review: Directory.Build.targets & MoneyServer csproj

## Context
The upstream `OpenSim-Tranquillity` repository is not publicly discoverable via GitHub search or unauthenticated `git ls-remote`, so the closest available baseline is the last commit before the new binary-download flow and MoneyServer reference reshuffle within this fork. The comparisons below use:

* `Directory.Build.targets` at `eebe9141` (pre-network download, base64-embedded archive).
* `OpenSim.Server.MoneyServer.csproj` at `a08a5d29` (earlier hint paths + `Private` flags layout).

## Observed deviations from the inferred baseline

### Directory.Build.targets
* **Current (HEAD):** downloads `legacy-bin-libs.zip` from a configurable URL with optional cache path and SHA256 verification, restoring before both `Restore` and `ResolveReferences`.【F:Directory.Build.targets†L1-L36】
* **Baseline (`eebe9141`):** expected an embedded base64 blob (`ThirdParty/LegacyBin/legacy-bin-libs.base64`) and decoded it locally during `ResolveReferences`, with no network access or checksum enforcement.【F:Directory.Build.targets†L2-L22】

### MoneyServer csproj hint paths
* **Current (HEAD):** keeps package references plus explicit bin-folder references for `Nini`, `XMLRPC`, and the `OpenMetaverse` assemblies; no `Private` overrides are set, so MSBuild defaults apply.【F:addon-modules/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj†L23-L48】
* **Baseline (`a08a5d29`):** stored the same hint paths in the first item group and marked `Nini` as `Private` while leaving other legacy DLLs `Private=False`, resulting in different copy-local behavior.【F:addon-modules/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj†L11-L40】

## Reconciliation plan
1. **Attempt to rebase onto upstream once access is available.** Bring in upstream changes, then re-apply a small patch that keeps the network-based legacy bin retrieval with checksum + optional cache to avoid reintroducing embedded binary blobs.
2. **Patch MoneyServer references after rebase.** If upstream still uses the older `Private` settings, keep our current copy-local defaults (which allow the new `Directory.Build.targets` flow to manage shared bin assets consistently). Add a local `patch`/`reapply` step in merge instructions so the hint-path layout survives future merges.
3. **Track checksum and URL overrides as intentional drift.** Document environment variables (`LEGACY_BIN_URL`, `LEGACY_BIN_SHA256`, `LEGACY_BIN_CACHE[_DIR]`) in merge notes so upstream pulls do not strip the secure-download flexibility.

## Intentional differences to preserve
* **Network-based legacy bin provisioning with SHA256 verification** replaces the embedded base64 archive to reduce repo size and enable cache reuse. Future merges should keep the download + checksum logic and only adjust URLs/checksums as releases change.
* **Unified bin hint paths without explicit `Private` overrides** in MoneyServer to rely on the shared `bin` restore performed by `EnsureLegacyBinDependencies`; avoid reintroducing conflicting copy-local flags unless upstream offers a replacement distribution strategy.
