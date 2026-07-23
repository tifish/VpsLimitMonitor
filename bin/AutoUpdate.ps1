# Called by AutoUpdater.LaunchInstall with the staged package directory.
# Waits for the app in this directory to exit, mirrors the new files in
# (keeping user data), restarts the app, then removes the staging directory.
param([Parameter(Mandatory = $true)][string]$StagedPackageDir)

$appDir = $PSScriptRoot
$appName = 'VpsLimitMonitor'

# Only wait for the instance running from this directory; the app exits right
# after launching this script.
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    $running = Get-Process -Name $appName -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ((Split-Path $_.Path) -ieq $appDir) }
    if (-not $running) { break }
    Start-Sleep -Milliseconds 500
}

# Mirror new files in, drop files the old version no longer needs,
# keep user data directories.
robocopy $StagedPackageDir $appDir /MIR /XD Logs Config /R:10 /W:1 | Out-Null

Start-Process (Join-Path $appDir "$appName.exe") -WorkingDirectory $appDir

# Staged layout is <temp>\<app>-update\package; remove the whole staging root.
Remove-Item (Split-Path $StagedPackageDir) -Recurse -Force -ErrorAction SilentlyContinue
