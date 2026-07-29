@echo off
cd /d "%~dp0"
dotnet build VpsLimitMonitor.slnx -c Debug
if errorlevel 1 exit /b 1

rem Publish the MCP adapter as a single file into bin (single-file keeps the
rem runtimeconfig inside the exe, so NetBeauty leaves it alone). An agent
rem session may hold the old exe open; keep the existing copy on failure.
dotnet publish tools\VpsLimitMonitorMcp\VpsLimitMonitorMcp.csproj -c Release
if errorlevel 1 (
    if exist bin\VpsLimitMonitorMcp.exe (
        echo Warning: VpsLimitMonitorMcp.exe is in use; kept the existing copy.
    ) else (
        exit /b 1
    )
)
