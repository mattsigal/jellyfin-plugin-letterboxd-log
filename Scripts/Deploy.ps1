param(
    [string]$NasPath
)

$ProjectRoot = Resolve-Path "$PSScriptRoot\..\LetterboxdSync"
$BuildOutput = "$ProjectRoot\bin\Release\net9.0"

# 1. Build the project
Write-Host "Building LetterboxdSync..." -ForegroundColor Cyan
dotnet build "$ProjectRoot\LetterboxdSync.csproj" -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

# 2. Determine Destination
if ([string]::IsNullOrWhiteSpace($NasPath)) {
    Write-Host "`nPlease enter the full path to your Jellyfin 'plugins' directory on the NAS." -ForegroundColor Yellow
    Write-Host "Example: \\NAS\Jellyfin\plugins or Z:\Jellyfin\plugins" -ForegroundColor Gray
    $NasPath = Read-Host "Path"
}

if -not (Test-Path $NasPath) {
    Write-Error "Destination path does not exist: $NasPath"
    exit 1
}

$PluginDir = Join-Path $NasPath "LetterboxdSync"

if -not (Test-Path $PluginDir) {
    Write-Host "Creating plugin directory: $PluginDir"
    New-Item -ItemType Directory -Path $PluginDir | Out-Null
}

# 3. Copy Files
Write-Host "Copying files to $PluginDir..." -ForegroundColor Cyan
$FilesToCopy = @(
    "LetterboxdSync.dll",
    "LetterboxdSync.pdb",
    "LetterboxdSync.deps.json"
)

foreach ($File in $FilesToCopy) {
    $Source = Join-Path $BuildOutput $File
    if (Test-Path $Source) {
        Copy-Item $Source -Destination $PluginDir -Force
        Write-Host "Copied $File" -ForegroundColor Green
    } else {
        Write-Warning "File not found: $Source"
    }
}

Write-Host "`nDeployment complete! Please restart your Jellyfin server." -ForegroundColor Green
