@echo off
cd /d "%~dp0"
dotnet publish VpsLimitMonitor\VpsLimitMonitor.csproj -c Release
