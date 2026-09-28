# Fresh Metaverse — Project Status

**Date:** 2026-09-15 04:20 UTC
**Branch:** `tasia/fresh-metaverse-snapshot` @ `https://github.com/martysl/igrid-server-code-2026-07-03/`
**Snapshot:** `H:\grid\work\fixtest\Docs\deployment-examples\fresh-metaverse\` (excludes `bgimg`/`js`/`Web`/zips, sanitized `CHANGE_ME` secrets)
**Host:** Windows 10 `10.0.19045` (native QUIC via MsQuic OpenSSL) · `H:\grid\igrid-package\` · `H:\grid\FreshMetaverseManager\` · `H:\grid\work\fixtest\`
**Last deployment:** `generate_configs.py` → `generated\deploy.json` (`console_pass DRPJk68PfNeqcW111v4JX1uE`) → `generated\robust\Robust.ini` + `generated\sims\<Name>\OpenSim.ini`

---

## 1. Objective

Stabilize a Windows-native Fresh Metaverse OpenSim grid:

* Single-port viewer QUIC `22002` via `QuicProxyConnector` in Robust + per-region QUIC `22200-22400` on sims
* WPF manager with *hidden* launches but **owned `stdin`** so `backup` / `oar backup` / `load oar` actually work
* `H:\grid\igrid-package\regions.yaml` + `templates\SimOpenSim.ini.tpl` as source-of-truth, `generated\` as deploy artifact
* Postgres `robust`/`assets`/`money` on `127.0.0.1:5432` (`opensim/opensim`), Apache `8082`, MoneyD `1026→24304`, Helper `8015→7852`
* Monthly Let's Encrypt rotation **without restart** (cert auto-reload)

---

## 2. Deployment Topology (verified)

| Component | Listen | Advertise | Notes |
|-----------|--------|-----------|-------|
| **Robust public** | `0.0.0.0:22000` TCP | `os.tasia.work.gd:22000` | `generated\robust\Robust.dll` cwd `generated\robust` |
| **Robust private** | `0.0.0.0:22001` TCP | `127.0.0.1:22001` | `[PrivatePort]` — QUIC `ProxyRegistrationURL http://127.0.0.1:22001/admin/quic/circuit` |
| **QUIC proxy** | `0.0.0.0:22002` UDP | `ok.tasia.work.gd:22002` | `QuicProxyConnector` ALPN `opensim-ll/1` |
| **Sim LLUDP** | `22200-22400` per-region | `51.89.54.203:*` via `ok.tasia.work.gd` | `Ground_Zero 22203/22006`, `Fresh01 22208..22229` |
| **Sim QUIC** | `22200-22400` pool | `ok.tasia.work.gd` | `[ClientStack.Quic] Port` per sim |
| **Sim HTTP** | per-region `HttpPort` | `127.0.0.1:*` | manager polls `http://127.0.0.1:{HttpPort}/` |
| **Apache** | `127.0.0.1:8082` (`8672/12344` internal) | — | `H:\grid\xampp\apache\bin\httpd.exe` + `apache_start/stop.bat` |
| **MoneyD** | `127.0.0.1:1026` → `24304` | — | `H:\grid\moneyd\tasia_moneyd.py` + `MoneyServer.ini` |
| **Helper** | `127.0.0.1:8015` → `7852` | `127.0.0.1:8082/ostools/currency.php` | `H:\grid\xampp\htdocs\ostools\config.json` `mode xmlrpc_passthrough` |
| **Postgres** | `127.0.0.1:5432` | — | DBs `robust`/`assets`/`money` |

DNS: `os.tasia.work.gd` + `ok.tasia.work.gd` → `51.89.54.203` · LAN NAT hairpin → use `127.0.0.1:22000` for local login, externally `os.tasia.work.gd:22000`.

---

## 3. Configuration Source-of-Truth

```
H:\grid\igrid-package\
  regions.yaml                          # 30 regions, single truth
  templates\SimOpenSim.ini.tpl          # sim template (QuicPoolStart 22200, AdvertiseHost ok.tasia.work.gd)
  generate_configs.py                   # QUIC_REAL_CERT=1 python generate_configs.py
  generated\
    deploy.json                         # sims[] {name,port,quic_port,http_port,x,y,size,region_uuid,folder}
    robust\Robust.ini                   # [ClientStack.Quic] CertificatePath ./SSL/quic/quic-cert.p12 + [QuicProxy] pem/key
    robust\SSL\quic\quic-cert.pem       # CN=not.let-us.cyou (1 intermediate) / fallback CN=fresh.metaverse
    sims\<Name>\OpenSim.ini              # [ClientStack.Quic] Port AdvertiseHost ProxyRegistrationURL
    sims\<Name>\data\OpenSimManaged.log  # per-sim managed log
  bin\                                  # live binaries (lib64 for Bullet/ODE)
```

`generated\robust\Robust.log` vs `bin\Robust.log` vs `generated\robust\data\RobustManaged.log` — current Robust (`generated\robust\Robust.dll`) logs to `generated\robust\Robust.log` + `bin\Robust.log` depending on launch cwd. Always check both.

---

## 4. What Is Working (verified 2026-09-15)

| Area | Status | Proof |
|------|--------|-------|
| **Robust TCP** | ✅ | `netstat 0.0.0.0:22000/22001 LISTENING` · `GET 127.0.0.1:22000/get_grid_info 200` |
| **QUIC proxy UDP** | ✅ (after fix) | `netstat UDP 0.0.0.0:22002 pid 19568` · `Robust.log 04:20:07 QUIC proxy listening on port 22002` |
| **Cert watcher** | ✅ | `Robust.log 04:20:07 Certificate watcher active for SSL/quic/quic-cert.pem (auto-reload)` + `Certificate context created CN=not.let-us.cyou` |
| **Win10 QUIC runtime** | ✅ | `msquic.dll 4180512 FDBCDCD4` + `System.Net.Quic.dll 284944 81CB8552` + `System.Private.CoreLib.dll 13175048 09C58983` from `quic-windows10-selfcontained-bin-replace\bin\` |
| **Region DB** | ✅ | `SELECT quicHost,quicPort FROM regions` → `ok.tasia.work.gd 22203`/`22208..22229`, 0 `NULL` after re-generate |
| **Manager** | ✅ | `H:\grid\FreshMetaverseManager\bin\Release\net8.0-windows\FreshMetaverseManager.exe` pid `31640` · `UseShellExecute false RedirectStandardInput true CreateNoWindow HideWindows` · `ShowWindow SW_HIDE/SW_SHOW` |
| **MoneyD queries** | ✅ | fixed `tasia_moneyd.py` → `presence`/`griduser`/`useraccounts` cols `UserID/SessionID/SecureSessionID`; `POST 127.0.0.1:1026 501` normal · `GET healthz` ok |
| **Helper bridge** | ✅ | `config.json xmlrpc_passthrough upstream http://127.0.0.1:1026/` · `:8015/healthz 200` · `:8082/ostools/currency.php 200` |
| **OAR smart bulk** | ✅ | `OarMapping {Region,OarPath}` + `BulkOarRestoreMappedAsync parallel 2` + `ChkOarDirectPath` + `ProgressOarSmart` |
| **Sites** | ✅ | `amber/password.php` `amber/map-data.php` `search/index.php` 200 |
| **Snapshot push** | ✅ | `e22305b→4c9923f→08511db→05c630e` `tasia/fresh-metaverse-snapshot` |

---

## 5. What Was Fixed This Cycle

### 5.1 RegionInfo QUIC fallback (`OpenSim\Framework\RegionInfo.cs`)

`ReadNiniConfig(source.Configs["ClientStack.Quic"])` where `source = regions/*.ini` never had the section → `QuicHost="" QuicPort=0` → DB `NULL` for all 30. Patched to fallback to `globalSource.Configs["ClientStack.Quic"]` so `[ClientStack.Quic] AdvertiseHost ok.tasia.work.gd` in `OpenSim.ini` is actually read. Re-generated → 30 rows pushed. Also fixes `No packets for 60000ms Disconnecting`.

### 5.2 Manager stop PowerShell quoting (`FreshMetaverseManager\MainWindow.xaml.cs`)

`JsonSerializer.Serialize(script)` escapes `'` → `\u0027` → `Where-Object { $_.Name -like \u0027python*\u0027 }` → `ParserError`. Fixed via `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` `s_psArgsOptions` for all 5 sweeps (money/helper/robust/apache/kill) + always sweep external `python* tasia_moneyd.py` / `*Robust.dll*` / `httpd.exe`.

### 5.3 Sims msquic pin (`H:\grid\igrid-package\bin\msquic.dll`)

`bin\msquic.dll 512KB 04/15/2026` (inbox Schannel Win11-only) vs `generated\robust\msquic.dll 4083KB 12/12/2025` (OpenSSL Win10-compatible `FDBCDCD4`). Sim log `Failed to start QUIC listener: Current Windows version 10.0.19045 not supported. Minimal 10.0.20145` while Robust `Certificate context created` ok. Fixed by copying robust `4083KB` → `bin\msquic.dll` + `quic-windows10-selfcontained-bin-replace\bin\` as source.

### 5.4 Certificate auto-reload (this cycle)

* **Robust** `OpenSim\Server\Handlers\QuicProxyConnector.cs`: `FileSystemWatcher m_certWatcher/m_certKeyWatcher` + `Timer 1min` + `OnCertFileChanged` debounce `800ms` + `TryReloadCertificate(reason)` → `BuildFreshCertificateContext() exportableEphemeral` → `StartCertificateWatcher()` after `StartListener()`. Log: `Certificate watcher active` / `Certificate reloaded`.
* **Sims** `OpenSim\Region\ClientStack\Linden\Quic\QuicServerConfig.cs`: `m_cachedContext m_certLastWriteUtc m_keyLastWriteUtc m_certLock` → `LoadCertificate()` mtime-cached, returns ephemerally exportable `X509Certificate2` for MsQuic.
* Verified: `build\Release\OpenSim.Server.Handlers.dll 209920` contains `Certificate watcher` utf16 + `Certificate reloaded` utf16.

### 5.5 Npgsql alignment

`bin\Npgsql.dll 8.0.5 1374KB` vs required `9.0.3 1396KB` → `Failed to load plugin OpenSim.Data.IRegionData ... Npgsql Version=9.0.3.0 not found`. Fixed by copying `C:\Users\2fast\.nuget\packages\npgsql\9.0.3\lib\net8.0\Npgsql.dll` → `bin\` + `generated\robust\`.

### 5.6 Build output path gotcha

`Directory.Build.props` sets `<OutputPath>$(SolutionDir)/build/$(Configuration)/$(AssemblyName)/` — so `dotnet build OpenSim.sln -c Release` outputs to `build\Release\` not `bin\Release\net8.0`. `obj\Release\...` is intermediate. Fixed sync: `robocopy build\Release → bin\ + generated\robust\ /XF msquic.dll` then **re-patch** `System.Net.Quic.dll` + `msquic.dll` + `System.Private.CoreLib.dll` from `quic-windows10-selfcontained-bin-replace\bin\` + re-apply `Npgsql 9.0.3` + watcher Handlers. See incident below.

### 5.7 Win10 runtime restore incident (2026-09-15 04:06-04:20)

Blind `robocopy build\Release → igrid-package\bin\ + generated\robust\` overwrote patched runtime:

* **Working** `System.Net.Quic.dll 284944 81CB8552` + `msquic 4083 FDBCDCD4` → `QUIC proxy listening` at `02:54`, `03:19`, `03:23`
* **Broken** `System.Net.Quic.dll 284456 BE9EB071` + `msquic 512 99DAF179` → `Failed to start: not supported on this platform 10.0.19045 minimal 10.0.20145` at `04:06`, `04:16`

Fixed by restoring full `quic-windows10-selfcontained-bin-replace\bin\` (371 files differ stock vs patch — we now patch only QUIC-relevant three + keep app dlls) to both `bin` + `generated\robust`, then re-applying watcher + Npgsql + app dlls. Verified at `04:20:05 pid 19568` → `TCP 22000/22001` + `UDP 22002` + `QUIC proxy listening` + `Certificate watcher active`.

**Rule going forward:** never `robocopy build\Release` without `/XF System.Net.Quic.dll msquic.dll System.Private.CoreLib.dll System.Net.Security.dll` OR always **re-patch** those 3 + `msquic` immediately after.

---

## 6. Current Live State (2026-09-15 04:20)

* **Robust:** `dotnet "H:\grid\igrid-package\generated\robust\Robust.dll"` cwd `generated\robust` pid `19568` `04:20:05` — `TCP 22000/22001 LISTENING` + `UDP 22002 *:* 19568` (12 UDP lines = QUIC sockets). Log `generated\robust\Robust.log` tail `04:20:07 Certificate watcher active`.
* **Sims:** not started (all `offline` in manager) — ready to `Start All` via manager `31640`. `Ground_Zero` etc. `OpenSimManaged.log` expected `QuicServer Initialized port=22203` once started.
* **Manager:** `FreshMetaverseManager.exe 31640 03:31:56` alive — `Robust: offline→online` check via `http://127.0.0.1:22000`.
* **Money/Helper/Apache:** `moneyd` + `helper` stopped before cert work; `Apache httpd` stopped. XAMPP `apache\logs\httpd.pid` stale cleaned by manager.
* **DB:** `auth` table uses `uuid,passwordHash,passwordSalt,webLoginKey,accountType` (not `PrincipalID`); `useraccounts` has 5 rows lowercase UUIDs; `balances` tested `INSERT ... ON CONFLICT`.

---

## 7. Known Risks / Constraints

* `H:\grid\xampp\htdocs` & `H:\grid\FreshMetaverseManager` are **not** Git repos — deploy via manual copy into `Docs\deployment-examples\fresh-metaverse\` then `git push`.
* Repo dirty `obj\Release\ build\` must not be staged.
* `msquic.dll` locked while sims/robust running — `Stop All` before copy.
* `Flexible` MySQL modules bring their own `Npgsql` version — keep both `bin` + `generated\robust` in sync (currently `9.0.3`).
* Native QUIC viewer path still requires viewer with QUIC support; Robusto fallback `BridgeViewerQuicToSimUdpAsync` exists for LLUDP.
* Cert files are `SSL\quic\quic-cert.pem` + `quic-key.pem` + `quic-cert.p12` (password `f353ebda…` in `Robust.ini`) — `quic-cert.pem` currently `CN=not.let-us.cyou` with 1 intermediate (02:34), not `fresh.metaverse` self-signed. Watcher monitors `quic-cert.pem` + `quic-key.pem` mtime.

---

## 8. Verification Checklist (run before claiming green)

```powershell
# 1) dlls
python -c "d=open(r'H:\grid\igrid-package\generated\robust\OpenSim.Server.Handlers.dll','rb').read(); print('watcher', 'Certificate watcher'.encode('utf-16le') in d)"
Get-FileHash H:\grid\igrid-package\generated\robust\System.Net.Quic.dll -Algorithm SHA256   # expect 81CB8552ABCC8287...
Get-FileHash H:\grid\igrid-package\generated\robust\msquic.dll -Algorithm SHA256            # expect FDBCDCD48F6CDBD8...
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("H:\grid\igrid-package\generated\robust\Npgsql.dll").FileVersion  # expect 9.0.3.0

# 2) Robust
netstat -ano | Select-String "0.0.0.0:22000|0.0.0.0:22001"
netstat -ano -p UDP | Select-String "22002"
Get-Content H:\grid\igrid-package\generated\robust\Robust.log -Tail 20 | Select-String "QuicProxy|Certificate watcher|QUIC proxy listening"

# 3) Touch cert → auto-reload without restart (after 1-2 min)
Copy-Item H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem H:\grid\igrid-package\generated\robust\SSL\quic\quic-cert.pem -Force
# wait 90s then:
Select-String -LiteralPath H:\grid\igrid-package\generated\robust\Robust.log -Pattern "Certificate reloaded"

# 4) Sim QUIC after Start All in manager
netstat -ano -p UDP | Select-String "22203|22208"
Get-Content H:\grid\igrid-package\generated\sims\Ground_Zero_*\data\OpenSimManaged.log -Tail 20 | Select-String "QuicServer|Failed to start QUIC"

# 5) Login (viewer external)
#   Login URI http://os.tasia.work.gd:22000  (remote) / http://127.0.0.1:22000 locally
```

---

## 9. File Inventory (key)

| Path | Role | LastWrite |
|------|------|-----------|
| `H:\grid\work\fixtest\OpenSim\Server\Handlers\QuicProxyConnector.cs` | watcher `TryReloadCertificate` | 2026-09-15 03:29 |
| `H:\grid\work\fixtest\OpenSim\Region\ClientStack\Linden\Quic\QuicServerConfig.cs` | `m_cachedContext` mtime cache | 2026-09-15 03:29 |
| `H:\grid\work\fixtest\OpenSim\Framework\RegionInfo.cs` | `globalSource` QUIC fallback | 2026-09-15 03:29 |
| `H:\grid\work\fixtest\build\Release\OpenSim.Server.Handlers.dll` | 205KB watcher build | 2026-09-15 03:56 |
| `H:\grid\igrid-package\bin\OpenSim.Server.Handlers.dll` | deployed watcher | 2026-09-15 03:56 |
| `H:\grid\igrid-package\generated\robust\Robust.dll` | live Robust | 2026-09-15 03:38 |
| `H:\grid\igrid-package\generated\robust\System.Net.Quic.dll` | **patched 284944 81CB8552** | 2026-03-19 22:37 |
| `H:\grid\igrid-package\bin\msquic.dll` | **4083KB FDBCDCD4** | 2025-12-12 02:00 |
| `H:\grid\quic-windows10-selfcontained-bin-replace\bin\` | patch source (371 diffs) | 2026-03-19 22:37 |
| `H:\grid\FreshMetaverseManager\MainWindow.xaml.cs` | manager `UnsafeRelaxedJsonEscaping` stop sweeps | 2026-09-15 |
| `H:\grid\work\fixtest\Docs\deployment-examples\fresh-metaverse\` | snapshot | push `05c630e` |
| `C:\Users\2fast\AppData\Local\Temp\opencode\msquic-4083.dll` | safe msquic backup | 2026-09-15 04:08 |

---

## 10. How To Operate

* **Start:** manager → `Start Robust` (uses `generated\robust\Robust.dll`) → verify `TCP 22000` + `UDP 22002` → `Start Money + Helper` → `Start Apache` → `Start All Sims` (parallel 3).
* **Restart/Shutdown:** Backup tab `Backup` (console `oar backup`) vs `Bulk Tasia flow` (`backup` → 60s → `tasia-ngc/restart schedule 60s`). Do **not** mix.
* **OAR restore:** Smart tab `Same OAR→many` or mapped `BulkOarRestoreMappedAsync` (parallel 2, direct vs copy).
* **Deploy after code change:** `dotnet build OpenSim.sln -c Release` → `robocopy build\Release → bin\ + generated\robust\ /XF msquic.dll System.Net.Quic.dll System.Private.CoreLib.dll System.Net.Security.dll` → **re-patch** `msquic 4083` + `System.Net.Quic 81CB8552` + `System.Private.CoreLib 09C58983` from `quic-windows10-selfcontained-bin-replace\bin\` → re-apply `Npgsql 9.0.3` → re-apply `OpenSim.Server.Handlers` watcher → restart Robust → check `UDP 22002`.

---

*Prepared by Tasia for Mom — Windows 10 QUIC-safe.*
