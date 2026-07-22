# VpsLimitMonitor installer
# Usage: irm https://raw.githubusercontent.com/tifish/VpsLimitMonitor/main/install.ps1 | iex
# For a private repo, set $env:GITHUB_TOKEN first.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'tifish/VpsLimitMonitor'
$appName = 'VpsLimitMonitor'
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"

$apiHeaders = @{ 'User-Agent' = 'VpsLimitMonitor-installer' }
if ($env:GITHUB_TOKEN) {
    $apiHeaders['Authorization'] = "Bearer $env:GITHUB_TOKEN"
}

Write-Host "Fetching latest release of $repo..."
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $apiHeaders
$asset = $release.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
if (-not $asset) {
    throw "No zip asset found in release $($release.tag_name)."
}

Write-Host "Downloading $($asset.name) ($($release.tag_name))..."
$zipPath = Join-Path $env:TEMP "$appName-install.zip"
if ($env:GITHUB_TOKEN) {
    # Private repos require downloading through the asset API endpoint.
    $downloadHeaders = $apiHeaders.Clone()
    $downloadHeaders['Accept'] = 'application/octet-stream'
    Invoke-WebRequest $asset.url -Headers $downloadHeaders -OutFile $zipPath
} else {
    Invoke-WebRequest $asset.browser_download_url -Headers $apiHeaders -OutFile $zipPath
}

# Stop the running instance installed in the target directory, if any.
Get-Process -Name $appName -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host 'Stopping running instance...'
        $_ | Stop-Process -Force
        $_.WaitForExit()
    }

Write-Host "Installing to $installDir..."
New-Item -ItemType Directory -Force $installDir | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $installDir -Force
Remove-Item $zipPath -Force

$exePath = Join-Path $installDir "$appName.exe"
if (-not (Test-Path $exePath)) {
    throw "Installation failed: $exePath not found."
}

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

Write-Host "Done. Installed $($release.tag_name) to $installDir (auto-starts at login)."
