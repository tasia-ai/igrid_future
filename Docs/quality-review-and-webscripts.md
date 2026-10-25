# Review and web-script implementation

Branch comparisons included `develop`, `tranquillity/hg-travel-net10`,
`fix/cutover-net10-daemon-and-deploy`, `merge/script-engine-single-project`
and the existing older `work` checkout. PostgreSQL group membership queries
were recovered from `work`; the root-object-ID query follows the working MySQL
implementation. Other branches retained several of the same stubs.

Confirmed issues corrected:

* Migration failures now stop startup and do not advance their version; original
  exceptions are preserved. Legacy imports explicitly handle absent old tables.
* Historical group and offline-message upgrades preserve rows instead of dropping
  their tables. Invalid historical values fail visibly instead of disappearing.
* Region registration/deregistration no longer report success after failed writes.
  PostgreSQL stores QUIC host/port metadata and constrains the port range.
* Group membership and root-object-ID lookup are implemented; estate deletion is
  transactional and refuses estates that still have regions assigned.
* Group search and GridUser prefix lookup use parameters. Group count and timestamp
  cleanup queries are valid PostgreSQL; user search uses the database's collation.
* Invalid UUIDs/numbers no longer silently become zero. A stray `/develop` line in
  the region migration is removed and the final additive migration can be retried.
* Null plugin constructor arguments again mean a parameterless constructor. This
  fixes a regression in the previous loader change, with a dedicated test.
* Vulnerable Caching.Memory and Microsoft.Data.Sqlite dependencies were updated
  to the .NET 10 package line used by this repository.

`tools/quality-review.sh` compiles the entire solution with analyzers, audits all
NuGet dependencies, lints PHP, and executes regression suites with current TRX
results. It distinguishes skipped tests and rejects runs with no executed tests.
A source inventory records TODO/FIXME/unsupported markers for contextual review;
those markers are not proof of defects, and a green run is not proof that every
possible repository bug is fixed. CodeQL is configured for the target branch
with the pinned SDK and manual build; hosted CodeQL execution requires GitHub CI.

The local complete compile passed with existing analyzer warnings. The reviewed
NuGet dependency set reported zero advisories. Current focused checks passed:
40 PostgreSQL regressions, 43 web-script security/PHP checks, and two real-engine
installation/execution checks. Broader-suite outcomes are recorded in the current
quality artifacts; do not infer their result from these focused counts.

The new addon, signed protocol, PHP generator, configuration and stock-grid helper
are documented in [Addons/TasiaAddons.WebScripts](../Addons/TasiaAddons.WebScripts/README.md).
The standalone project also builds outside this repository against the published
.NET 10 host assemblies. Other grid vendors and runtimes still require a matching
build and host validation. This does not modify production databases, production
credentials or deploy the addon to a live grid.

Known optional PostgreSQL gaps remain `ITrustedGridData`, `IUserAliasData` and
experimental `IXGroupData`; the provider audit records these explicitly. Optional
parameterless asset-plugin initialization entry points still require their
configured connection-string overloads. Those are not silently replaced with
fake successful implementations.
