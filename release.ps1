#Requires -Version 7
[CmdletBinding()]
param(
    [string]$DistRoot = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'

$pluginJson   = Get-Content "$PSScriptRoot\plugin.json" | ConvertFrom-Json
$pluginId     = $pluginJson.id
$assemblyName = $pluginJson.entryAssembly -replace '\.dll$', ''
$version      = $pluginJson.version

$project       = "$PSScriptRoot\$assemblyName.csproj"
$publishOutput = "$PSScriptRoot\bin\publish"
$OutputPath    = "$DistRoot\$pluginId"

Write-Host "Publishing $assemblyName v$version..."
dotnet publish $project -c Release -o $publishOutput --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath | Out-Null

Get-ChildItem $publishOutput -Filter "*.dll" |
    Copy-Item -Destination $OutputPath

Copy-Item "$publishOutput\$assemblyName.deps.json" $OutputPath
Copy-Item "$PSScriptRoot\plugin.json"              $OutputPath

Write-Host "Release v$version ready at: $OutputPath"
