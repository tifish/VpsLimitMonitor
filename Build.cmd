@echo off
setlocal
cd /d "%~dp0"

rem Release build into bin\. Cleans stale outputs; strips PDBs.
taskkill /f /im "VpsLimitMonitor.exe" >nul 2>nul

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\*.dll" "bin\*.pdb" "bin\Libs\*" 2>nul
rd /s /q "bin\Logs" 2>nul

dotnet build VpsLimitMonitor.slnx -c Release
if errorlevel 1 exit /b 1

rem Publish the MCP adapter as a single file into bin.
dotnet publish tools\VpsLimitMonitorMcp\VpsLimitMonitorMcp.csproj -c Release
if errorlevel 1 (
    if exist bin\VpsLimitMonitorMcp.exe (
        echo Warning: VpsLimitMonitorMcp.exe is in use; kept the existing copy.
    ) else (
        exit /b 1
    )
)

del /q /s bin\*.pdb 2>nul

endlocal
