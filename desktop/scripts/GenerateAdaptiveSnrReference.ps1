# Independent decimal reference for psychophysicsFunction.m. No production assemblies.
$cases = @(
    @{ Name='pass'; Answers=@(1,1,1,1,1,1,1,1) },
    @{ Name='fail'; Answers=@(0,0,0,0,0,0) },
    @{ Name='alternating'; Answers=@(1,0,1,0,1,0,1,0,1) },
    @{ Name='plateau'; Answers=@(0,0,1,1,0,0,1,0) }
)
$output = foreach ($case in $cases) {
    [decimal]$snr = 10
    $last = $null
    $reversals = 0
    $submissions = foreach ($answer in $case.Answers) {
        if ($null -ne $last -and $last -ne $answer) { $reversals++ }
        [decimal]$step = if ($reversals -lt 2) { 8 } elseif ($reversals -lt 4) { 4 } else { 2 }
        [decimal]$next = if ($answer) { $snr - $step } else { [Math]::Min(20, $snr + $step) }
        @{ Snr=$snr; Passed=[bool]$answer; Reversals=$reversals; Step=$step; Next=$next }
        $snr = $next
        $last = $answer
    }
    @{ Name=$case.Name; Submissions=@($submissions) }
}
$destination = Join-Path $PSScriptRoot '../EGGtCSPlatform.SpeechTraining.Tests/Fixtures/AdaptiveSnrReference.json'
$output | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
