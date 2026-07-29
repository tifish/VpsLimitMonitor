@echo off
cd /d "%~dp0"
call "%~dp0Build.cmd"
if errorlevel 1 exit /b 1
start "" "%~dp0bin\VpsLimitMonitor.exe"
