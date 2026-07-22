@echo off
cd /d "%~dp0"
dotnet build VpsLimitMonitor.slnx -c Debug
