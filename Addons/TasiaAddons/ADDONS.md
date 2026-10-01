# Tasia Addons Documentation

This document describes all addon modules available in the Tasia OpenSim distribution.

## Table of Contents

1. [Installation](#installation)
2. [Addon Modules](#addon-modules)
   - [AccessLogger](#accesslogger)
   - [LoginSecurity](#loginsecurity)
   - [AbuseReports](#abusereports)
   - [Marketplace](#marketplace)
   - [WoWonder](#wowonder)
   - [AlertNotifications](#alertnotifications)
   - [ChatAudit](#chataudit)
   - [MACAudit](#macaudit)
   - [RemoteSound](#remotesound)
   - [RestartModule](#restartmodule)
3. [Voice Chat Configuration](#voice-chat-configuration)
4. [OpenSim.ini Examples](#opensimini-examples)
5. [Robust.ini Examples](#robustini-examples)

---

## Installation

All addon modules are built as DLLs and use the plugin.json + reflection loading system (not Mono.Addins).

### Step 1: Copy DLLs

Copy all `TasiaAddons.*.dll` files to your `bin/` directory:

```bash
cp TasiaAddons.*.dll /path/to/opensim/bin/
```

### Step 2: Configure OpenSim.ini

Each addon has its own configuration section in `OpenSim.ini`. See the examples below.

### Step 3: Enable the Module

Some addons require you to change the service module in `[LoginService]` or other sections.

---

## Addon Modules

### AccessLogger

Logs all login attempts with detailed information for security and auditing.

**What it does:**
- Logs every login attempt to `Data/access.log`
- Records: avatar name, IP address, MAC address, hardware ID, grid URI (hypergrid), client version

**Log Format:**
```
2024-01-15 10:30:00 UTC|LOGIN|FirstName LastName|192.168.1.1|MAC|id0|grid_uri|client_version|channel
2024-01-15 10:30:00 UTC|LOGIN_RESULT|FirstName LastName|SUCCESS
2024-01-15 10:30:00 UTC|LOGIN_RESULT|FirstName LastName|FAILED
```

**Configuration (OpenSim.ini):**
```ini
[LoginService]
    ; IMPORTANT: Change to use AccessLogger
    LocalServiceModule = TasiaAddons.AccessLogger.dll:AccessLoggerLoginService

[AccessLogger]
    ; Path to log file (default: Data/access.log)
    LogFile = Data/access.log
```

**Use Case:** Parse the log with PHP to ban problematic users or entire grids by blocking their grid URI.

---

### LoginSecurity

Provides IP banning, hardware banning, and Terms of Service prompt on login.

**What it does:**
- IP address blocking
- Hardware ID (MAC, id0) blocking
- ToS prompt during login (user must accept before entering)
- Custom welcome message from URL

**Configuration (OpenSim.ini):**
```ini
[LoginService]
    ; Keep existing or change if needed
    LocalServiceModule = OpenSim.Services.LLLoginService.dll:LLLoginService

[AccessControl]
    ; Enable the module
    Enabled = true
    
    ; Banfile - one IP per line in Data/banned_ips.txt
    ; BanFile = Data/banned_ips.txt
    
    ; Hardware ban file - MAC addresses or id0 hardware IDs
    ; HardwareBanned = Data/banned_hardware.txt
    
    ; Enable ToS prompt
    ; EnableToS = true
    ; ToSText = "Welcome! Accept our Terms of Service to enter."
    ; ToSURL = https://example.com/tos

[MessageUrl]
    ; Optional: Rotate welcome message from URL
    ; Enabled = false
    ; Url = https://example.com/welcome.txt
    ; Fallback = "Welcome to our grid!"
```

---

### AbuseReports

Allows users to submit abuse reports directly in-world.

**What it does:**
- In-world abuse report dialog
- Web-based abuse report submission
- Report history and management

**Configuration (OpenSim.ini):**
```ini
[AbuseReports]
    ; Enable the module
    Enabled = true
    
    ; Web URL for abuse reports (optional)
    ; AbuseReportURL = https://yourgrid.com/abuse
    
    ; Email for abuse notifications (optional)
    ; AbuseEmail = admin@yourgrid.com
```

---

### Marketplace

Handles prim delivery for marketplace-style purchases.

**What it does:**
- Delivers purchased items to users
- API for marketplace integration
- Transaction logging

**Configuration (OpenSim.ini):**
```ini
[Marketplace]
    ; Enable the module
    Enabled = true
    
    ; Marketplace URL
    ; MarketplaceURL = https://marketplace.example.com
    
    ; API key for authentication
    ; ApiKey = your-secret-key
```

---

### WoWonder

Integration with WoWonder social network platform.

**What it does:**
- IM API endpoint at `/api/v1/im/send`
- User info sync with WoWonder
- Activity feeds

**Configuration (OpenSim.ini):**
```ini
[WoWonder]
    ; Enable the module
    Enabled = true
    
    ; WoWonder site URL
    ; SiteURL = https://your-wonder-site.com
    
    ; API key
    ; ApiKey = your-wonder-api-key
    
    ; IM service enabled
    ; InstantMessageService = true
```

**API Endpoint:**
```
POST /api/v1/im/send
Headers:
  X-API-Key: your-wonder-api-key
Body:
  {
    "to": "username",
    "message": "Hello!",
    "from": "avatar_name"
  }
```

---

### AlertNotifications

Send push notifications to users via external API.

**What it does:**
- In-world alert notifications
- External API integration for push notifications
- Batch notifications to all users

**Configuration (OpenSim.ini):**
```ini
[AlertNotifications]
    ; Enable the module
    Enabled = true
    
    ; External notification API URL
    ; NotificationURL = https://api.example.com/notifications
    
    ; API key
    ; ApiKey = your-api-key
```

---

### ChatAudit

Logs all chat and instant messages to a file.

**What it does:**
- Records all local chat messages
- Records all IMs (both sent and received)
- Records region and position of sender
- HTTP API to retrieve recent messages

**Configuration (OpenSim.ini):**
```ini
[ChatAudit]
    ; Enable the module
    Enabled = true
    
    ; Path to log file
    LogFile = Data/chat-audit.log
    
    ; API endpoint path
    ApiPath = /addons/chat-audit
    
    ; API token for authentication
    ApiToken = your-secret-token
    
    ; Number of recent entries to keep in memory
    RecentEntryLimit = 500
```

**HTTP API:**
```
GET /addons/chat-audit?token=your-secret-token&limit=100&type=chat
```

---

### MACAudit

Logs MAC addresses and hardware IDs of connecting users.

**What it does:**
- Records MAC address, id0 (hardware ID), IP address
- Useful for identifying and banning hardware
- Login audit trail

**Configuration (OpenSim.ini):**
```ini
[MACAudit]
    ; Enable the module
    Enabled = true
    
    ; Path to log file
    LogFile = Data/mac-audit.log
```

---

### RemoteSound

Allows playing remote audio URLs in-world via script function.

**What it does:**
- `osNgcPlaySoundURL(url, volume, target, cacheOverride)` OSSL function
- Streams audio from external URLs
- Cache control for bandwidth optimization
- Rate limiting per object and region

**Configuration (OpenSim.ini):**
```ini
[RemoteSound]
    ; Enable the module
    Enabled = true
    
    ; Max concurrent streams per region
    ; MaxStreams = 10
    
    ; Cache duration in seconds (default: 3600)
    ; CacheDuration = 3600
    
    ; Rate limiting - max requests per minute per object
    ; ObjectRateLimit = 5
    
    ; Rate limiting - max requests per minute per region
    ; RegionRateLimit = 60
```

**Script Usage:**
```csharp
// Play sound at full volume to nearby users
osNgcPlaySoundURL("https://example.com/sound.mp3", 1.0, "", 0.0);

// Play sound to specific avatar
osNgcPlaySoundURL("https://example.com/music.mp3", 0.5, "avatar-uuid", 300.0);
```

---

### RestartModule

Enhanced region restart with countdown dialogs.

**What it does:**
- Region restart with countdown notices
- Bluebox dialogs to users
- Configurable restart intervals

**Commands:**
```
region restart notice <delta seconds>+
region restart abort [<message>]
```

**Configuration (OpenSim.ini):**
```ini
[RestartModule]
    ; Path for restart marker file (optional)
    ; MarkerPath = Data
    
    ; Skip delay if region is empty
    ; SkipDelayOnEmptyRegion = false
    
    ; Full reboot on inworld restart
    ; InworldRestartShutsDown = false
```

---

## Voice Chat Configuration

OpenSim supports multiple voice backends:

### Option 1: VivoxVoice (Recommended for production)

Vivox is the same technology used by Second Life. Requires a Vivox account.

**OpenSim.ini:**
```ini
[VivoxVoice]
    ; Enable the module
    enabled = true
    
    ; Your Vivox account details
    vivox_server = www.foobar.vivox.com
    vivox_sip_uri = foobar.vivox.com
    vivox_admin_user = your_admin_username
    vivox_admin_password = your_admin_password
    
    ; Channel type: "positional" (spatial) or "channel" (conference)
    vivox_channel_type = positional
```

**Get Vivox Account:**
1. Go to https://www.vivox.com/
2. Register for an account
3. Create an admin user
4. Note your server URL and credentials

---

### Option 2: FreeSwitchVoice

Open-source voice server. More complex to set up but free.

**OpenSim.ini:**
```ini
[FreeSwitchVoice]
    ; Enable the module
    enabled = true
    
    ; FreeSwitch server URL
    ; freeswitch_server = 127.0.0.1
    
    ; Port (default: 8021)
    ; freeswitch_port = 8021
    
    ; Password for FreeSwitch ESL
    ; freeswitch_password = rocks
    
    ; Default realm
    ; freeswitch_realm = your-grid.com
```

**FreeSwitch Setup:**
```bash
# Install FreeSwitch
apt-get install freeswitch

# Configure in /etc/freeswitch/vars.xml
# Set your domain in <X-PRE-PROCESS cmd="set" data="domain=your-grid.com"/>

# Start FreeSwitch
freeswitch -nonat
```

---

### Option 3: WebRTC (Janus)

For browser-based voice in hypergrid.

**OpenSim.ini:**
```ini
[WebRtcVoice]
    ; Module configuration
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    
    ; Janus server URL
    ; JanusURL = http://janus.example.com:8088
    
    ; API secret for Janus
    ; JanusApiSecret = your-secret
```

**Janus Setup:**
```bash
# Install Janus WebRTC gateway
# See https://janus.conf.meetecho.com/

# Configure with your OpenSim grid URL
```

---

## OpenSim.ini Examples

### Minimal Standalone Configuration

```ini
[Architecture]
    Include-Architecture = Standalone/Standalone.ini
    Include-Storage = Standalone/StandaloneStorage.ini

[Database Service]
    StorageProvider = OpenSim.Data.SQLite.dll
    ConnectionString = "URI=file:OpenSim.db,version=3"

[LoginService]
    LocalServiceModule = OpenSim.Services.LLLoginService.dll:LLLoginService
    WelcomeMessage = "Welcome to Tasia Grid!"

[AccessControl]
    Enabled = true
    BanFile = Data/banned_ips.txt

[ChatAudit]
    Enabled = true
    LogFile = Data/chat-audit.log
    ApiToken = change-this-token

[VivoxVoice]
    enabled = true
    vivox_server = www.your-vivox.com
    vivox_sip_uri = your-grid.vivox.com
    vivox_admin_user = your-admin
    vivox_admin_password = your-password
```

### Hypergrid Configuration

```ini
[Architecture]
    Include-Architecture = StandaloneHypergrid/StandaloneHypergrid.ini

[Database Service]
    StorageProvider = OpenSim.Data.MySQL.dll
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=your-password;SslMode=none"

[LoginService]
    LocalServiceModule = OpenSim.Services.LLLoginService.dll:LLLoginService

[Hypergrid]
    UserAgent = "TasiaGrid/1.0"
    GridURL = "https://yourgrid.com"
    GridName = Tasia Grid
    GatekeeperURI = "https://yourgrid.com:8002"

[AccessLogger]
    LogFile = Data/access.log

[ChatAudit]
    Enabled = true
    LogFile = Data/chat-audit.log

[MACAudit]
    Enabled = true
    LogFile = Data/mac-audit.log
```

---

## Robust.ini Examples

For grid mode (Robust server), add services:

### Robust.ini - Login Service

```ini
[LoginService]
    ; Point to your login service DLL
    LoginServiceModule = OpenSim.Services.LLLoginService.dll:LLLoginService
    
    ; Database for user accounts
    UserAccountService = OpenSim.Services.UserAccountService.dll:UserAccountService
    
    ; Authentication service
    AuthenticationService = OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService
```

### Robust.ini - Grid Service

```ini
[GridService]
    GridServiceModule = OpenSim.Services.GridService.dll:GridService
    
    ; Database
    StorageProvider = OpenSim.Data.MySQL.dll
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=your-password;SslMode=none"
```

### Robust.ini - Voice Connector

```ini
[ServiceConnectors]
    ; Enable voice connector
    LoginService = "8002/OpenSim.Server.Handlers.dll:LLLoginServiceInConnector"
    GridService = "8003/OpenSim.Server.Handlers.dll:GridServiceInConnector"
    
    ; Voice - uncomment for Freeswitch
    ; VoiceConnector = "8004/OpenSim.Server.Handlers.dll:FreeswitchServerConnector"
```

---

## Quick Start Checklist

1. **Copy DLLs** to `bin/`:
   ```bash
   cp TasiaAddons.*.dll bin/
   ```

2. **Configure OpenSim.ini** - Add sections for each addon you want to enable

3. **Enable Services** - Change `[LoginService]` module if using AccessLogger

4. **Test** - Start OpenSim and check logs

5. **Voice** - Configure `[VivoxVoice]` or `[FreeSwitchVoice]` section

---

## Troubleshooting

### Addons not loading
- Check `plugin.json` exists in addon directory
- Verify DLL is in `bin/` folder
- Check OpenSim console for errors

### AccessLogger not working
- Ensure `Data/` directory exists and is writable
- Check file permissions

### Voice not working
- Verify Vivox/FreeSwitch credentials
- Check network connectivity to voice server
- Review voice module logs

### ChatAudit API returns empty
- Verify `ApiToken` matches in config and request
- Check the log file exists

---

For additional help, check the individual addon README files or visit https://opensimulator.org/
