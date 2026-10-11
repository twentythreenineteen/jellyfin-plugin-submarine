param(
    [string]$Version = "1.0.0.0",
    [string]$TargetAbi = "10.9.0.0"
)

$ErrorActionPreference = "Stop"
$dotnet = if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
} else {
    "dotnet"
}

Write-Host "Building Submarine v$Version (Release)..." -ForegroundColor Cyan
& $dotnet build "$PSScriptRoot\Jellyfin.Plugin.Submarine\Jellyfin.Plugin.Submarine.csproj" -c Release

$distDir = Join-Path $PSScriptRoot "dist"
$stageDir = Join-Path $distDir "Submarine_$Version"
$zipPath = Join-Path $distDir "Submarine_$Version.zip"

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item "$PSScriptRoot\Jellyfin.Plugin.Submarine\bin\Release\net8.0\Jellyfin.Plugin.Submarine.dll" -Destination $stageDir -Force

$meta = [ordered]@{
    category    = "Subtitles"
    changelog   = "Submarine v$Version release."
    description = "Plex-style on-the-fly subtitle search, download, and multi-format conversion in the playback subtitle menu across all platforms."
    guid        = "8b6a2e4c-9f1d-4e3a-b8c7-1d2e3f4a5b6c"
    name        = "Submarine"
    overview    = "Search, download, convert, and stream subtitles on-the-fly during playback across all Jellyfin platforms."
    owner       = "Submarine"
    targetAbi   = $TargetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $true
    assemblies  = @("Jellyfin.Plugin.Submarine.dll")
} | ConvertTo-Json -Depth 4

Set-Content -Path (Join-Path $stageDir "meta.json") -Value $meta -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -Force
$md5 = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()

Write-Host "`nPackage ready!" -ForegroundColor Green
Write-Host "  ZIP Path : $zipPath"
Write-Host "  MD5 Hash : $md5"
