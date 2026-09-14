@echo off
setlocal EnableExtensions

set "GRID_ROOT=H:\grid\igrid-package"
set "BIN=%GRID_ROOT%\bin"
set "ROBUST_DIR=%GRID_ROOT%\generated\robust"
set "SIM_DIR=%GRID_ROOT%\generated\sims"
set "HG_DB=%GRID_ROOT%\generated\hg\hgauth.db"

:menu
cls
echo ============================================================
echo  Fresh Metaverse Grid Starter
echo ============================================================
echo.
echo  1. Status
echo  2. Start Robust + Quick-G
echo  3. Start HG Auth API
echo  4. Start MainLand01 only
echo  5. Start remaining active sims
echo  6. Start Robust + HG Auth + all active sims
echo  7. Stop all grid processes
echo  0. Exit
echo.
choice /c 12345670 /n /m "Select: "
if errorlevel 8 goto end
if errorlevel 7 goto stop_all
if errorlevel 6 goto start_all
if errorlevel 5 goto start_rest
if errorlevel 4 goto start_first
if errorlevel 3 goto start_hgauth
if errorlevel 2 goto start_robust
if errorlevel 1 goto status

:status
cls
echo ============================================================
echo  Status
echo ============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$names=@('dotnet.exe','OpenSim.exe','Robust.exe','python.exe','py.exe','Quick-G.exe'); 'Grid processes:'; Get-CimInstance Win32_Process | Where-Object { (($_.Name -in $names) -and ($_.CommandLine -match 'Robust|OpenSim|hgauth|Quick-G')) -or ($_.Name -eq 'Quick-G.exe') } | ForEach-Object { '  ' + $_.ProcessId + '  ' + $_.CommandLine }; ''; 'TCP ports:'; foreach($p in 22000,22001,22037,22063,19001,19002){ $c=Get-NetTCPConnection -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1; if($c){ '  ' + $p + ' -> PID ' + $c.OwningProcess } else { '  ' + $p + ' -> closed' } }; ''; 'UDP ports:'; foreach($p in 22002){ $u=Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1; if($u){ '  ' + $p + ' -> PID ' + $u.OwningProcess } else { '  ' + $p + ' -> closed' } }"
echo.
pause
goto menu

:start_robust
call :launch_robust
timeout /t 8 /nobreak >nul
call :launch_quickg
pause
goto menu

:start_hgauth
call :launch_hgauth
pause
goto menu

:start_first
call :launch_sim MainLand01_X2000Y2000
pause
goto menu

:start_rest
call :launch_sim Fresh_MetaVers_Racing_Simulator
call :launch_sim NintendoLand
call :launch_sim Fresh_MetaVerse_Archive
call :launch_sim Ground_Zero_X2000Y2002
call :launch_sim MainLand02_X1998Y2002
call :launch_sim MainLand04_X2000Y2004
call :launch_sim MainLand07_X2002Y1998
pause
goto menu

:start_all
call :launch_robust
timeout /t 8 /nobreak >nul
call :launch_quickg
timeout /t 2 /nobreak >nul
call :launch_hgauth
timeout /t 2 /nobreak >nul
call :launch_sim MainLand01_X2000Y2000
timeout /t 4 /nobreak >nul
call :launch_sim Fresh_MetaVers_Racing_Simulator
call :launch_sim NintendoLand
call :launch_sim Fresh_MetaVerse_Archive
call :launch_sim Ground_Zero_X2000Y2002
call :launch_sim MainLand02_X1998Y2002
call :launch_sim MainLand04_X2000Y2004
call :launch_sim MainLand07_X2002Y1998
pause
goto menu

:stop_all
echo Stopping Robust/OpenSim/HG auth/Quick-G processes...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$names=@('dotnet.exe','OpenSim.exe','Robust.exe','python.exe','py.exe','Quick-G.exe'); Get-CimInstance Win32_Process | Where-Object { (($_.Name -in $names) -and ($_.CommandLine -match 'Robust|OpenSim|hgauth|Quick-G')) -or ($_.Name -eq 'Quick-G.exe') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; 'Stopped PID ' + $_.ProcessId }"
pause
goto menu

:launch_robust
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "if(Get-CimInstance Win32_Process | Where-Object { ($_.Name -in @('dotnet.exe','Robust.exe')) -and ($_.CommandLine -match 'Robust.dll') }) { 'Robust already running.' } else { Start-Process -FilePath 'cmd.exe' -ArgumentList '/k','title FM Robust && dotnet Robust.dll' -WorkingDirectory '%ROBUST_DIR%'; 'Started Robust.' }"
exit /b

:launch_quickg
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$robust=Get-CimInstance Win32_Process | Where-Object { ($_.Name -in @('dotnet.exe','Robust.exe')) -and ($_.CommandLine -match 'Robust.dll') } | Select-Object -First 1; if(-not $robust) { 'Robust is not running; Quick-G not started.' } elseif(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'Quick-G.exe' }) { 'Quick-G already running.' } else { $args='/k title FM Quick-G && Quick-G.exe -listen-port 22002 -control-port 19001 -brain-bind 127.0.0.1 -brain-port 19002 -brain-lease-seconds 90 -region-port-start 22200 -region-port-end 22400 -region-port-exclude 22445 -cert SSL/quic/quic-cert.pem -key SSL/quic/quic-key.pem -alpn opensim-ll/1 -parent-pid ' + $robust.ProcessId; Start-Process -FilePath 'cmd.exe' -ArgumentList $args -WorkingDirectory '%ROBUST_DIR%'; 'Started Quick-G.' }"
exit /b

:launch_hgauth
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "if(Get-CimInstance Win32_Process | Where-Object { ($_.Name -in @('python.exe','py.exe')) -and ($_.CommandLine -match 'hgauth') }) { 'HG auth already running.' } else { Start-Process -FilePath 'cmd.exe' -ArgumentList '/k','title FM HG Auth && python "%GRID_ROOT%\manager\hgauth.py" --port 22063 --db "%HG_DB%"' -WorkingDirectory '%GRID_ROOT%\manager'; 'Started HG auth.' }"
exit /b

:launch_sim
set "SIM_NAME=%~1"
set "SIM_INI=%SIM_DIR%\%SIM_NAME%\OpenSim.ini"
if not exist "%SIM_INI%" (
    echo Missing sim ini: %SIM_INI%
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "if(Get-CimInstance Win32_Process | Where-Object { ($_.Name -in @('dotnet.exe','OpenSim.exe')) -and ($_.CommandLine -match [regex]::Escape('%SIM_NAME%')) }) { '%SIM_NAME% already running.' } else { Start-Process -FilePath 'cmd.exe' -ArgumentList '/k','title FM Sim - %SIM_NAME% && set "PATH=%BIN%\lib64;%%PATH%%" && dotnet OpenSim.dll -inifile="%SIM_INI%"' -WorkingDirectory '%BIN%'; 'Started %SIM_NAME%.' }"
exit /b

:end
endlocal
