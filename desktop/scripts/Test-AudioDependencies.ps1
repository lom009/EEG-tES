param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$dependencies = @{
    'EGGtCSPlatform.Audio.Core' = @()
    'EGGtCSPlatform.Audio.Dsp' = @('EGGtCSPlatform.Audio.Core')
    'EGGtCSPlatform.Audio.Sdl' = @('EGGtCSPlatform.Audio.Core')
    'EGGtCSPlatform.SpeechTraining' = @('EGGtCSPlatform.Audio.Core', 'EGGtCSPlatform.Audio.Dsp')
}
foreach ($name in $dependencies.Keys) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $RepositoryRoot "$name/$name.csproj") -Raw
    $actual = @($project.SelectNodes('//ProjectReference') | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include.Replace('\', '/')) } | Sort-Object)
    $expected = @($dependencies[$name] | Sort-Object)
    if (($actual -join '|') -ne ($expected -join '|')) { throw "$name has unexpected dependencies: $actual" }
    if ($name -eq 'EGGtCSPlatform.Audio.Core' -and $project.SelectNodes('//PackageReference').Count -ne 0) { throw 'Core must use framework libraries only.' }
    if ($name -eq 'EGGtCSPlatform.Audio.Dsp') {
        foreach ($friend in $project.SelectNodes('//InternalsVisibleTo')) {
            if ($friend.Include -ne 'EGGtCSPlatform.Audio.Dsp.Tests') { throw "DSP grants internal access to $($friend.Include)" }
        }
    }
}
Write-Output 'Audio project dependency direction and DSP internal visibility verified.'
