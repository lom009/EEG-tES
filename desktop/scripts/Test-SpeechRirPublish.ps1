param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$work = Join-Path ([IO.Path]::GetDirectoryName($publish)) 'RirPublishSmoke'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$escaped = [Security.SecurityElement]::Escape($publish)
$sdlNative = Join-Path $publish 'SDL3.dll'
if (-not (Test-Path -LiteralPath $sdlNative -PathType Leaf)) {
    $sdlNative = Join-Path $publish 'runtimes/win-x64/native/SDL3.dll'
}
if (-not (Test-Path -LiteralPath $sdlNative -PathType Leaf)) {
    throw 'Published SDL3.dll was not found.'
}
$escapedSdl = [Security.SecurityElement]::Escape($sdlNative)
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
 <ItemGroup>
  <Reference Include="EGGtCSPlatform.SpeechTraining"><HintPath>$escaped/EGGtCSPlatform.SpeechTraining.dll</HintPath></Reference>
  <Reference Include="EGGtCSPlatform.Audio.Core"><HintPath>$escaped/EGGtCSPlatform.Audio.Core.dll</HintPath></Reference>
  <Reference Include="EGGtCSPlatform.Audio.Dsp"><HintPath>$escaped/EGGtCSPlatform.Audio.Dsp.dll</HintPath></Reference>
  <Reference Include="EGGtCSPlatform.Audio.Sdl"><HintPath>$escaped/EGGtCSPlatform.Audio.Sdl.dll</HintPath></Reference>
  <Reference Include="SDL3-CS"><HintPath>$escaped/SDL3-CS.dll</HintPath></Reference>
  <None Include="$escapedSdl" Link="SDL3.dll" CopyToOutputDirectory="PreserveNewest" />
 </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $work 'RirPublishSmoke.csproj') -Value $project -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SpeechRirPublishSmoke.cs') -Destination (Join-Path $work 'Program.cs') -Force
& dotnet run --project (Join-Path $work 'RirPublishSmoke.csproj') -- $publish
if ($LASTEXITCODE -ne 0) { throw 'Published RIR smoke test failed.' }
