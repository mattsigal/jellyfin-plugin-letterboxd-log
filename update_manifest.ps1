$zipPath = Join-Path $PSScriptRoot "LetterboxdLog.zip"
$manifestPath = Join-Path $PSScriptRoot "manifest.json"

if (-not (Test-Path $zipPath)) {
    Write-Error "Zip file not found at $zipPath. Run build_plugin.ps1 first."
    exit 1
}

# Calculate MD5 Checksum (Compatibility with older Jellyfin)
$fileHash = Get-FileHash -Path $zipPath -Algorithm MD5
$checksum = $fileHash.Hash.ToLower()

# Calculate Timestamp
$timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ"

# Update manifest.json
$manifest = Get-Content $manifestPath | ConvertFrom-Json
$manifest[0].versions[0].checksum = $checksum
$manifest[0].versions[0].timestamp = $timestamp

$manifest | ConvertTo-Json -Depth 10 -AsArray | Set-Content $manifestPath

Write-Host "Manifest updated with checksum: $checksum" -ForegroundColor Green
Write-Host "Updated timestamp to: $timestamp" -ForegroundColor Green
