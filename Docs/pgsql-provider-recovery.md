# PostgreSQL provider recovery (.NET 10)

The target branch is `upgrade/develop-20261022`. Do not copy its DLLs into the
old .NET 8 program directory. The supplied incident log refers to the older
`OpenSim/Services/...` source layout; this branch uses `Source/OpenSim.Services.*`.
The log hides the original PostgreSQL loader exception, so it does not by itself
prove which deployed DLL or dependency is incompatible.

## Diagnosed defects

* `ServiceBase` hid loader exceptions and printed constructor argument values,
  including connection strings. Both loaders now share contract-identity checks,
  public/concrete/closed-type and constructor checks, and recursive diagnostics
  including `ReflectionTypeLoadException.LoaderExceptions`. Argument values are
  never included in loader diagnostics.
* The PGSQL base class and AgentPreferences provider falsely implemented
  `IExperienceData` with no-op methods. A dedicated, persistent PostgreSQL
  provider and migration replace these implementations.
* Generic PGSQL Store, Get and Delete, and several derived providers were stubs.
  CRUD now uses schema-typed parameters, UUID mapping and atomic upserts using
  one actual unique index. Conversion and database failures propagate.
* PGSQL UserAccounts lacked the current DisplayName/NameChanged fields.
  Migration 6 adds these and UserCountry, preserving existing rows.
* MuteList's migration existed on disk but was not embedded. All PGSQL migration
  resources are now embedded. Obsolete OIDS storage options are removed for
  PostgreSQL 12 and newer. The mute-ID deletion column spelling is corrected.
* Configured service connectors could register handlers with null services.
  Required service loading now fails construction, and GridServer aborts when
  any configured connector cannot load or no connectors are configured. Startup
  errors preserve a nonzero process exit status; the running message follows
  successful initialization.

All current `OpenSim.Data` contracts are enumerated by the discovery audit.
The optional `IUserAliasData`, `ITrustedGridData` and experimental `IXGroupData`
contracts have no PGSQL implementation on this branch; do not enable services
that require them. The simulator storage interface lives in Region.Framework
and is also included in the discovery audit.

## Reproduce tests safely

Use the SDK pinned by `global.json` (.NET 10.0.112). Docker is needed only for the
isolated database. The tests refuse a database other than LOCAL
`igrid_provider_test`. Do not point these tests at production.

```bash
docker run -d --name igrid-pgsql-smoke \
  -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=igrid_provider_test \
  -p 127.0.0.1:55432:5432 postgres:17
docker exec igrid-pgsql-smoke pg_isready -U postgres -d igrid_provider_test
export IGRID_PGSQL_TEST_CONNECTION='Host=127.0.0.1;Port=55432;Database=igrid_provider_test;Username=postgres'
dotnet test Tests/OpenSim.Data.PGSQL.Tests -c Release -m:4 \
  --logger 'trx;LogFileName=pgsql.trx' --results-directory TestResults
```

Without that variable, discovery and startup-failure tests run and PostgreSQL
integration tests are explicitly skipped. CI supplies the disposable database.
The integration tests exercise migrations, constructors, persistence, region
registration/lookup, inventory, authentication, presence and successful login.
Only the simulator's QueryAccess/CreateAgent handshake is a test double. This
is not a claim that a real viewer completed UDP/QUIC attachment or rendered a
region. Test records are retained only in the disposable test database.

## Build and deploy one coherent set

After committing the fixes, from the repository root:

```bash
IGRID_PGSQL_TEST_CONNECTION='Host=127.0.0.1;Port=55432;Database=igrid_provider_test;Username=postgres' \
  tools/publish-grid-verified.sh /absolute/new/grid-directory linux-x64
# For a Windows host, use win-x64 and a different new output directory.
```

The script rejects an existing destination and an uncommitted checkout, tests
providers, publishes the whole GridServer dependency graph and creates
`deployment-manifest.json` with the exact source commit, target framework,
runtime and SHA256 of every published file. Verify those hashes after copying.
Keep the complete `.deps.json`, runtime configuration, Npgsql and all transitive
DLLs together. Never selectively replace OpenSim.Data.PGSQL.dll or combine old
and new published directories. A cross-published Windows package still needs
validation on Windows.

Deployment to production is a separate operation. Retain existing production
configuration and credentials securely, take the normal database backup, review
the additive migration, and stage the complete release in a new program home.
This task does not modify production data or credentials. A successful local
start does not prove the incident deployment has been repaired.

For a local core-only startup against the same disposable database, use
`tools/run-pgsql-smoke-grid.sh /absolute/new/grid-directory`. It creates a test
configuration in that directory, with ports 58002/58003 and QUIC disabled.

Start core services first. Confirm no loader errors, configured connectors all
loaded, GridService registration/lookup and neighbours, UserAccounts, Presence,
inventory and login. Only then enable/test QUIC. TLS verification must remain
active. No QUIC viewer test is implied by the PostgreSQL tests.
