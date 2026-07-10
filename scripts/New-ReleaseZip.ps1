[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',

    [ValidateSet('win-x64', 'win-x86')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'MdfToBakConverter.csproj'
$artifactsDirectory = Join-Path $projectRoot 'artifacts'
$releaseName = "MdfToBakConverter-$Version-$Runtime"
$publishDirectory = Join-Path $artifactsDirectory $releaseName
$zipPath = Join-Path $artifactsDirectory "$releaseName.zip"

if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "Project file was not found: $projectFile"
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Restoring packages for $Runtime..."
dotnet restore $projectFile --runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing $releaseName..."
dotnet publish $projectFile `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Release ZIP created: $zipPath" -ForegroundColor Green
