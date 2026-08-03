@echo off
setlocal
cd /d "%~dp0"

rem Debug build + launch for development / Debug MCP.
dotnet build VpsLimitMonitor.slnx -c Debug
if errorlevel 1 exit /b 1

dotnet publish tools\VpsLimitMonitorMcp\VpsLimitMonitorMcp.csproj -c Debug
if errorlevel 1 (
    if exist bin\VpsLimitMonitorMcp.exe (
        echo Warning: VpsLimitMonitorMcp.exe is in use; kept the existing copy.
    ) else (
        exit /b 1
    )
)

start "" "%~dp0bin\VpsLimitMonitor.exe"
endlocal
