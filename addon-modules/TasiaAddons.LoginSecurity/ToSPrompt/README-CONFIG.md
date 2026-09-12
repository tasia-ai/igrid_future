# ToS Module Configuration

## Two configs needed

### 1. `[ToS]` section (in config-include/regions/AccessControl.ini)
Used by **ToSPromptModule** — console commands (`tos status`, `tos reset`, etc.)

```ini
[ToS]
; Terms of Service - shows once to users until they accept
; URL points to nginx → tos-server container
Enabled = false
TOS_URL = https://tos.yourdomain.com
TOS_Date = 1
StorageProvider = "TasiaAddons.LoginSecurity.dll:TasiaAddons.LoginSecurity.Data.SQLiteToSAcceptanceData"
ConnectionString = "Data Source=Data/accesscontrol.db;Version=3"
```

### 2. MACAudit config (Source/Tasia.Extensions.Wrappers.MACAudit/ngc-macaudit.ini)
Used by **MACAuditLoginService** — actually **blocks login** if ToS not accepted:

```ini
; Terms of Service check - blocks login if ToS not accepted
EnableToSCheck = false
ToSVersion = 1
ToSUrl = https://tos.yourdomain.com
ToSAcceptanceService = TasiaAddons.LoginSecurity.dll:TasiaAddons.LoginSecurity.Data.SQLiteToSAcceptanceData
ToSConnectionString = Data Source=Data/accesscontrol.db;Version=3
```

## Quick start

1. Set both configs with your domain URL
2. Set `Enabled = true` / `EnableToSCheck = true`
3. Build ToS server: `cd addon-modules/TasiaAddons.LoginSecurity/ToSPrompt && docker build -t tos-server .`
4. Run: `docker run -d -p 8099:8099 -v tos-data:/data --name tos-server tos-server`
5. Set up nginx proxy (see sstats_fix/docker/nginx-tos.conf)
6. Restart sim
7. Test: try logging in — viewer should pop up TOS dialog

## Console commands (when ToSPromptModule enabled)

```
tos status          — show current ToS version and URL
tos version         — show current ToS version
tos set version 2   — bump version (forces re-acceptance)
tos reset           — reset ALL users (everyone must re-accept)
tos reset user John — reset one user
tos check John      — check if user accepted current version
tos url             — show/set ToS URL
```

## Files

- `addon-modules/TasiaAddons.LoginSecurity/ToSPrompt/tos.md` — ToS content (edit this!)
- `addon-modules/TasiaAddons.LoginSecurity/ToSPrompt/tos_server.py` — Python server
- `addon-modules/TasiaAddons.LoginSecurity/ToSPrompt/Dockerfile` — Docker build
- `sstats_fix/docker/nginx-tos.conf` — nginx proxy config
