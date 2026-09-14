@echo off
REM I-Grid Fresh Metaverse - Robust grid services starter (Windows)
REM Run from the Fresh Metaverse root directory (where bin/ is)

set ROOT=%~dp0
cd /d "%ROOT%"

echo Starting Robust grid services on port 22000...
echo Data directory: %ROOT%generated\robust\data
echo.

dotnet bin\Robust.dll

pause