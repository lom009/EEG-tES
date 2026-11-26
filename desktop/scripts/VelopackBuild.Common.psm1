Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-VelopackTagSelection {
    param([int]$Count)

    if ([Console]::IsInputRedirected -or [Environment]::GetCommandLineArgs() -match '^-NonI(?:nteractive)?$') {
        throw 'Tag selection requires interactive input.'
    }
    return Read-Host "Select a tag number (1-$Count)"
}

function Resolve-VelopackVersion {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string]$Version,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )

    # SemVer 2.0.0: numeric prerelease identifiers cannot contain leading zeroes.
    $semVerPattern = '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z'
    if ($PSBoundParameters.ContainsKey('Version')) {
        if ([string]::IsNullOrEmpty($Version) -or $Version -cnotmatch $semVerPattern) {
            throw 'Version must be a SemVer 2.0.0 version without a v/V prefix (for example, 1.2.3-rc.1+build.5).'
        }
        Write-Host "Using version $Version from the explicit Version parameter."
        return $Version
    }

    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
        throw 'Git is required to resolve HEAD tags. Install Git or supply -Version explicitly.'
    }
    $tags = @(& git -C $RepoRoot tag --points-at HEAD)
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to read Git tags at HEAD. Check the repository or supply -Version explicitly.'
    }
    [string[]]$candidates = @($tags | Where-Object {
        $_ -cmatch '^[vV]' -and $_.Substring(1) -cmatch $semVerPattern
    })
    [Array]::Sort($candidates, [StringComparer]::Ordinal)
    if ($candidates.Count -eq 0) {
        throw 'HEAD has no valid v/V-prefixed SemVer tag. Create a tag on HEAD or supply -Version explicitly.'
    }

    $selected = $candidates[0]
    if ($candidates.Count -gt 1) {
        Write-Host 'Multiple version tags exist at HEAD:'
        for ($i = 0; $i -lt $candidates.Count; $i++) {
            Write-Host "  $($i + 1). $($candidates[$i]) -> $($candidates[$i].Substring(1))"
        }
        while ($true) {
            try {
                $answer = Read-VelopackTagSelection -Count $candidates.Count
            }
            catch {
                throw 'Cannot read a tag selection. Supply -Version explicitly for non-interactive builds.'
            }
            if ($null -eq $answer) {
                throw 'Cannot read a tag selection. Supply -Version explicitly for non-interactive builds.'
            }
            $selection = 0
            if ([int]::TryParse($answer, [ref]$selection) -and $selection -ge 1 -and $selection -le $candidates.Count) {
                $selected = $candidates[$selection - 1]
                break
            }
            Write-Host 'Invalid selection. Enter one of the displayed numbers.'
        }
    }
    $resolvedVersion = $selected.Substring(1)
    Write-Host "Using version $resolvedVersion from HEAD tag '$selected'."
    return $resolvedVersion
}

function Test-IsWindowsHost {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-IsLinuxHost {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)
}

function Test-IsMacOSHost {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Assert-SafeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedChild = [System.IO.Path]::GetFullPath($Child)
    if (-not $resolvedChild.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside '$resolvedParent': $resolvedChild"
    }
}

function Assert-SourceBaseAddress {
    param([Parameter(Mandatory = $true)][string]$SourceBaseAddress)

    $uri = $null
    if (-not [System.Uri]::TryCreate($SourceBaseAddress.Trim(), [System.UriKind]::Absolute, [ref]$uri) `
        -or $uri.Scheme -notin @('http', 'https') `
        -or -not [string]::IsNullOrEmpty($uri.Query) `
        -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'SourceBaseAddress must be an absolute HTTP/HTTPS address without a query or fragment.'
    }

    return $SourceBaseAddress.Trim().TrimEnd('/')
}

function Get-VelopackTarget {
    param([Parameter(Mandatory = $true)][string]$RuntimeIdentifier)

    switch ($RuntimeIdentifier) {
        'win-x86'     { return @{ Os = 'win';   Arch = 'x86';   FeedPath = 'win/x86';     MainExe = 'EGGtCSPlatform.Desktop.exe'; Icon = 'icon.ico' } }
        'win-x64'     { return @{ Os = 'win';   Arch = 'x64';   FeedPath = 'win/x64';     MainExe = 'EGGtCSPlatform.Desktop.exe'; Icon = 'icon.ico' } }
        'win-arm64'   { return @{ Os = 'win';   Arch = 'arm64'; FeedPath = 'win/arm64';   MainExe = 'EGGtCSPlatform.Desktop.exe'; Icon = 'icon.ico' } }
        'linux-x64'   { return @{ Os = 'linux'; Arch = 'x64';   FeedPath = 'linux/x64';   MainExe = 'EGGtCSPlatform.Desktop';     Icon = 'icon.png' } }
        'linux-arm64' { return @{ Os = 'linux'; Arch = 'arm64'; FeedPath = 'linux/arm64'; MainExe = 'EGGtCSPlatform.Desktop';     Icon = 'icon.png' } }
        'osx-x64'     { return @{ Os = 'osx';   Arch = 'x64';   FeedPath = 'osx/x64';     MainExe = 'EGGtCSPlatform.Desktop';     Icon = 'icon.icns' } }
        'osx-arm64'   { return @{ Os = 'osx';   Arch = 'arm64'; FeedPath = 'osx/arm64';   MainExe = 'EGGtCSPlatform.Desktop';     Icon = 'icon.icns' } }
        default       { throw "Unsupported RuntimeIdentifier '$RuntimeIdentifier'." }
    }
}

function Resolve-VelopackRuntimeIdentifiers {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]]$RuntimeIdentifiers,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$AllowedRuntimeIdentifiers,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$DefaultRuntimeIdentifiers
    )

    $resolved = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($RuntimeIdentifiers)) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        foreach ($part in $value.Split(',')) {
            $runtimeIdentifier = $part.Trim().ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($runtimeIdentifier)) {
                continue
            }
            if ($AllowedRuntimeIdentifiers -notcontains $runtimeIdentifier) {
                throw "Unsupported RuntimeIdentifier '$runtimeIdentifier'. Allowed values: $($AllowedRuntimeIdentifiers -join ', ')."
            }
            if (-not $resolved.Contains($runtimeIdentifier)) {
                $resolved.Add($runtimeIdentifier)
            }
        }
    }

    if ($resolved.Count -eq 0) {
        foreach ($runtimeIdentifier in $DefaultRuntimeIdentifiers) {
            $resolved.Add($runtimeIdentifier)
        }
    }

    return $resolved.ToArray()
}

function Set-PublishedUpdateSource {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$SourceBaseAddress
    )

    $settingsPath = Join-Path $PublishDirectory 'appsettings.default.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "Published appsettings.default.json was not found: $settingsPath"
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($null -eq $settings.ApplicationUpdate) {
        $settings | Add-Member -MemberType NoteProperty -Name ApplicationUpdate -Value ([pscustomobject]@{})
    }
    $settings.ApplicationUpdate | Add-Member `
        -MemberType NoteProperty `
        -Name SourceBaseAddress `
        -Value $SourceBaseAddress `
        -Force
    if ($settings.ApplicationUpdate.PSObject.Properties.Name -contains 'Source') {
        $settings.ApplicationUpdate.PSObject.Properties.Remove('Source')
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function Get-VpkDirectiveArguments {
    param([Parameter(Mandatory = $true)][string]$TargetOs)

    if ($TargetOs -eq 'linux' -and -not (Test-IsLinuxHost)) { return @('[linux]') }
    if ($TargetOs -eq 'win' -and -not (Test-IsWindowsHost)) { return @('[win]') }
    if ($TargetOs -eq 'osx' -and -not (Test-IsMacOSHost)) {
        throw 'Velopack macOS packages must be created on a macOS host.'
    }
    return @()
}

function New-WindowsInstallLocationSetup {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$ReleasesDirectory
    )

    $setupFileName = 'EGGtCSPlatform.Desktop-win-Setup.exe'
    $velopackSetupPath = Join-Path $ReleasesDirectory $setupFileName
    if (-not (Test-Path -LiteralPath $velopackSetupPath -PathType Leaf)) {
        throw "Velopack setup was not found: $velopackSetupPath"
    }

    $targetRoot = Split-Path -Parent $ReleasesDirectory
    $wrapperPublishDirectory = Join-Path (Join-Path $targetRoot 'setup') $Version
    $installerProject = Join-Path (Join-Path $RepoRoot 'EGGtCSPlatform.WindowsSetup') 'EGGtCSPlatform.WindowsSetup.csproj'
    Assert-SafeChildPath -Parent $targetRoot -Child $wrapperPublishDirectory
    if (Test-Path -LiteralPath $wrapperPublishDirectory) {
        Remove-Item -LiteralPath $wrapperPublishDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $wrapperPublishDirectory -Force | Out-Null

    Write-Host "Building install-location selector for $RuntimeIdentifier..."
    Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
        'publish', $installerProject,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:RequireEmbeddedVelopackSetup=true',
        "-p:VelopackSetupPath=$velopackSetupPath",
        "-p:Version=$Version",
        '--output', $wrapperPublishDirectory)

    $wrapperPath = Join-Path $wrapperPublishDirectory 'EGGtCSPlatform.WindowsSetup.exe'
    if (-not (Test-Path -LiteralPath $wrapperPath -PathType Leaf)) {
        throw "Install-location selector was not produced: $wrapperPath"
    }

    Copy-Item -LiteralPath $wrapperPath -Destination $velopackSetupPath -Force
}

function Invoke-VelopackBuildAndPublish {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$SourceBaseAddress,

        [Parameter(Mandatory = $true)]
        [string]$PublishPath,

        [Parameter(Mandatory = $true)]
        [string[]]$RuntimeIdentifiers
    )

    $versionArguments = @{}
    if ($PSBoundParameters.ContainsKey('Version')) {
        $versionArguments.Version = $Version
    }
    $Version = Resolve-VelopackVersion @versionArguments
    $sourceBase = Assert-SourceBaseAddress -SourceBaseAddress $SourceBaseAddress
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $artifactsRoot = Join-Path (Join-Path $repoRoot 'TestArtifacts') 'Velopack'
    $resolvedPublishPath = [System.IO.Path]::GetFullPath($PublishPath)
    $desktopProject = Join-Path (Join-Path $repoRoot 'EGGtCSPlatform.Desktop') 'EGGtCSPlatform.Desktop.csproj'
    $imagesDirectory = Join-Path (Join-Path (Join-Path $repoRoot 'EGGtCSPlatform') 'Assets') 'Images'
    $builtTargets = [System.Collections.Generic.List[object]]::new()

    foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
        $target = Get-VelopackTarget -RuntimeIdentifier $runtimeIdentifier
        if ($target.Os -eq 'osx' -and -not (Test-IsMacOSHost)) {
            throw 'The requested target matrix contains macOS, but Velopack requires a macOS build host.'
        }
        $iconPath = Join-Path $imagesDirectory $target.Icon
        if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
            throw "Packaging icon was not found: $iconPath"
        }
    }

    Push-Location $repoRoot
    try {
        Invoke-CheckedCommand -Command 'dotnet' -Arguments @('tool', 'restore')

        foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
            $target = Get-VelopackTarget -RuntimeIdentifier $runtimeIdentifier
            $targetRoot = Join-Path $artifactsRoot $runtimeIdentifier
            $publishDirectory = Join-Path (Join-Path $targetRoot 'publish') $Version
            $releasesDirectory = Join-Path $targetRoot 'Releases'
            Assert-SafeChildPath -Parent $artifactsRoot -Child $publishDirectory

            if (Test-Path -LiteralPath $publishDirectory) {
                Remove-Item -LiteralPath $publishDirectory -Recurse -Force
            }
            New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
            New-Item -ItemType Directory -Path $releasesDirectory -Force | Out-Null

            Write-Host "Publishing self-contained application for $runtimeIdentifier..."
            Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
                'publish', $desktopProject,
                '--configuration', 'Release',
                '--runtime', $runtimeIdentifier,
                '--self-contained', 'true',
                "-p:Version=$Version",
                '--output', $publishDirectory)

            Set-PublishedUpdateSource `
                -PublishDirectory $publishDirectory `
                -SourceBaseAddress $sourceBase

            $directive = Get-VpkDirectiveArguments -TargetOs $target.Os
            $packArguments = @('tool', 'run', 'vpk', '--') + $directive + @(
                'pack',
                '--packId', 'EGGtCSPlatform.Desktop',
                '--packTitle', 'EGGtCSPlatform',
                '--packVersion', $Version,
                '--packDir', $publishDirectory,
                '--mainExe', $target.MainExe,
                '--runtime', $runtimeIdentifier,
                '--channel', $target.Os,
                '--icon', (Join-Path $imagesDirectory $target.Icon),
                '--outputDir', $releasesDirectory)

            if ($target.Os -eq 'win') {
                $packArguments += @('--msi', '--instLocation', 'Either')
            }
            elseif ($target.Os -eq 'linux') {
                $packArguments += @('--categories', 'Science;Utility')
            }

            Write-Host "Packaging Velopack release for $runtimeIdentifier..."
            Invoke-CheckedCommand -Command 'dotnet' -Arguments $packArguments
            if ($target.Os -eq 'win') {
                New-WindowsInstallLocationSetup `
                    -RepoRoot $repoRoot `
                    -RuntimeIdentifier $runtimeIdentifier `
                    -Version $Version `
                    -ReleasesDirectory $releasesDirectory
            }
            $builtTargets.Add([pscustomobject]@{
                RuntimeIdentifier = $runtimeIdentifier
                Os = $target.Os
                FeedPath = $target.FeedPath
                ReleasesDirectory = $releasesDirectory
            })
        }

        New-Item -ItemType Directory -Path $resolvedPublishPath -Force | Out-Null
        $legacyUpdatesDirectory = Join-Path $resolvedPublishPath 'updates'
        if (Test-Path -LiteralPath $legacyUpdatesDirectory) {
            Write-Warning "Legacy publish directory '$legacyUpdatesDirectory' is not used. PublishPath now maps directly to SourceBaseAddress."
        }
        foreach ($builtTarget in $builtTargets) {
            $feedDirectory = Join-Path $resolvedPublishPath $builtTarget.FeedPath
            New-Item -ItemType Directory -Path $feedDirectory -Force | Out-Null
            $directive = Get-VpkDirectiveArguments -TargetOs $builtTarget.Os
            $uploadArguments = @('tool', 'run', 'vpk', '--') + $directive + @(
                'upload', 'local',
                '--outputDir', $builtTarget.ReleasesDirectory,
                '--path', $feedDirectory,
                '--channel', $builtTarget.Os,
                '--regenerate')

            Write-Host "Publishing $($builtTarget.RuntimeIdentifier) to $feedDirectory..."
            Invoke-CheckedCommand -Command 'dotnet' -Arguments $uploadArguments

            $releaseIndexName = "releases.$($builtTarget.Os).json"
            $releaseIndexPath = Join-Path $feedDirectory $releaseIndexName
            if (-not (Test-Path -LiteralPath $releaseIndexPath -PathType Leaf)) {
                throw "Published Velopack release index was not found: $releaseIndexPath"
            }

            $feedUrlPath = $builtTarget.FeedPath.Replace('\', '/')
            Write-Host "Verified release index: $sourceBase/$feedUrlPath/$releaseIndexName"
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Published $($builtTargets.Count) target(s) for version $Version."
    Write-Host "Update source base address: $sourceBase"
    Write-Host "Publish path: $resolvedPublishPath"
}

Export-ModuleMember -Function Invoke-VelopackBuildAndPublish, Resolve-VelopackRuntimeIdentifiers, Resolve-VelopackVersion
