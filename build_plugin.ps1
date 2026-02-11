$ErrorActionPreference = "Stop"

# 1. Check for .NET SDK
try {
    dotnet --version
}
catch {
    Write-Error "Microsoft .NET SDK is not installed. Please install .NET 9.0 SDK to proceed."
    exit 1
}

# 2. Build the Plugin
Write-Host "Building Plugin..." -ForegroundColor Cyan
$projectPath = "LetterboxdSync\LetterboxdSync.csproj"
dotnet publish $projectPath -c Release -o .\dist

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build Failed!"
    exit 1
}

Write-Host "Build Successful! Artifacts are in .\dist" -ForegroundColor Green

# 3. Create Plugin Zip for Distribution
Write-Host "Creating zip archive..." -ForegroundColor Cyan
$zipPath = ".\LetterboxdLog.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path .\dist\* -DestinationPath $zipPath -Force

Write-Host "Zip created: $zipPath" -ForegroundColor Green

# 3. Deployment Instructions
Write-Host "`nTo Deploy to NAS:" -ForegroundColor Yellow
Write-Host "1. Copy the DLL to your NAS:"
Write-Host "   scp .\dist\LetterboxdLog.dll user@192.168.0.224:/var/packages/Jellyfin/target/var/data/plugins/LetterboxdLog/"
Write-Host "   (Note: You may need to adjust the destination path based on your Synology install)"
Write-Host "2. Restart Jellyfin on the NAS."
