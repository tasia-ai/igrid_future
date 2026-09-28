# Fresh Metaverse — Future Plans & Propositions

**Date:** 2026-09-15
**Project:** Fresh Metaverse OpenSim grid (Windows 10, native QUIC)
**Branch:** `tasia/fresh-metaverse-snapshot` · `H:\grid\igrid-package\` / `H:\grid\FreshMetaverseManager\` / `H:\grid\work\fixtest\`
**Status doc:** `md/FRESH_METAVERSE_PROJECT_STATUS.md`
**Snapshot:** `Docs/deployment-examples/fresh-metaverse\`

> This is the living roadmap + proposition backlog. Same style as `md/plan.md` / `md/planed-changed.md` / `md/propostion-estate-code.md`.

---

## 0. Principles

1. **Windows 10 stays supported** — no Schnitzel `19045` → `20145` gate via patched `System.Net.Quic` + `msquic.dll` OpenSSL.
2. **`regions.yaml` is truth** — `generate_configs.py` → `generated\` is the only deploy path. Never edit `Robust.ini`/`OpenSim.ini` by hand.
3. **Manager owns stdin** — hidden launches are fine, but `RedirectStandardInput=true` is non-negotiable for `backup`/`oar`.
4. **Snapshot is release** — `H:\grid\xampp\htdocs` + `FreshMetaverseManager` are not Git repos; push via `Docs\deployment-examples\fresh-metaverse\` only, sanitized `CHANGE_ME`.
5. **No blind `robocopy build\Release`** — always `/XF System.Net.Quic.dll msquic.dll System.Private.CoreLib.dll` then **re-patch** from `quic-windows10-selfcontained-bin-replace\bin\`.

---

## 1. Immediate (0–2 weeks) — stabilize & close gaps

### 1.1 QUIC — finish cert rotation loop (DONE, needs live soak)

* **Current:** Robust watcher `FileSystemWatcher + 1min poll + 800ms debounce → TryReloadCertificate → BuildFreshCertificateContext(exportableEphemeral)` + sim `QuicServerConfig` mtime cache is built and deployed (`OpenSim.Server.Handlers.dll 209920 watcher=True`).
* **Next:** Live soak — `touch SSL\quic\quic-cert.pem` on running `19568` and confirm `Robust.log: Certificate reloaded (file watcher ...)` without restart. Document 90-day LE rotation via `acme.sh` → `generated\robust\SSL\quic\` atomic replace.
* **Propose:** Add `quic-cert-rotation.ps1` that calls `acme.sh --issue ... --install-cert --cert-file ... --key-file ... --fullchain-file ...` then `Copy-Item → touch` to trigger watcher.

### 1.2 Build pipeline — make it bulletproof

* **Propose:** `H:\grid\work\fixtest\scripts\deploy-fresh.ps1`:

  ```powershell
  dotnet build OpenSim.sln -c Release
  robocopy build\Release H:\grid\igrid-package\bin /E /XF msquic.dll System.Net.Quic.dll System.Private.CoreLib.dll System.Net.Security.dll
  robocopy build\Release H:\grid\igrid-package\generated\robust /E /XF msquic.dll System.Net.Quic.dll System.Private.CoreLib.dll System.Net.Security.dll
  Copy-Item quic-windows10-selfcontained-bin-replace\bin\msquic.dll H:\grid\igrid-package\bin\msquic.dll -Force
  Copy-Item quic-windows10-selfcontained-bin-replace\bin\System.Net.Quic.dll H:\grid\igrid-package\generated\robust\System.Net.Quic.dll -Force
  Copy-Item $nuget\npgsql\9.0.3\lib\net8.0\Npgsql.dll H:\grid\igrid-package\bin\Npgsql.dll -Force
  # verify
  python -c "assert 'Certificate watcher'.encode('utf-16le') in open(r'H:\grid\igrid-package\generated\robust\OpenSim.Server.Handlers.dll','rb').read()"
  ```
* **Propose:** CI check in `build\Release` — fail if `msquic.dll` is `524320` (stock) not `4180512`.

### 1.3 Manager hardening

* **Propose:** `BtnStartRobust` → also tail `generated\robust\Robust.log` for `QUIC proxy listening` within 10s, else show red `QUIC failed — check System.Net.Quic`.
* **Propose:** `TxtRobustDetail` shows cert `CN=` + `notAfter` parsed from `quic-cert.pem` (`openssl x509 -noout -subject -enddate`).
* **Propose:** Guard `Stop All` before any `msquic.dll` copy (already done, add explicit lock check `IOException being used by another process` → toast).

### 1.4 Viewer login smoke

* **Task:** External viewer via `os.tasia.work.gd:22000` + internal `127.0.0.1:22000` with `cute devil` `6cdcedb5...` — confirm both paths. Current LAN showed `timed out` externally from `192.168.178.10` (NAT hairpin) but `127.0.0.1` worked — document hairpin vs port-forward.

---

## 2. Short term (1–6 weeks) — operator UX

### 2.1 Estate & region visibility — `show region-estates` (proposition)

Build the proposal from `md/propostion-estate-code.md` into Fresh:

```
command: show region-estates [estate-name]
output: Region Name | Region UUID | Estate Name | Estate ID | Size | Pos | Status
sources: IGridService.GetRegionRange + IEstateService
sort: RegionName asc
filter: optional estate name
perm: console admin only
```

* **Propose:** Also `show estates` and `show regions` (already Tasia). Wire into manager `Regions → Estate` column.

### 2.2 Manager — what operators ask for next

| Proposition | Why | Effort |
|-------------|-----|--------|
| **Live cert panel** — `CN / issuer / notBefore / notAfter / intermediates` + `Reload` button (`touch` trigger) | monthly ops anxiety | S |
| **Bulk restart progress with per-region logs** — expand `ProgressBulk` → per-region `✓ backup sent` `✓ schedule 60s` `✗ offline` | Tasia flow is opaque now | M |
| **OAR queue viewer** — list `generated\sims\<Name>\data\*.oar` + drag-drop to mapping | operators lose files | S |
| **DB quick panel** — `balances count`, `regions count`, `gridinfo` fetch | sanity check without psql | S |
| **`RestartModule` delay editable** — slider `30/60/120s` not hard 60s | events | S |
| **Hide/Show all sim consoles** — toggle + `Force Kill` already there, add `Show All` | debugging | S |

### 2.3 Backups & restores

* **Propose:** Automate `oar backup` nightly 04:00 per `H:\grid\work\fixtest\Docs\deployment-examples\fresh-metaverse\backup-cron.md` — manager already has `BulkOarRestoreMappedAsync`, add `BulkOarBackupScheduled`.
* **Propose:** Keep last 7 `*.oar` per region in `generated\sims\<Name>\data\backups\` with rotation.

### 2.4 Apache / helper / money — make XAMPP invisible

* **Propose:** Replace `apache_start.bat` / `apache_stop.bat` with manager-owned `httpd.exe` directly (already partially). Remove `@@BITROCK_INSTALLDIR@@` placeholder reliance.
* **Propose:** Single `Start All Stack` button: `Robust → wait 22000 → Money + Helper → Apache → Sims` with dependency checks (currently manual).

---

## 3. Medium term (6–12 weeks) — grid completeness

### 3.1 Money & economy

* **Current:** `tasia_moneyd.py` `xmlrpc_passthrough` helper, `balances` table, `ForceTransferMoney` works, `GET 501` normal.
* **Propose:** `premium_accounts` + `w4os` integration — sync balances to WordPress for web wallet display.
* **Propose:** `MoneyDNotifications` filter engine (from `History.md`) — type/sender/receiver allow/suppress, already in `tasia-moneyd` plan, enable for Fresh.
* **Propose:** Audit `PayObject` / `BuyMoney` dedup — `History.md` says typing notif duplicates, add `MoneyDNotifications` auto-reload on INI change.

### 3.2 Shared Inventory (from `md/planed-changed.md`)

* **Current:** module exists `TasiaAddons.SharedInventory` — shared avatar `RootFolderName`, per-user `Shared RW/Upload → Shared`.
* **Propose for Fresh:** Enable on `Fresh01` + `Ground_Zero` family:
  * `[SharedInventory] Enabled=true SharedAvatarUUID=<grid-system-uuid> SyncIntervalSeconds=300 SyncOnLogin=true`
  * Operator commands `sharedinventory sync all` + manager button.

### 3.3 Friend Conferences 100% (from `md/planed-changed.md`)

* **Current:** `TasiaAddons.FriendConference` handles `SessionGroupStart/SessionAdd/SessionDrop/SessionSend`, cross-region import exists but needs soak on Fresh QUIC (our region crossing fix is pending).
* **Propose:** Test matrix from that doc — 2-20 users same region / multi-region / offline cycle / HG guest / region restart.

### 3.4 Script protection (from `md/planed-changed.md`)

* **Current:** `ScriptProtectionMode=off|portable|local-only` + `llEncryptCode/llDecryptCode` `TASIA2:` soft obfuscation.
* **Propose for Fresh:** Default `portable` (thin LSL stub + external signed API) — keep marketplace scripts cross-grid runnable. Only `local-only` (`TASIA2L:<owner>:payload`) for grid-exclusive logic.

### 3.5 Search, map, profiles

* **Propose:** Wire `OpenSimSearch.Modules` + `w4os` profiles — Fresh already has `amber/password.php` + `search/index.php` 200, connect to viewer Search.
* **Propose:** `map-data.php` → dynamic tiles from `GridService` (currently static).

---

## 4. Longer term (3–6 months) — differentiate Fresh

### 4.1 Web viewer path

* **Propose:** From `md/WEB_VIEWER_PLAN.md` — proxy `WebSocket ↔ UDP` + Babylon.js `WorldRenderer/PrimFactory/AvatarRenderer` — but short-circuit via **QUIC** viewer first (our native viewer already speaks QUIC `22002` via proxy). Web viewer can reuse same `QuicProxyConnector` framing (`4-byte BE length`).
* **Priority:** Low for Fresh — native viewer QUIC is revenue path, web viewer is showcase.

### 4.2 Extension / plugin host (`md/plan.md`)

* **Propose:** Adopt `Tasia.Extensions.SDK/Loader/Host` pattern for Fresh modules — `TasiaAddons.MACAudit`, `AlertNotifications`, `RemoteSound` etc. as `bin/extensions/<Name>/plugin.json` with `target Sim|Robust`. Keeps `orig/` open-core clean.

### 4.3 Voice

* **Propose:** `WebRtcVoice` + `WebRtcJanusService` (already in `build\Release`) — Janus SFU on `ok.tasia.work.gd`, region modules auto-join. Low pri until QUIC stable.

### 4.4 Hardening

* **Propose:** `TasiaAddons.MACAudit` + `LoginSecurity` rate-limit `9632587410` invite → `FRESH_INVITE_CODE`, audit login via `OnLogin` connector already merged.
* **Propose:** Daily `pg_dump robust assets money` → `H:\grid\backups\` + `rclone` offsite.

---

## 5. Concrete Propositions (ready to pick)

### P1 — `show region-estates` (estate code proposition)

Priority **Now**. Doc-only proposal exists — turn into code:

```csharp
AddCommand("show region-estates", "List regions with estates", ShowRegionEstatesCommand);
void ShowRegionEstatesCommand(string[] cmd) {
  string filter = cmd.Length > 1 ? cmd[1] : null;
  var regions = m_gridService.GetRegionRange(UUID.Zero, int.MinValue,int.MaxValue,int.MinValue,int.MaxValue);
  var estates = m_estateService.GetEstates(); // estateId -> name
  // join, filter, orderby RegionName, write table
}
```

### P2 — Cert auto-rotate script

```powershell
# renew-and-touch.ps1
acme.sh --issue --dns dns_cf -d not.let-us.cyou
acme.sh --install-cert -d not.let-us.cyou `
  --cert-file H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem `
  --key-file  H:\grid\igrid-package\generated\robust\SSL\quic\quic-key.pem `
  --fullchain-file H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem
Copy-Item H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem H:\grid\igrid-package\generated\sims\*\SSL\quic\quic-cert.pem -Force
# watcher picks up mtime, or:
(Get-Item H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem).LastWriteTime = Get-Date
```

+ Task Scheduler monthly.

### P3 — `Robust.ini` self-heal guard

Add to `generate_configs.py`: always write `[QuicProxy] Port 22002` + `[ClientStack.Quic] CertificatePath ./SSL/quic/quic-cert.p12` + check `regions.yaml` `quic_port` pool `22200-22400` unique.

### P4 — Manager: one-click full stack

`BtnStartFullStack` → `Robust (wait 22000 200)` → `Money 1026 501` → `Helper 8015 healthz` → `Apache 8082` → `Sims parallel 3` → `netstat UDP 22002 + 22203` → `gridinfo`.

### P5 — OAR retention policy

`manager OAR tab → Settings: keep 7 per region, compress after 3 days (.oar.gz), upload to S3`.

### P6 — Monitoring

* Prometheus `RobustStats` + `OpenSimStats` → Grafana `UDP 22002` + `sim HTTP 200` + `region count` + `PG connections`.
* Uptime Kuma `http://127.0.0.1:22000/get_grid_info` every 60s.

### P7 — Web money mirror

WordPress `tasia-3d-viewer` + `w4os` → show `POSTGRES money.balances` in profile, link to in-world `ForceTransferMoney` history.

---

## 6. What We Will NOT Do (decisions)

* No ProBuild fork — single `quic-windows10-selfcontained-bin-replace\bin\` patch is enough for `19045`.
* No SChannel inbox `msquic 512KB` — we pin `4083KB` OpenSSL everywhere.
* No blind `dotnet publish` overwrites — curated `deploy-fresh.ps1` is the gate.
* No custom script encryption that breaks HG — `portable` stays runnable everywhere.

---

## 7. Next Session Checklist (for Mom to pick)

- [ ] Soak cert watcher live (touch → `Certificate reloaded` log)
- [ ] Approve `show region-estates` implementation
- [ ] Approve `renew-and-touch.ps1` + monthly Task Scheduler
- [ ] Approve `deploy-fresh.ps1` hardening
- [ ] Pick manager one-click full stack vs stepwise

---

*Sleep well, Mom 💖 — grid is on `pid 19568` `UDP 22002` watching your certs. Good night — do you want me to hibernate?*
