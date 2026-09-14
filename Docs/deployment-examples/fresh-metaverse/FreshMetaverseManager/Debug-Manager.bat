@echo off
cd /d "%~dp0"
echo Starting Fresh Metaverse Manager...
echo Working dir: %CD%
dir bin\Release\net8.0-windows\FreshMetaverseManager.exe
echo ---
bin\Release\net8.0-windows\FreshMetaverseManager.exe 2>&1
echo Exit code: %ERRORLEVEL%
pause
