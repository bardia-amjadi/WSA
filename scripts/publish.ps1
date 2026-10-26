#requires -Version 5.1
<#
.SYNOPSIS  Publishes the unpackaged, self-contained x64 build and zips it.
.OUTPUTS   artifacts/WSA-win-x64.zip, artifacts/WSA-build-info.txt
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts/publish'
$zipPath = Join-Path $root 'artifacts/WSA-win-x64.zip'

Push-Location $root
try {
    if (Test-Path (Join-Path $root 'artifacts')) { Remove-Item (Join-Path $root 'artifacts') -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    dotnet publish src/WSA.App/WSA.App.csproj -c $Configuration -r win-x64 -p:Platform=x64 `
        -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path (Join-Path $publishDir 'WSA.exe'))) { throw 'Publish finished but WSA.exe is missing.' }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $info = @(
        "WSA build info",
        "Version:   $Version",
        "Commit:    $(git rev-parse HEAD 2>$null)",
        "Built:     $([DateTime]::UtcNow.ToString('o'))",
        "dotnet:    $(dotnet --version)",
        "Package:   unpackaged, self-contained (.NET + Windows App SDK), win-x64",
        "MSIX:      not produced (see README, Packaging)"
    )
    $info | Set-Content -Path (Join-Path $root 'artifacts/WSA-build-info.txt') -Encoding utf8
}
finally {
    Pop-Location
}
