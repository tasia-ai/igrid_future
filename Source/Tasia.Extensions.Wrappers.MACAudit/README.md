# NGC MAC Audit Module

The MAC Audit add-on wraps the login service to capture the viewer MAC address (or its hashed representation) whenever a user authenticates. Each successful login is appended to a JSONL audit log and can optionally be forwarded to syslog or an HTTPS endpoint.

## Installation

1. Build the project and deploy `TasiaAddon.MACAudit.dll` alongside the other Robust services.
2. Update the Robust configuration so that the login service uses the decorator:
   ```
   [LoginService]
   LocalServiceModule = TasiaAddon.MACAudit.MACAuditLoginService
   InnerLoginService = OpenSim.Services.LLLoginService.dll:LLLoginService
   ```
3. Add the `[NGC.MACAudit]` section to your configuration (see below) and restart Robust.

## Configuration

```
[NGC.MACAudit]
Enable = true
LogPath = ./logs/ngc-mac-audit.jsonl
RotateDaily = true
MaxSizeMB = 256
RetentionDays = 30
StoreHashedMAC = true
HashSalt = change-me-please
IncludeIP = true
SyslogEndpoint =
HTTPSinkEndpoint =
HTTPSinkAuthHeader =
InnerLoginService = OpenSim.Services.LLLoginService.dll:LLLoginService
```

* **Enable** – master switch for the module.
* **LogPath** – JSONL file that receives audit entries. The module enforces `0600` permissions where supported.
* **RotateDaily / MaxSizeMB / RetentionDays** – file rotation policy.
* **StoreHashedMAC** – when `true`, persist a salted SHA-256 hash and `macTail` (last four hex digits). When `false`, the raw MAC is stored.
* **HashSalt** – per-grid salt used when hashing MAC addresses.
* **IncludeIP** – include the client IP address in the record.
* **SyslogEndpoint** – optional `host:port` pair for UDP syslog delivery.
* **HTTPSinkEndpoint** – optional HTTPS endpoint that receives JSON payloads (retries with exponential backoff).
* **HTTPSinkAuthHeader** – optional `Authorization` header value for the HTTP sink.
* **InnerLoginService** – underlying login service type. Defaults to `OpenSim.Services.LLLoginService.LLLoginService`.

## Record format

Each audit line is emitted as JSON similar to:

```
{
  "timestamp": "2024-05-14T18:23:42.123456Z",
  "userId": "9be4...",
  "username": "Example Resident",
  "sessionId": "...",
  "secureSessionId": "...",
  "regionHandle": 9223372036854775808,
  "simAddress": "192.0.2.42",
  "simPort": 9000,
  "viewer": "Firestorm 7.1.9",
  "macAddress": "f2f9...",
  "macTail": "eeff",
  "ipAddress": "203.0.113.15"
}
```

## Testing

The unit tests in `Tests/TasiaAddon.MACAudit.Tests` validate configuration parsing and the audit writer. Run them with `dotnet test`.
