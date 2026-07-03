# Upgrade Plan - Migration from Legacy Modules

## Overview

This document outlines the migration of features from legacy OpenSim forks (obsolete/, obsolete2/) and unityviewer to the current orig/ fork.

---

## 1. Features to Migrate

### 1.1 From `obsolete/` folder

| Feature | Source Location | Description | Priority |
|---------|---------------|-------------|----------|
| **IP and Hardware Banning** | `OpenSim/Services/AccessControlService/` | Ban users by IP, MAC address, or hardware ID | HIGH |
| **ToS Prompt during Login** | `OpenSim/Services/LLLoginService/LLLoginService.cs` | Terms of Service acceptance during login flow | HIGH |

### 1.2 From `obsolete2/` folder

| Feature | Source Location | Description | Priority |
|---------|---------------|-------------|----------|
| **Abuse Reports Service** | `OpenSim/Services/AbuseReportsService/` | Service for handling abuse reports from users | HIGH |
| **Region Restart Notice** | `OpenSim/Region/Framework/` | Notify users when region is about to restart | MEDIUM |

### 1.3 From `unityviewer/` folder

| Feature | Source Location | Description | Priority |
|---------|---------------|-------------|----------|
| **Mesh Rendering** | `Assets/Scripts/RenderPrimitive.cs, RenderAvatar.cs` | Enhanced mesh support for avatars and primitives | LOW |
| **Streaming Assets** | `Assets/StreamingAssets/` | Unity streaming assets structure | LOW |

---

## 2. Implementation Plan

### Phase 1: Core Login Features (HIGH Priority)

#### 2.1 IP and Hardware Banning

**Files to create:**
- `OpenSim/Services/Interfaces/IAccessControlService.cs` (interface)
- `OpenSim/Services/AccessControlService/AccessControlService.cs` (implementation)
- `OpenSim/Services/AccessControlService/AccessControlServiceBase.cs` (base class)

**Database changes:**
- Add tables: `bannedIPs`, `bannedMacs`, `bannedID0s`

**Config sections:**
```ini
[AccessControlService]
Enabled = true
BanDatabaseConnection = ...
```

**OpenSim touch points:**
- Modify `LLLoginService.cs` to call `IAccessControlService.IsIPBanned()` and `IsHardwareBanned()`
- Add console commands: `ban ip`, `unban ip`, `ban mac`, `unban mac`, etc.

#### 2.2 ToS Prompt During Login

**Files to modify:**
- `OpenSim/Services/LLLoginService/LLLoginService.cs`
- `OpenSim/Services/Interfaces/IUserAccountService.cs` (add TOSDate field)

**Database changes:**
- Add `TOSDate` column to UserAccounts table

**Config sections:**
```ini
[LoginService]
TOS_URL = https://example.com/tos
TOS_Date = 20240101
```

---

### Phase 2: Abuse Reports (HIGH Priority)

#### 2.3 Abuse Reports Service

**Files to create:**
- `OpenSim/Services/Interfaces/IAbuseReportsService.cs`
- `OpenSim/Services/AbuseReportsService/AbuseReportsService.cs`
- `OpenSim/Services/AbuseReportsService/AbuseReportsServiceBase.cs`
- `OpenSim/Server/Handlers/AbuseReportsHandler.cs`

**Database changes:**
- Add `abuse_reports` table

**Config sections:**
```ini
[AbuseReportsService]
LocalServiceModule = OpenSim.Services.AbuseReportsService.dll:AbuseReportsService
```

**OpenSim touch points:**
- Add region module to handle abuse report dialogs
- Add HTTP handler for abuse report submissions

---

### Phase 3: Region Features (MEDIUM Priority)

#### 2.4 New Region Restart Notice

**Files to create:**
- `OpenSim/Region/Framework/Interfaces/IRestartNoticeModule.cs`
- `OpenSim/Region/Modules/RestartNotice/RestartNoticeModule.cs`

**Features:**
- Notify users X seconds before region restart
- Show countdown dialog
- Handle graceful script suspension

---

## 3. Migration Strategy

### Step-by-step approach:

1. **Create new addon project structure** under `orig/addon-modules/`
   ```
   addon-modules/
   ├── TasiaAddons.LoginSecurity/
   │   ├── AccessControlService/
   │   └── ToSPrompt/
   ├── TasiaAddons.AbuseReports/
   │   ├── Service/
   │   └── Handler/
   └── TasiaAddons.RegionFeatures/
       └── RestartNotice/
   ```

2. **Copy source files** from obsolete/ and obsolete2/ (read-only source material)

3. **Adapt to latest OpenSim** by:
   - Updating namespaces
   - Fixing API changes
   - Updating config file formats

4. **Test each module** independently

---

## 4. Files to Copy/Adapt

### From obsolete/
```
OpenSim/Services/AccessControlService/
├── AccessControlService.cs
├── AccessControlServiceBase.cs
└── AccessControlServiceConnector.cs

OpenSim/Services/LLLoginService/LLLoginService.cs
  (extract TOS-related code sections)

OpenSim/Services/Interfaces/IAccessControlService.cs
```

### From obsolete2/
```
OpenSim/Services/AbuseReportsService/
├── AbuseReportsService.cs
├── AbuseReportsServiceBase.cs

OpenSim/Services/Connectors/AbuseReports/
└── AbuseReportsServicesConnector.cs

OpenSim/Services/Interfaces/IAbuseReportsService.cs

OpenSim/Region/Framework/Modules/RestartNotice/
└── (new - create based on existing patterns)
```

### From unityviewer/ (Reference only - C# Unity code)
```
Assets/Scripts/RenderAvatar.cs (mesh rendering reference)
Assets/Scripts/RenderPrimitive.cs (mesh rendering reference)
```
Note: UnityViewer code is C# for Unity, not compatible directly. Mesh rendering would require new implementation.

---

## 5. Build Integration

### Add to OpenSim.sln:
```bash
# Generate new solution entries
cd orig
./runprebuild.sh
```

### Config files to update:
- `OpenSim.ini` - Add module sections
- `Robust.ini` - Add service connectors
- `config-include/` - Add module configurations

---

## 6. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| API changes between OpenSim versions | HIGH | Careful diff of obsolete vs orig |
| Database schema changes | MEDIUM | Use migration scripts |
| Login service integration | HIGH | Test thoroughly with different clients |
| Breaking existing logins | HIGH | Add config toggle for new features |

---

## 7. Implementation Order

1. **AccessControlService** - Block malicious users first
2. **ToS Prompt** - Legal compliance
3. **Abuse Reports** - User safety
4. **Restart Notice** - User experience

---

## 8. TODO List

- [ ] Create addon-modules structure
- [ ] Copy AccessControlService from obsolete/
- [ ] Copy AbuseReportsService from obsolete2/
- [ ] Copy/restart notice module code
- [ ] Update LoginService to use new modules
- [ ] Add database migrations
- [ ] Test login flow
- [ ] Test abuse report submission
- [ ] Update configuration documentation

---

## Can I Start?

Yes, I can begin implementing these features. The recommended starting point is:

1. **Phase 1: AccessControlService** - Create the project structure, copy source, adapt to latest API
2. Then proceed through the phases

Would you like me to start with the AccessControlService implementation?

---

## IMPLEMENTATION PROGRESS

### Completed ✓

1. **AccessControlService** - Copied from `obsolete/`
   - Location: `addon-modules/TasiaAddons.LoginSecurity/AccessControlService/`
   - Files: AccessControlService.cs, AccessControlServiceBase.cs, AccessControlServiceConnector.cs
   - Interface: IAccessControlService.cs

2. **ToS Prompt Module** - Created new
   - Location: `addon-modules/TasiaAddons.LoginSecurity/ToSPrompt/`
   - File: ToSPromptModule.cs

3. **AbuseReportsService** - Copied from `obsolete2/`
   - Location: `addon-modules/TasiaAddons.AbuseReports/Service/`
   - Handler: `addon-modules/TasiaAddons.AbuseReports/Handler/`
   - Interface: IAbuseReportsService.cs

4. **Project Files Created**
   - TasiaAddons.LoginSecurity.csproj
   - TasiaAddons.AbuseReports.csproj

### Still Needed

- [ ] Update namespaces in copied files
- [ ] Create database migrations
- [ ] Integrate with LoginService
- [ ] Test build
- [ ] Add config examples
