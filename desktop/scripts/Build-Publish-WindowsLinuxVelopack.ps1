[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$SourceBaseAddress,

    [string]$PublishPath = '',

    [AllowNull()]
    [AllowEmptyCollection()]
    [string[]]$RuntimeIdentifiers
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path (Split-Path -Parent $scriptRoot) 'TestArtifacts\Publish'
}
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This entry script must run on Windows. Use Build-Publish-MacOSVelopack.ps1 on macOS.'
}

Import-Module (Join-Path $scriptRoot 'VelopackBuild.Common.psm1') -Force

$allowedRuntimeIdentifiers = @(
    'win-x86',
    'win-x64',
    'win-arm64',
    'linux-x64',
    'linux-arm64')
$resolvedRuntimeIdentifiers = @(Resolve-VelopackRuntimeIdentifiers `
    -RuntimeIdentifiers $RuntimeIdentifiers `
    -AllowedRuntimeIdentifiers $allowedRuntimeIdentifiers `
    -DefaultRuntimeIdentifiers $allowedRuntimeIdentifiers)

Invoke-VelopackBuildAndPublish `
    -Version $Version `
    -SourceBaseAddress $SourceBaseAddress `
    -PublishPath $PublishPath `
    -RuntimeIdentifiers $resolvedRuntimeIdentifiers
