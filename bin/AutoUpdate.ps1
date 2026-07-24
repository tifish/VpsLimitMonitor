# Called by AutoUpdater.LaunchInstall with the staged package directory.
# Waits for the app in this directory to exit, mirrors the new files in
# (keeping user data), restarts the app, then removes the staging directory.
param([Parameter(Mandatory = $true)][string]$StagedPackageDir)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$appDir = $PSScriptRoot
$appName = 'VpsLimitMonitor'
$appExe = Join-Path $appDir "$appName.exe"
$stagedPackageDir = [IO.Path]::GetFullPath($StagedPackageDir)
$stagedExe = Join-Path $stagedPackageDir "$appName.exe"

function Get-CurrentInstallProcesses {
    Get-Process -Name $appName -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq [IO.Path]::GetFullPath($appExe))
            }
            catch {
                $false
            }
        }
}

try {
    if (-not (Test-Path -LiteralPath $stagedPackageDir -PathType Container)) {
        throw "Staged package directory does not exist: $stagedPackageDir"
    }
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "Staged package is missing $appName.exe: $stagedPackageDir"
    }
    if ($stagedPackageDir.TrimEnd('\') -ieq $appDir.TrimEnd('\')) {
        throw 'Staged package directory must differ from the install directory.'
    }

    # Only wait for the instance running from this directory; the app exits
    # immediately after launching this script.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-CurrentInstallProcesses)) {
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (Get-CurrentInstallProcesses) {
        throw "Timed out waiting for $appExe to exit."
    }

    # Robocopy exit codes below 8 indicate success, including files copied or
    # removed. Keep user data directories outside the mirror operation.
    & robocopy $stagedPackageDir $appDir /MIR /XD Logs Config /R:10 /W:1 | Out-Null
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -ge 8) {
        throw "Robocopy failed with exit code $robocopyExitCode."
    }

    Start-Process -FilePath $appExe -WorkingDirectory $appDir

    # Staged layout is <temp>\<app>-update\package; remove the whole staging root.
    Remove-Item (Split-Path $stagedPackageDir) -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
catch {
    [Console]::Error.WriteLine("Auto-update failed: $($_.Exception.Message)")
    [Console]::Error.WriteLine("Staged files were preserved at: $stagedPackageDir")
    if ((-not (Get-CurrentInstallProcesses)) -and (Test-Path -LiteralPath $appExe -PathType Leaf)) {
        Start-Process -FilePath $appExe -WorkingDirectory $appDir
    }
    exit 1
}
