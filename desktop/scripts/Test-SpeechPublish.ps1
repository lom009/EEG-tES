param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x86','win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PublishDirectory)
foreach ($assembly in @('EGGtCSPlatform.Audio.Core', 'EGGtCSPlatform.Audio.Dsp', 'EGGtCSPlatform.Audio.Sdl', 'EGGtCSPlatform.SpeechTraining')) {
    if (!(Test-Path -LiteralPath (Join-Path $root "$assembly.dll"))) { throw "Missing audio dependency: $assembly.dll" }
}
if (Test-Path -LiteralPath (Join-Path $root 'EGGtCSPlatform.SpeechTraining.Audio.Sdl.dll')) { throw 'Obsolete speech SDL facade remains in the publish directory. Use a clean publish directory.' }
$resources = Join-Path $root 'SpeechCorpora'
foreach ($obsolete in @('catalog.json','audio','texts','MSP10')) {
    if (Test-Path -LiteralPath (Join-Path $resources $obsolete)) { throw "Obsolete resource layout: $obsolete" }
}
. (Join-Path $PSScriptRoot 'SpeechResourceValidation.ps1')
$summary = Test-SpeechResourceDirectory $resources
Test-SpeechNoiseResources $root
foreach ($corpus in @('MSP', 'MHINT', 'MHINT-P')) {
    if (!(Test-Path -LiteralPath (Join-Path $resources "$corpus/corpus.json"))) {
        throw "Missing built-in scoring configuration: $corpus/corpus.json"
    }
}
# The baseline checks built-in files only; valid additional corpora/sentences need no CSV registration.
$sources = @(Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'SpeechResourceHashes.csv'))
foreach ($source in $sources) {
    $file = Join-Path $resources $source.Target
    $hash = if ($source.Kind -eq 'generated-text') {
        $lines = @(Read-SpeechCorpusDirectory ([IO.Path]::GetDirectoryName($file)))
        $count = [int]$source.BuiltinSentenceCount
        if ($lines.Count -lt $count) { throw "Missing built-in sentences: $file" }
        $canonical = ($lines[0..($count-1)] -join "`n") + "`n"
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    } else { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    if ($hash -ne $source.TargetSha256) { throw "Resource hash mismatch: $($source.Target)" }
}
$library = if ($RuntimeIdentifier.StartsWith('win-')) { 'SDL3.dll' } elseif ($RuntimeIdentifier.StartsWith('linux-')) { 'libSDL3.so' } else { 'libSDL3.dylib' }
if (!(Test-Path -LiteralPath (Join-Path $root $library)) -and !(Test-Path -LiteralPath (Join-Path $root "runtimes/$RuntimeIdentifier/native/$library"))) { throw "Missing $RuntimeIdentifier SDL3 runtime." }
Write-Output "$RuntimeIdentifier verified: $($summary.CorpusCount) corpora, $($summary.SentenceCount) WAV files and SDL3 runtime."
