; AccessLogger Configuration
; Add to OpenSim.ini

[AccessLogger]
    ; Path to the log file (default: Data/access.log)
    LogFile = Data/access.log

; IMPORTANT: To enable the logger, you must change the login service in OpenSim.ini:
;
; Find the [LoginService] section and change:
;    LocalServiceModule = OpenSim.Services.LLLoginService.dll:LLLoginService
; To:
;    LocalServiceModule = TasiaAddons.AccessLogger.dll:AccessLoggerLoginService
;
; Log format (Data/access.log):
; 2024-01-15 10:30:00 UTC|LOGIN|FirstName LastName|192.168.1.1|mac_address|hardware_id|grid_uri|client_version|channel
; 2024-01-15 10:30:00 UTC|LOGIN_RESULT|FirstName LastName|SUCCESS
; 2024-01-15 10:30:00 UTC|LOGIN_RESULT|FirstName LastName|FAILED
;
; Fields:
; - timestamp: UTC time of login attempt
; - event: LOGIN (attempt) or LOGIN_RESULT (result)
; - name: Avatar first and last name
; - IP: Client IP address
; - MAC: Client MAC address
; - hardware_id: Client hardware ID (id0)
; - grid_uri: For hypergrid logins, the originating grid's URI (scopeID)
; - client_version: Viewer client version
; - channel: Viewer channel (e.g., "Second Life Release")
