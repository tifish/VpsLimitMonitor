# VpsLimitMonitor installer
# Usage: irm https://raw.githubusercontent.com/tifish/VpsLimitMonitor/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$appName = 'VpsLimitMonitor'
$assetUrl = "https://github.com/tifish/$appName/releases/latest/download/$appName-win-x64.zip"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"

# Same mirror list as JeekTools.NET GitHubMirrors (keep in sync).
$mirrors = @(
    $assetUrl,
    $assetUrl.Replace('https://github.com/', 'https://ghfast.top/https://github.com/'),
    $assetUrl.Replace('https://github.com/', 'https://gh-proxy.com/github.com/')
)

# Download with a speed floor: abort and fail over to the next mirror
# when the average speed stays below 0.5 MB/s (checked after the first 5 seconds).
function Get-FileWithSpeedCheck([string]$Url, [string]$OutFile) {
    $minBytesPerSec = 512KB
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.AllowAutoRedirect = $true
    $response = $request.GetResponse()
    try {
        $stream = $response.GetResponseStream()
        $file = [System.IO.File]::Create($OutFile)
        try {
            $buffer = New-Object byte[] 65536
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $total = 0
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $file.Write($buffer, 0, $read)
                $total += $read
                $seconds = $stopwatch.Elapsed.TotalSeconds
                if ($seconds -ge 5 -and ($total / $seconds) -lt $minBytesPerSec) {
                    throw "download speed below 0.5 MB/s"
                }
            }
        } finally {
            $file.Close()
        }
    } finally {
        $response.Close()
    }
}

$zipPath = Join-Path $env:TEMP "$appName-install.zip"
$downloaded = $false
foreach ($mirror in $mirrors) {
    Write-Host "Downloading $mirror ..."
    try {
        Get-FileWithSpeedCheck $mirror $zipPath
        $downloaded = $true
        break
    } catch {
        Write-Host "  Failed: $($_.Exception.Message)"
    }
}
if (-not $downloaded) {
    throw 'All mirrors failed.'
}

# Stop the running instance installed in the target directory only,
# leaving instances running from a development directory alone.
Get-Process -Name $appName -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host 'Stopping running instance...'
        $_ | Stop-Process -Force
        $_.WaitForExit()
    }

Write-Host "Installing to $installDir..."
$stageDir = Join-Path $env:TEMP "$appName-install"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $stageDir
Remove-Item $zipPath -Force

# Mirror new files in, drop files the new version no longer ships, keep user data.
robocopy $stageDir $installDir /MIR /XD Logs Config /R:10 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "robocopy failed with exit code $LASTEXITCODE."
}
Remove-Item $stageDir -Recurse -Force

$exePath = Join-Path $installDir "$appName.exe"
if (-not (Test-Path $exePath)) {
    throw "Installation failed: $exePath not found."
}

# Start Menu shortcut, plus a Startup shortcut so the tray monitor runs at login.
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutDir in @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup')
    )) {
    $shortcut = $shell.CreateShortcut((Join-Path $shortcutDir "$appName.lnk"))
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Save()
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$hasRuntime = $dotnet -and ((& dotnet --list-runtimes) -match '^Microsoft\.NETCore\.App 10\.')
if ($hasRuntime) {
    Write-Host 'Starting VpsLimitMonitor...'
    Start-Process $exePath -WorkingDirectory $installDir
} else {
    # Setup.cmd elevates, installs the .NET runtime, then starts the app.
    Write-Host '.NET 10 runtime not found, running Setup.cmd (needs elevation)...'
    Start-Process (Join-Path $installDir 'Setup.cmd') -WorkingDirectory $installDir
}

Write-Host "Done. Installed to $installDir (auto-starts at login)."
