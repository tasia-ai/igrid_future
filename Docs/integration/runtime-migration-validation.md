# Deferred runtime migration validation

These procedures are **for a separately authorized maintenance window only**.
They were not run during offline integration. Never point them at production or
`testing-poligon` without explicit authorization and a verified disposable
backup. Use isolated copies with outbound network access disabled.

## EstateStore branch-collision matrix

For each provider, prepare disposable copies representing both historical
schemas at the colliding version. Record row counts and checksums for
`estate_settings`, `estate_manager_roles`, and every existing
`estate_*_experiences` table before starting.

| Provider | Legacy I-Grid schema | Upstream Experience schema | Expected forward version |
|---|---|---|---:|
| PostgreSQL | EstateStore v16 with `EstateOwnerGroup` and manager-role rows | EstateStore v16 with allowed/key/blocked experience tables | 18 |
| MySQL | EstateStore v38 with `EstateOwnerGroup` and manager-role rows | EstateStore v38 with allowed/key/blocked experience tables | 40 |
| SQLite | EstateStore v13 with `EstateOwnerGroup` and manager-role rows | EstateStore v13 with allowed/key/blocked experience tables | 15 |

Also test a pre-collision schema for each provider. After the upgrade, verify:

1. The version is the expected forward version and restarting the provider
   applies no additional changes.
2. Both feature families exist: all three Experience tables, the owner-group
   column, and the manager-role table/index.
3. Every pre-upgrade estate row, owner-group UUID, manager-role UUID, and
   Experience UUID is unchanged; no source table or row was dropped.
4. Provider load/save round-trips the owner-group UUID and manager-role list.
5. Re-running the forward repair on a disposable copy is idempotent.

Do not treat `CREATE TABLE IF NOT EXISTS` as proof that a legacy table's
definitions or indexes are correct; inspect those separately before approval.

## PostgreSQL UserAccount v7/v8

Use disposable copies and preserve a pre-test snapshot. Cover a fresh
UserAccounts schema, the upstream v6 schema, and each known I-Grid v6
type/constraint variant. Include rows with:

- null and empty/whitespace `DisplayName`, `NameChanged`, and `UserCountry`;
- names of lengths 31 and 32, and country values of lengths 64 and 65;
- `NameChanged` values `0`, positive integer text, zero-decimal numeric text,
  negative values, nonnumeric text, and values above `2147483647`.

Expected behavior: null/blank timestamps normalize to `0`; nullable display and
country values normalize to empty strings; representable values are preserved;
overflow, malformed numeric text, and overlong display/country values stop the
migration without advancing its version or truncating data. Verify defaults,
nullability, indexes, row counts, and metadata after the run and after a
restart. Test the v7-to-v8 repair separately against a disposable schema already
marked version 7.

## Authorized runtime evidence

Capture provider/version, schema-version rows, before/after row counts, column
definitions, and migration output. Do not include account data, UUID lists,
connection strings, or secrets in shared logs. Runtime schema testing does not
require launching a grid; keep provider tests isolated from production services.
