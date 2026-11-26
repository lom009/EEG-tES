<# .SYNOPSIS
Bounded version/transaction tests. Does not build packages or access hardware.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NuGetPack.Common.psm1') -Force
$module = Get-Module NuGetPack.Common
$root = Join-Path ([IO.Path]::GetTempPath()) ('nuget pack tests ' + [Guid]::NewGuid().ToString('N'))
function Equal($actual, $expected) { if ($actual -cne $expected) { throw "Expected [$expected], got [$actual]" } }
function Fails([scriptblock]$action, [string]$message) {
    try { & $action | Out-Null } catch { if ($_.Exception.Message -notlike "*$message*") { throw }; return }
    throw "Expected error: $message"
}
function Git([string[]]$arguments) { [void](Invoke-PackProcess (Get-Command git -CommandType Application | Select-Object -First 1).Source $arguments $root 30) }
try {
    [void][IO.Directory]::CreateDirectory($root)
    foreach ($version in @('0.0.0', '1.2.3', '1.2.3-alpha.13', '1.2.3-rc.1+build.5', '1.2.3-a-b.0+001')) {
        Equal (Resolve-NuGetPackVersion -Version $version -RepoRoot $root).Full $version
    }
    foreach ($version in @('', ' ', 'v1.2.3', 'V1.2.3', '01.2.3', '1.2', '1.2.3.4', '1.0.0-01', '1.0.0-a..b', '1.0.0+', "1.0.0`n", '1.0.0;echo')) {
        Fails { Resolve-NuGetPackVersion -Version $version -RepoRoot $root } 'SemVer'
    }
    Fails { Resolve-NuGetPackVersion -Version '65535.0.0' -RepoRoot $root } 'limit'
    Equal (Resolve-NuGetPackVersion -Version '1.2.3-RC.1+build.5').Normalized '1.2.3-rc.1'
    # Explicit resolution must not consult Git at all.
    & $module { function script:Invoke-PackProcess { throw 'Git must not be invoked' } }
    Equal (Resolve-NuGetPackVersion -Version '1.2.3' -RepoRoot 'missing').Full '1.2.3'
    Import-Module (Join-Path $PSScriptRoot 'NuGetPack.Common.psm1') -Force
    $module = Get-Module NuGetPack.Common
    Git @('init', '--quiet')
    Git @('-c', 'user.name=Package Tests', '-c', 'user.email=tests@example.invalid', 'commit', '--allow-empty', '-m', 'First', '--quiet')
    Fails { Resolve-NuGetPackVersion -RepoRoot $root } 'exactly one'
    Git @('tag', 'release-1.0.0')
    Git @('tag', 'v1.0.0-01')
    Fails { Resolve-NuGetPackVersion -RepoRoot $root } 'exactly one'
    Git @('tag', '1.2.3')
    Git @('tag', 'v1.2.3')
    Git @('-c', 'user.name=Package Tests', '-c', 'user.email=tests@example.invalid', 'tag', '-a', '-f', 'v1.2.3', '-m', 'Annotated')
    Equal (Resolve-NuGetPackVersion -RepoRoot $root).Full '1.2.3'
    Git @('tag', 'v2.0.0-rc.1+build.5')
    Fails { Resolve-NuGetPackVersion -RepoRoot $root } 'exactly one'
    Equal (Resolve-NuGetPackVersion -Version '3.0.0' -RepoRoot $root).Full '3.0.0'
    Git @('-c', 'user.name=Package Tests', '-c', 'user.email=tests@example.invalid', 'commit', '--allow-empty', '-m', 'Next', '--quiet')
    Fails { Resolve-NuGetPackVersion -RepoRoot $root } 'exactly one'
    Git @('-c', 'user.name=Package Tests', '-c', 'user.email=tests@example.invalid', 'tag', '-a', 'V2.0.0-rc.1+build.5-new', '-m', 'Current')
    Equal (Resolve-NuGetPackVersion -RepoRoot $root).Full '2.0.0-rc.1+build.5-new'
    $hostExecutable = (Get-Process -Id $PID).Path
    Fails { Invoke-PackProcess $hostExecutable @('-NoProfile', '-Command', 'Start-Sleep -Seconds 10') $root 1 } 'timed out'

    $stage = Join-Path $root 'stage'
    $output = Join-Path $root 'output'
    [void][IO.Directory]::CreateDirectory($stage)
    [void][IO.Directory]::CreateDirectory($output)
    Set-Content (Join-Path $output 'a') 'old'
    Set-Content (Join-Path $output 'unrelated') 'keep'
    Set-Content (Join-Path $stage 'a') 'new'
    Fails { Publish-PackFiles $stage $output @('a') } 'exists'
    Equal (Get-Content (Join-Path $output 'a')) 'old'
    Fails { Publish-PackFiles $stage $output @('a', 'missing') -Overwrite } 'does not exist'
    Equal (Get-Content (Join-Path $output 'a')) 'old'
    Set-Content (Join-Path $stage 'a') 'new'
    Publish-PackFiles $stage $output @('a') -Overwrite
    Equal (Get-Content (Join-Path $output 'a')) 'new'
    Equal (Get-Content (Join-Path $output 'unrelated')) 'keep'

    # Exercise orchestration with fake dotnet, failing before any package can be published.
    foreach ($project in Get-NuGetPackProjects) {
        [void][IO.Directory]::CreateDirectory((Join-Path $root $project))
        Set-Content (Join-Path $root "$project/$project.csproj") '<Project />'
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'pack_nuget'))
    Set-Content (Join-Path $root 'pack_nuget/unrelated.nupkg') 'keep'
    & $module {
        function script:Invoke-PackProcess {
            param($FilePath, $Arguments, $WorkingDirectory, $TimeoutSeconds)
            if ($Arguments[0] -eq '--version') { return '10.0.401' }
            if ($Arguments[0] -eq 'pack') { throw 'Injected pack failure' }
            return ''
        }
    }
    Fails { Invoke-NuGetPack $root (Resolve-NuGetPackVersion -Version '1.2.3') } 'Injected pack failure'
    Equal (Get-Content (Join-Path $root 'pack_nuget/unrelated.nupkg')) 'keep'
    Equal @(Get-ChildItem (Join-Path $root 'pack_nuget') -Filter '.staging-*').Count 0
    Write-Host 'PASS: SemVer, HEAD tags, explicit/no-Git resolution, paths with spaces, failure and publication rollback.'
}
finally { Remove-PackTemporaryDirectory ([IO.Path]::GetTempPath()) $root }
