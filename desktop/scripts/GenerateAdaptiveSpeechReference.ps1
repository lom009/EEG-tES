param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '../EGGtCSPlatform.SpeechTraining.Tests/Fixtures/AdaptiveSpeechReference.json')
)
# Independent decimal evaluation of psychophysicsFunction.m. No production assembly is loaded.
# Actual code switches at reversal 2 and 4 (not the initialization comment's "sixth").
$sequences = [ordered]@{
    pass = @(1,1,1,1,1,1,1,1,1,1)
    fail = @(-1,-1,-1,-1,-1,-1,-1,-1,-1,-1)
    alternating = @(1,-1,1,-1,1,-1,1,-1,1,-1)
    boundaries = @(1,1,-1,-1,1,1,-1,-1,1,1,-1)
}
$cases = foreach ($entry in $sequences.GetEnumerator()) {
    [decimal]$r = 0.5
    $previous = 0
    $reversals = 0
    $rows = foreach ($answer in $entry.Value) {
        if ($previous * $answer -eq -1) { $reversals++ }
        [decimal]$factor = if ($reversals -lt 2) { 1.5 } elseif ($reversals -lt 4) { 1.26 } else { 1.1 }
        [decimal]$next = if ($answer -eq 1) { $r * $factor } else { $r / $factor }
        [ordered]@{ passed = ($answer -eq 1); r = $r; multiplier = 1 + $r; reversals = $reversals; factor = $factor; nextR = $next }
        $previous = $answer
        $r = $next
    }
    [ordered]@{ name = $entry.Key; rows = @($rows) }
}
[ordered]@{
    source = 'SpeechIntelligibility/psychophysicsFunction.m -> mySoundProcess.m -> SOLA/solafs.m'
    arithmetic = 'Decimal reference; production uses double; tolerance 1e-12'
    cases = @($cases)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
