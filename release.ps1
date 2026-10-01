#Requires -Version 5.0
<#
.SYNOPSIS
    Builds the mod in Release and packages GameData into a versioned zip.

.DESCRIPTION
    Builds the project (which makes KSPBT populate the
    plugin and the GameData .version file), reads the resulting version, and
    zips the GameData folder into Builds\<modName>-<version>.zip.

    No external tools required.
#>

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot

# Derive the mod name from the .version file at the repo root.
$versionFiles = @(Get-ChildItem -Path $root -Filter *.version -File)
if ($versionFiles.Count -ne 1) {
    throw "Expected exactly one .version file in $root, found $($versionFiles.Count)."
}
$modName = $versionFiles[0].BaseName

# Build the backend and the UI in Release.
Write-Host "Building $modName (Release)..." -ForegroundColor Cyan
dotnet build "$root\$modName.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# Read the version KSPBT wrote into the packaged .version file.
$versionFile = Join-Path $root "GameData\$modName\$modName.version"
$version = (Get-Content $versionFile -Raw | ConvertFrom-Json).VERSION
Write-Host "Version: $version" -ForegroundColor Cyan

# Package GameData into Builds\<mod>-<version>.zip.
$buildsDir = Join-Path $root "Builds"
New-Item -ItemType Directory -Force -Path $buildsDir | Out-Null

$zip = Join-Path $buildsDir "$modName-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# Written by hand rather than with Compress-Archive, which in Windows PowerShell separates folders with
# backslashes, against the zip spec. The .pdb files come along: they put line numbers in logs from Unity's
# development player, which modders use, though the normal player ignores them.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, "Create")
try {
    Get-ChildItem (Join-Path $root "GameData") -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($root.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
        Write-Host "  $entry"
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Created $zip" -ForegroundColor Green
