@echo off
title Fresh Metaverse Manager
cd /d "%~dp0"
start "" ".\bin\Release\net8.0-windows\FreshMetaverseManager.exe"
if errorlevel 1 (
  echo Failed to start ? trying dotnet run...
  dotnet bin\Release\net8.0-windows\FreshMetaverseManager.dll
  pause
)
