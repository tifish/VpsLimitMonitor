@echo off
cd /d "%~dp0"
dotnet publish VpsLimitMonitor\VpsLimitMonitor.csproj -c Release
if errorlevel 1 exit /b 1
dotnet publish tools\VpsLimitMonitorMcp\VpsLimitMonitorMcp.csproj -c Release
