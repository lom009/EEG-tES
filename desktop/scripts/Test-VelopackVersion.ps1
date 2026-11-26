[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VelopackBuild.Common.psm1') -Force
$module = Get-Module VelopackBuild.Common
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('velopack-version-' + [Guid]::NewGuid().ToString('N'))

function Assert-Equal($Actual, $Expected) {
    if ($Actual -cne $Expected) { throw "Expected '$Expected', got '$Actual'." }
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected failure containing '$Message'."
}

function Invoke-TestGit([string[]]$GitArguments) {
    & git -C $testRoot @GitArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Test git failed: $GitArguments" }
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Invoke-TestGit @('init', '--quiet')
    Invoke-TestGit @('config', 'user.name', 'Version Test')
    Invoke-TestGit @('config', 'user.email', 'version-test@example.invalid')
    Invoke-TestGit @('commit', '--allow-empty', '-m', 'Initial', '--quiet')

    foreach ($version in @('0.0.0', '1.2.3', '1.2.3-rc.1+build.005', '1.0.0-0', '1.0.0-01a')) {
        Assert-Equal (Resolve-VelopackVersion -Version $version -RepoRoot 'nonexistent-repository') $version
    }
    foreach ($version in @('', ' ', 'v1.2.3', 'V1.2.3', '01.2.3', '1.2', '1.2.3.4', '1.0.0-01', '1.0.0-a..b', '1.0.0+', "1.0.0`n")) {
        Assert-Fails { Resolve-VelopackVersion -Version $version -RepoRoot $testRoot } 'SemVer'
    }
    Assert-Fails { Resolve-VelopackVersion -RepoRoot $testRoot } 'HEAD has no valid'
    Assert-Fails { Resolve-VelopackVersion -RepoRoot (Join-Path $testRoot 'missing') } 'Failed to read Git'

    foreach ($tag in @('release-1.0.0', '1.0.0', 'v01.0.0', 'v1.0.0-01')) {
        Invoke-TestGit @('tag', $tag)
    }
    Assert-Fails { Resolve-VelopackVersion -RepoRoot $testRoot } 'HEAD has no valid'
    Invoke-TestGit @('tag', 'v1.2.3')
    Assert-Equal (Resolve-VelopackVersion -RepoRoot $testRoot) '1.2.3'
    Invoke-TestGit @('commit', '--allow-empty', '-m', 'Next', '--quiet')
    Assert-Fails { Resolve-VelopackVersion -RepoRoot $testRoot } 'HEAD has no valid'
    Invoke-TestGit @('tag', '-a', 'V2.0.0-rc.1+build.5', '-m', 'Annotated')
    Assert-Equal (Resolve-VelopackVersion -RepoRoot $testRoot) '2.0.0-rc.1+build.5'
    Invoke-TestGit @('tag', 'v2.0.0')
    Assert-Equal (Resolve-VelopackVersion -Version '3.0.0' -RepoRoot $testRoot) '3.0.0'

    # Exercise the real input wrapper in a separate non-interactive process.
    $hostExe = (Get-Process -Id $PID).Path
    $modulePath = (Join-Path $PSScriptRoot 'VelopackBuild.Common.psm1').Replace("'", "''")
    $quotedRoot = $testRoot.Replace("'", "''")
    $probe = "Import-Module '$modulePath'; try { Resolve-VelopackVersion -RepoRoot '$quotedRoot'; exit 2 } catch { if (`$_.Exception.Message -like '*Supply -Version explicitly*') { exit 0 }; exit 3 }"
    & $hostExe -NoProfile -NonInteractive -Command $probe
    Assert-Equal $LASTEXITCODE 0

    # Replace only the input boundary to verify retries and ordinal tag ordering.
    & $module {
        $script:answers = [Collections.Generic.Queue[string]]::new()
        foreach ($answer in @('bad', '0', '3', '2')) { $script:answers.Enqueue($answer) }
        function script:Read-VelopackTagSelection { param([int]$Count) return $script:answers.Dequeue() }
    }
    Assert-Equal (Resolve-VelopackVersion -RepoRoot $testRoot) '2.0.0'
    Assert-Equal (& $module { $script:answers.Count }) 0
    & $module {
        function script:Read-VelopackTagSelection { param([int]$Count) throw 'Input unavailable' }
    }
    Assert-Fails { Resolve-VelopackVersion -RepoRoot $testRoot } 'Supply -Version explicitly'

    # Verify the orchestrator resolves once before any build operation.
    & $module {
        $script:resolveCalls = 0
        function script:Resolve-VelopackVersion {
            param([string]$Version)
            $script:resolveCalls++
            if (-not $PSBoundParameters.ContainsKey('Version')) { throw 'Missing explicit version' }
            return $Version
        }
        function script:Assert-SourceBaseAddress {
            param([string]$SourceBaseAddress)
            if ($Version -cne '3.2.1-rc.1+build.5') { throw 'Version was not propagated' }
            throw 'Resolved before build'
        }
    }
    Assert-Fails {
        Invoke-VelopackBuildAndPublish -Version '3.2.1-rc.1+build.5' -SourceBaseAddress 'https://example.invalid' -PublishPath $testRoot -RuntimeIdentifiers win-x64
    } 'Resolved before build'
    Assert-Equal (& $module { $script:resolveCalls }) 1
    Write-Host 'All Velopack version tests passed.'
}
finally {
    Remove-Module VelopackBuild.Common -ErrorAction SilentlyContinue
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notmatch '^velopack-version-[0-9a-f]{32}$') {
        throw "Refusing to remove unexpected test path: $resolvedTestRoot"
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
