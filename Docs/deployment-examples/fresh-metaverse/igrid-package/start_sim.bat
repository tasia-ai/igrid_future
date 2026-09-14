@echo off
REM I-Grid Fresh Metaverse - Region simulator starter (Windows)
REM Usage: start_sim.bat <RegionName>
REM Run from the Fresh Metaverse root directory (where bin/ is)

if "%~1"=="" (
    echo Usage: %~nx0 ^<RegionName^>
    echo Example: %~nx0 Fresh01
    echo.
    echo Available regions:
    for /d %%d in (generated\sims\*) do echo   %%~nxd
    exit /b 1
)

set REGION=%~1
set ROOT=%~dp0
cd /d "%ROOT%"

if not exist "generated\sims\%REGION%\OpenSim.ini" (
    echo ERROR: Region "%REGION%" not found in generated\sims\
    exit /b 1
)

echo Starting region %REGION% on port ...
echo Data directory: %ROOT%generated\sims\%REGION%\data
echo.

dotnet bin\OpenSim.dll

pause