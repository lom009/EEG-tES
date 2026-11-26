Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Projects = @(
    'EGGtCSPlatform.Communication.Core', 'EGGtCSPlatform.Communication.Udp',
    'EGGtCSPlatform.DeviceSdk', 'EGGtCSPlatform.DeviceRuntime',
    'EGGtCSPlatform.Protocol.EggtCs.Gen1', 'EGGtCSPlatform.Audio.Core',
    'EGGtCSPlatform.Audio.Dsp', 'EGGtCSPlatform.Audio.Sdl', 'EGGtCSPlatform.SpeechTraining'
)
$script:SemVer = '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z'

function Get-NuGetPackProjects { return $script:Projects }

function ConvertTo-ProcessArgument([string]$Value) {
    # ProcessStartInfo.Arguments uses the standard escaped quote/backslash rules.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-PackProcess {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 600)
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    try {
        try { [void]$process.Start() }
        catch { throw "Cannot start '$FilePath' in '$WorkingDirectory': $($_.Exception.Message)" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Kill the entire tree where supported; Windows PowerShell 5.1 needs taskkill.
            if ($PSVersionTable.PSVersion.Major -ge 7) { $process.Kill($true) }
            elseif ($env:OS -eq 'Windows_NT') { & taskkill.exe /PID $process.Id /T /F | Out-Null }
            else { $process.Kill() }
            throw "Command timed out after ${TimeoutSeconds}s: $FilePath"
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Command failed ($($process.ExitCode)): $FilePath`n$output" }
        return $output
    }
    finally { $process.Dispose() }
}

function Resolve-NuGetPackVersion {
    param([AllowNull()][AllowEmptyString()][string]$Version, [string]$RepoRoot)
    $source = 'explicit'
    if (-not $PSBoundParameters.ContainsKey('Version')) {
        $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $git) { throw 'Git is required for HEAD tags; supply -Version.' }
        $tags = (Invoke-PackProcess $git.Source @('tag', '--points-at', 'HEAD') $RepoRoot 30) -split '\r?\n'
        $versions = @($tags | ForEach-Object {
            $candidate = $_ -creplace '^[vV]', ''
            if ($candidate -cmatch $script:SemVer) { $candidate }
        } | Sort-Object -Unique -CaseSensitive)
        if ($versions.Count -ne 1) {
            throw "HEAD must have exactly one distinct SemVer version; found [$($versions -join ', ')]. Tags: [$($tags -join ', ')]. Supply -Version."
        }
        $Version = $versions[0]
        $source = 'HEAD tag'
    }
    if ([string]::IsNullOrEmpty($Version) -or $Version -cnotmatch $script:SemVer) {
        throw 'Version must be strict SemVer 2 without v/V or whitespace, e.g. 1.2.3-rc.1+build.5.'
    }
    # SDK derives AssemblyVersion/FileVersion from these components; reject rather than truncate.
    $parts = $Version.Split('+')[0].Split('-')[0].Split('.')
    foreach ($part in $parts) {
        $number = 0
        if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65534) {
            throw 'SemVer numeric component exceeds the SDK assembly-version limit (65534); no truncation is performed.'
        }
    }
    [pscustomobject]@{ Full = $Version; Normalized = $Version.Split('+')[0].ToLowerInvariant(); Source = $source }
}

function Remove-PackTemporaryDirectory([string]$Root, [string]$Path) {
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $target" }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

function Read-PackNuspec([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
        if ($entries.Count -ne 1) { throw "Expected one nuspec: $Path" }
        $reader = New-Object IO.StreamReader($entries[0].Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return $xml
    }
    finally { $archive.Dispose() }
}

function Assert-SpeechPackageResources([string]$Path, [string]$RepoRoot) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if (-not $archive.GetEntry('buildTransitive/EGGtCSPlatform.SpeechTraining.targets')) {
            throw 'SpeechTraining package is missing its transitive resource targets.'
        }
        foreach ($pair in @(@('Resources', 'SpeechCorpora'), @('NoiseResources', 'SpeechNoises'))) {
            $source = Join-Path $RepoRoot "EGGtCSPlatform.SpeechTraining/$($pair[0])"
            foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
                $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/').Replace('\', '/')
                $entry = $archive.GetEntry("speechResources/$($pair[1])/$relative")
                if (-not $entry -or $entry.Length -ne $file.Length) { throw "Missing/invalid packaged resource: $relative" }
                $stream = $entry.Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $stream.Dispose(); $sha.Dispose() }
                if ($hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                    throw "Packaged resource checksum differs: $relative"
                }
            }
        }
    }
    finally { $archive.Dispose() }
}

function Publish-PackFiles {
    param([string]$Stage, [string]$Output, [string[]]$Names, [switch]$Overwrite)
    $backup = Join-Path $Stage 'backup'
    [void][IO.Directory]::CreateDirectory($backup)
    $saved = @()
    $published = @()
    try {
        foreach ($name in $Names) {
            $target = Join-Path $Output $name
            if (Test-Path -LiteralPath $target) {
                if (-not $Overwrite) { throw "Output exists: $target. Use -Overwrite." }
                Move-Item -LiteralPath $target -Destination (Join-Path $backup $name)
                $saved += $name
            }
            Move-Item -LiteralPath (Join-Path $Stage $name) -Destination $target
            $published += $name
        }
    }
    catch {
        foreach ($name in $published) { Remove-Item -LiteralPath (Join-Path $Output $name) -Force }
        foreach ($name in $saved) { Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $Output $name) }
        throw
    }
}

function Invoke-NuGetPack {
    param([string]$RepoRoot, [object]$ResolvedVersion, [switch]$Overwrite)
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $sdk = Invoke-PackProcess $dotnet @('--version') $RepoRoot 30
    if ($sdk.Trim() -notmatch '^([0-9]+)\.' -or [int]$Matches[1] -lt 10) { throw '.NET SDK 10 or newer is required.' }
    $output = Join-Path $RepoRoot 'pack_nuget'
    $full = $ResolvedVersion.Full
    $normalized = $ResolvedVersion.Normalized
    $names = @($script:Projects | ForEach-Object { "$_.${normalized}.nupkg" })
    $manifestName = "EGGtCSPlatform.${normalized}.manifest.json"
    foreach ($project in $script:Projects) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "$project/$project.csproj"))) { throw "Missing project: $project" }
    }
    foreach ($name in ($names + $manifestName)) {
        if (-not $Overwrite -and (Test-Path -LiteralPath (Join-Path $output $name))) { throw "Output exists: $name. Use -Overwrite." }
    }
    $commit = $null
    $status = $null
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        try {
            $commit = (Invoke-PackProcess $git.Source @('rev-parse', 'HEAD') $RepoRoot 30).Trim()
            $status = (Invoke-PackProcess $git.Source @('status', '--porcelain') $RepoRoot 30).TrimEnd()
        } catch { Write-Warning 'Git metadata unavailable; building the current working tree with the explicit version.' }
    }
    Write-Host "Packing $full ($($ResolvedVersion.Source)); NuGet identity: $normalized"
    Write-Host 'Build metadata does not distinguish NuGet package identities. Building the current working tree.'
    [void][IO.Directory]::CreateDirectory($output)
    # Serialize publication and prevent concurrent scripts from corrupting shared project outputs.
    $lock = [IO.File]::Open((Join-Path $output '.pack.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $stage = Join-Path $output ('.staging-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void][IO.Directory]::CreateDirectory($stage)
        $packages = @()
        foreach ($project in $script:Projects) {
            Write-Host "Packing $project ..."
            $arguments = @('pack', (Join-Path $RepoRoot "$project/$project.csproj"), '-c', 'Release',
                '-o', $stage, '-m:1', '-p:UseSharedCompilation=false',
                "-p:Version=$full", "-p:PackageVersion=$full", "-p:InformationalVersion=$full",
                '-p:IncludeSourceRevisionInInformationalVersion=false', '--verbosity', 'minimal')
            Write-Host (Invoke-PackProcess $dotnet $arguments $RepoRoot 600)
            $candidates = @(Get-ChildItem -LiteralPath $stage -Filter "$project.*.nupkg" -File | Where-Object {
                (Read-PackNuspec $_.FullName).package.metadata.id -ceq $project
            })
            if ($candidates.Count -ne 1) { throw "Expected exactly one package for $project" }
            $file = $candidates[0]
            $spec = Read-PackNuspec $file.FullName
            if ([string]$spec.package.metadata.version -cne $full) { throw "Package lost full version: $project" }
            if (-not $spec.SelectSingleNode("//*[local-name()='group' and @targetFramework='net10.0']")) {
                throw "Wrong target framework: $project"
            }
            if ($project -eq 'EGGtCSPlatform.SpeechTraining') { Assert-SpeechPackageResources $file.FullName $RepoRoot }
            $dependencies = @($spec.SelectNodes("//*[local-name()='dependency']"))
            [xml]$projectXml = Get-Content -LiteralPath (Join-Path $RepoRoot "$project/$project.csproj") -Raw
            if ($dependencies.Count -ne $projectXml.SelectNodes('//ProjectReference | //PackageReference').Count) {
                throw "Unexpected package dependencies: $project"
            }
            foreach ($reference in @($projectXml.SelectNodes('//ProjectReference'))) {
                $id = [IO.Path]::GetFileNameWithoutExtension(($reference.Include -replace '\\', '/'))
                $found = @($dependencies | Where-Object { $_.id -ceq $id })
                if ($id -notin $script:Projects -or $found.Count -ne 1 -or
                    ([string]$found[0].version).Trim('[', ']', ' ', ',').Split('+')[0].ToLowerInvariant() -ne $normalized) {
                    throw "Incorrect internal package dependency: $project -> $id"
                }
            }
            foreach ($reference in @($projectXml.SelectNodes('//PackageReference'))) {
                $found = @($dependencies | Where-Object { $_.id -ceq $reference.Include })
                if ($found.Count -ne 1 -or ([string]$found[0].version).Trim('[', ']', ' ', ',') -ne $reference.Version) {
                    throw "External dependency was not preserved: $($reference.Include)"
                }
            }
            $name = "$project.${normalized}.nupkg"
            if ($file.Name -cne $name) { Move-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $name) }
            $packages += [ordered]@{ Id = $project; File = $name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash }
        }
        $manifest = [ordered]@{
            Version = $full; NormalizedVersion = $normalized; VersionSource = $ResolvedVersion.Source
            GitCommit = $commit; GitStatus = $status; Sdk = $sdk.Trim()
            CreatedAtUtc = [DateTime]::UtcNow.ToString('o'); Packages = $packages
        }
        [IO.File]::WriteAllText((Join-Path $stage $manifestName), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        Publish-PackFiles $stage $output ($names + $manifestName) -Overwrite:$Overwrite
        Write-Host "Created 9 packages and $manifestName in $output"
    }
    finally {
        try { Remove-PackTemporaryDirectory $output $stage } finally { $lock.Dispose() }
    }
}

Export-ModuleMember -Function Get-NuGetPackProjects, Resolve-NuGetPackVersion, Invoke-NuGetPack, Invoke-PackProcess, Read-PackNuspec, Remove-PackTemporaryDirectory, Publish-PackFiles
