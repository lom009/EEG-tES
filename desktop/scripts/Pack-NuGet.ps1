<#
.SYNOPSIS
Builds nine reusable libraries into pack_nuget at the solution root. Never uploads.
.EXAMPLE
.\scripts\Pack-NuGet.ps1
.EXAMPLE
.\scripts\Pack-NuGet.ps1 -Version '1.2.3-rc.1+build.5' -Overwrite
#>
[CmdletBinding()]
param([AllowNull()][AllowEmptyString()][string]$Version, [switch]$Overwrite)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NuGetPack.Common.psm1') -Force
$root = Split-Path -Parent $PSScriptRoot
try {
    $resolve = @{ RepoRoot = $root }
    if ($PSBoundParameters.ContainsKey('Version')) { $resolve.Version = $Version }
    $resolved = Resolve-NuGetPackVersion @resolve
    Invoke-NuGetPack -RepoRoot $root -ResolvedVersion $resolved -Overwrite:$Overwrite
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
