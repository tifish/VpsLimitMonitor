@echo off
cd /d "%~dp0"
dotnet build VpsLimitMonitor.slnx -c Debug
if errorlevel 1 exit /b 1
start "" "%~dp0bin\VpsLimitMonitor.exe"
