<# .SYNOPSIS
Consumes already-built packages in isolated caches; simulation/audio preparation only.
.EXAMPLE
.\scripts\Test-NuGetPackages.ps1
#>
[CmdletBinding()]
param([string[]]$Versions = @('1.2.3', '1.2.3-rc.1+build.5'))
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NuGetPack.Common.psm1') -Force
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$root = Join-Path $repo ('artifacts/nuget consumer ' + [Guid]::NewGuid().ToString('N'))
$previousPackages = $env:NUGET_PACKAGES
function Write-Utf8([string]$path, [string]$value) { [IO.File]::WriteAllText($path, $value, (New-Object Text.UTF8Encoding($false))) }
function Run([string[]]$arguments, [int]$timeout = 180) { Write-Host (Invoke-PackProcess $dotnet $arguments $root $timeout) }
function Check-Resources([string]$directory) {
    foreach ($pair in @(@('Resources', 'SpeechCorpora'), @('NoiseResources', 'SpeechNoises'))) {
        $source = Join-Path $repo "EGGtCSPlatform.SpeechTraining/$($pair[0])"
        foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
            $target = Join-Path (Join-Path $directory $pair[1]) $relative
            if (-not (Test-Path -LiteralPath $target) -or
                (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
                throw "Resource missing or changed: $target"
            }
        }
    }
    if (@(Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object { $_.Name -match '(?i)(SDL3\.dll|libSDL3.*\.(so|dylib))$' }).Count -eq 0) {
        throw "SDL native runtime assets missing in $directory"
    }
}
$program = @'
using System.Reflection;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.SpeechTraining;

var expected = args[0];
foreach (var assembly in new[] { typeof(DeviceRuntimeBuilder).Assembly, typeof(SpeechTrainingSession).Assembly })
    if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != expected)
        throw new Exception("Assembly lost full version metadata");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var runtime = new DeviceRuntimeBuilder().UseSimulation().Build();
DeviceCandidate? found = null;
await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1), cts.Token))
    found ??= candidate;
if (found is null) throw new Exception("Discovery failed");
var device = await runtime.ConnectVerifiedAsync(found, cts.Token);
var status = await device.ReadStatusAsync(cts.Token);
if (status.Status != DeviceCommandStatus.Success) throw new Exception("Status failed");
var provider = new FileSpeechCorpusProvider();
var corpus = provider.Corpora.Single(c => c.Id == "MHINT-P");
var sentence = new SpeechSentenceListService().GetSentence(corpus, 20, 1, 1);
var preparation = new TrainingAudioPreparationService(provider, new FileSpeechNoiseProvider());
var request = preparation.CreateRequest(sentence,
    new TrainingAudioSettings(Mode: SpeechTrainingMode.FixedSnr, SnrDb: 5), 1, 1729, corpus.SnrNoise, cts.Token);
var result = preparation.Prepare(request, token: cts.Token);
result.ValidateRequest();
if (result.Playback.Frames == 0) throw new Exception("Empty prepared audio");
Console.WriteLine($"PASS {expected}: {device.Identity.DeviceId}, {result.Playback.Frames} audio frames; no hardware output.");
'@
try {
    [void][IO.Directory]::CreateDirectory($root)
    $feed = Join-Path $repo 'pack_nuget'
    $extraFeed = Join-Path $root 'feed'
    [void][IO.Directory]::CreateDirectory($extraFeed)
    $cache = Join-Path $root 'packages'
    $oldCache = $env:NUGET_PACKAGES
    if (-not $oldCache) { $oldCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $env:NUGET_PACKAGES = $cache
    $sources = '<add key="built" value="' + [Security.SecurityElement]::Escape($feed) + '" />' +
        '<add key="bridge" value="' + [Security.SecurityElement]::Escape($extraFeed) + '" />' +
        '<add key="cached-external" value="' + [Security.SecurityElement]::Escape($oldCache) + '" />' +
        '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    # Source mapping prevents accidentally restoring these libraries from any global/public source.
    Write-Utf8 (Join-Path $root 'NuGet.Config') ('<configuration><packageSources><clear />' + $sources +
        '</packageSources><packageSourceMapping><clear />' +
        '<packageSource key="built"><package pattern="EGGtCSPlatform.*" /></packageSource>' +
        '<packageSource key="bridge"><package pattern="PackTestBridge" /></packageSource>' +
        '<packageSource key="cached-external"><package pattern="SDL3-CS*" /></packageSource>' +
        '<packageSource key="nuget.org"><package pattern="SDL3-CS*" /></packageSource>' +
        '</packageSourceMapping><config><add key="globalPackagesFolder" value="' + [Security.SecurityElement]::Escape($cache) + '" /></config></configuration>')
    foreach ($version in $Versions) {
        $resolved = Resolve-NuGetPackVersion -Version $version
        $normal = $resolved.Normalized
        foreach ($project in Get-NuGetPackProjects) {
            $spec = Read-PackNuspec (Join-Path $feed "$project.$normal.nupkg")
            if ([string]$spec.package.metadata.version -cne $version) { throw "Wrong package version: $project" }
        }
        $references = (@('DeviceRuntime', 'Protocol.EggtCs.Gen1', 'SpeechTraining', 'Audio.Sdl') | ForEach-Object {
            '<PackageReference Include="EGGtCSPlatform.' + $_ + '" Version="[' + $normal + ']" />'
        }) -join ''
        $bridge = Join-Path $root "bridge-$normal"
        [void][IO.Directory]::CreateDirectory($bridge)
        Write-Utf8 (Join-Path $bridge 'Bridge.csproj') ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>PackTestBridge</PackageId><Version>' + $normal + '</Version></PropertyGroup><ItemGroup>' + $references + '</ItemGroup></Project>')
        Run @('pack', (Join-Path $bridge 'Bridge.csproj'), '-o', $extraFeed, '-m:1', '-p:UseSharedCompilation=false', '-p:NuGetAudit=false', '-v', 'quiet')
        foreach ($mode in @('direct', 'transitive')) {
            $app = Join-Path $root "$mode-$normal"
            [void][IO.Directory]::CreateDirectory($app)
            $refs = $references
            if ($mode -eq 'transitive') { $refs = '<PackageReference Include="PackTestBridge" Version="[' + $normal + ']" />' }
            Write-Utf8 (Join-Path $app 'Consumer.csproj') ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + $refs + '</ItemGroup></Project>')
            Write-Utf8 (Join-Path $app 'Program.cs') $program
            Run @('build', (Join-Path $app 'Consumer.csproj'), '-m:1', '-p:UseSharedCompilation=false', '-p:NuGetAudit=false', '-v', 'quiet')
            $build = Join-Path $app 'bin/Debug/net10.0'
            Check-Resources $build
            Run @((Join-Path $build 'Consumer.dll'), $version) 60
            $publish = Join-Path $app 'publish'
            Run @('publish', (Join-Path $app 'Consumer.csproj'), '-c', 'Release', '-o', $publish, '--no-restore', '-m:1', '-p:UseSharedCompilation=false', '-v', 'quiet')
            Check-Resources $publish
            Run @((Join-Path $publish 'Consumer.dll'), $version) 60
        }
    }
    Write-Host 'PASS: direct/transitive package consumers, build/publish resources, full versions and SDL runtime assets.'
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    Remove-PackTemporaryDirectory (Join-Path $repo 'artifacts') $root
}
