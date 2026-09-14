@echo off
REM I-Grid Fresh Metaverse - Manager web console (Windows)
REM Run from the Fresh Metaverse root directory

set ROOT=%~dp0
cd /d "%ROOT%\manager"

echo Starting I-Grid Manager on http://localhost:8080
echo Web UI: http://localhost:8080
echo API auth token is in manager.json (generated on first run)
echo.

python manager.py --host 0.0.0.0 --port 8080

pause