[CmdletBinding()]
param(
    [AllowNull()][AllowEmptyString()]
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
    $PublishPath = Join-Path (Split-Path -Parent $scriptRoot) 'TestArtifacts/Publish'
}
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::OSX)) {
    throw 'Velopack macOS packages must be created on a macOS host.'
}

Import-Module (Join-Path $scriptRoot 'VelopackBuild.Common.psm1') -Force

$allowedRuntimeIdentifiers = @('osx-x64', 'osx-arm64')
$resolvedRuntimeIdentifiers = @(Resolve-VelopackRuntimeIdentifiers `
    -RuntimeIdentifiers $RuntimeIdentifiers `
    -AllowedRuntimeIdentifiers $allowedRuntimeIdentifiers `
    -DefaultRuntimeIdentifiers $allowedRuntimeIdentifiers)

$versionArguments = @{}
if ($PSBoundParameters.ContainsKey('Version')) {
    $versionArguments.Version = $Version
}

Invoke-VelopackBuildAndPublish @versionArguments `
    -SourceBaseAddress $SourceBaseAddress `
    -PublishPath $PublishPath `
    -RuntimeIdentifiers $resolvedRuntimeIdentifiers
