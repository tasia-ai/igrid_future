# Legacy helper compatibility notes (no secrets)

When bridging to older `currency.php` / `landtool.php` implementations, these issues are common:

1. Missing helper globals/constants (example: `W4OS_GRID_INFO`).
2. Missing utility functions (example: `make_random_guid`, `opensim_get_region_info`).
3. Presence/robust DB mismatch (`Presence` table looked up in wrong DB).
4. Money DB schema drift (required fields in `money.transactions` not provided by insert).
5. Non-portable SQL in balance update logic (lock/unlock syntax differences).

## Recommended hardening checklist

- Guard optional constants/functions with `defined()` / `function_exists()` checks.
- Use robust-safe UUID generator fallback if `make_random_guid()` is unavailable.
- Ensure Presence lookup points to the robust/main DB containing `Presence`.
- Match `money.transactions` inserts to your actual schema (required columns).
- Use atomic upsert for balances:

```sql
INSERT INTO balances (`user`,`balance`) VALUES (:user,:delta)
ON DUPLICATE KEY UPDATE `balance` = `balance` + VALUES(`balance`)
```

- Keep all credentials in local-only config files; never commit secrets.
