# Resource structure validation for publish checks. Runtime uses the equivalent C# provider.
function Test-SpeechNoiseResources([string]$PublishDirectory) {
    $noiseRoot = Join-Path $PublishDirectory 'SpeechNoises'
    $corpusRoot = Join-Path $PublishDirectory 'SpeechCorpora'
    foreach ($baseline in (Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'SpeechNoiseHashes.csv'))) {
        $path = Join-Path $noiseRoot $baseline.FileName
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing noise resource: $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $baseline.Sha256) {
            throw "Noise hash mismatch: $path"
        }
    }
    foreach ($corpus in (Get-ChildItem -LiteralPath $corpusRoot -Directory)) {
        $configPath = Join-Path $corpus.FullName 'corpus.json'
        if (!(Test-Path -LiteralPath $configPath)) { continue }
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        $noise = $config['snrNoise']
        foreach ($kind in @('ssn', 'babble')) {
            $name = if ($noise -is [Collections.IDictionary]) { $noise[$kind] } else { $null }
            if ($null -eq $name) {
                if ($corpus.Name -cin @('MSP', 'MHINT', 'MHINT-P')) { throw "Missing snrNoise.$kind in $configPath" }
                Write-Warning "No snrNoise.$kind in $configPath (this SNR noise option is unavailable)."
                continue
            }
            if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or $name -match '[/\\:]' -or !$name.EndsWith('.wav', [StringComparison]::Ordinal) -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                throw "Invalid noise filename in ${configPath}: $kind"
            }
            $path = Join-Path $noiseRoot $name
            if (!((Get-ChildItem -LiteralPath $noiseRoot -File).Name -ccontains $name)) { throw "Missing noise or incorrect case: $path" }
            if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked noise resource: $path" }
            $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
            try {
                if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw "Not RIFF: $path" }
                $riffEnd = [long]$reader.ReadUInt32() + 8
                if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE' -or $riffEnd -gt $reader.BaseStream.Length) { throw "Invalid WAV: $path" }
                $validFormat = $false
                $energy = $false
                $dataLength = 0
                $channels = 0
                while ($reader.BaseStream.Position + 8 -le $riffEnd) {
                    $tag = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
                    $size = $reader.ReadUInt32()
                    $end = $reader.BaseStream.Position + $size
                    if ($end -gt $riffEnd) { throw "Truncated WAV: $path" }
                    if ($tag -eq 'fmt ') {
                        if ($size -lt 16 -or $reader.ReadUInt16() -ne 1) { throw "Expected PCM16: $path" }
                        $channels = $reader.ReadUInt16()
                        $rate = $reader.ReadInt32()
                        $bytesPerSecond = $reader.ReadInt32()
                        $alignment = $reader.ReadUInt16()
                        $bits = $reader.ReadUInt16()
                        $validFormat = $channels -in @(1,2) -and $rate -ge 8000 -and $rate -le 192000 -and $bits -eq 16 -and $alignment -eq $channels * 2 -and $bytesPerSecond -eq $rate * $alignment
                    } elseif ($tag -eq 'data') {
                        $dataLength = $size
                        while (!$energy -and $reader.BaseStream.Position + 2 -le $end) { $energy = $reader.ReadInt16() -ne 0 }
                    }
                    $reader.BaseStream.Position = $end + ($size % 2)
                }
                if (!$validFormat -or !$energy -or $dataLength % ($channels * 2) -ne 0) { throw "Invalid or silent PCM16 noise: $path" }
            } finally { $reader.Dispose() }
        }
    }
}

function Read-SpeechCorpusDirectory([string]$Directory) {
    if (@(Get-ChildItem -LiteralPath $Directory -Directory).Count) { throw "Corpus must be flat: $Directory" }
    $files = @(Get-ChildItem -LiteralPath $Directory -File)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked resource: $($file.FullName)" }
        $null = $names.Add($file.Name)
    }
    if (!$names.Contains('sentences.txt')) { throw "Missing sentences.txt: $Directory" }
    $utf8 = [Text.UTF8Encoding]::new($false,$true)
    $text = $utf8.GetString([IO.File]::ReadAllBytes((Join-Path $Directory 'sentences.txt')))
    if ($text.Length -gt 0 -and [int]$text[0] -eq 0xFEFF) { $text = $text.Substring(1) }
    $lines = @($text -split '\r\n|\n|\r' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (!$lines.Count) { throw "Empty text: $Directory" }
    foreach ($line in $lines) {
        if ($line -match '\p{Cc}' -or $line -notmatch '[^\p{P}\s]') { throw "Invalid sentence: $Directory" }
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($n in 1..$lines.Count) {
        $name = $n.ToString([Globalization.CultureInfo]::InvariantCulture) + '.wav'
        $null = $expected.Add($name)
        if (!$names.Contains($name)) { throw "Missing $name in $Directory" }
    }
    foreach ($file in $files) {
        if ($file.Extension -ieq '.wav' -and !$expected.Contains($file.Name)) { throw "Unexpected WAV: $($file.FullName)" }
        if ($file.Name -ieq 'sentences.txt' -and $file.Name -cne 'sentences.txt') { throw "Text filename case: $($file.FullName)" }
    }
    return $lines
}
function Test-SpeechResourceDirectory([string]$Directory) {
    $corpora = @(Get-ChildItem -LiteralPath $Directory -Directory)
    if (!$corpora.Count) { throw "No corpora: $Directory" }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $count = 0
    foreach ($corpus in $corpora) {
        if (!$names.Add($corpus.Name)) { throw "Duplicate corpus: $($corpus.FullName)" }
        if ($corpus.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked corpus: $($corpus.FullName)" }
        $lines = @(Read-SpeechCorpusDirectory $corpus.FullName)
        $count += $lines.Count
        $rule = Read-SpeechAdaptiveScoringRule $corpus.FullName
        if ($null -ne $rule) {
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $characters = 0
                $elements = [Globalization.StringInfo]::GetTextElementEnumerator($lines[$i])
                while ($elements.MoveNext()) {
                    $element = $elements.GetTextElement()
                    if (![string]::IsNullOrWhiteSpace($element) -and ![char]::IsPunctuation($element, 0)) { $characters++ }
                }
                if ($characters -ne $rule.ExpectedCharacterCount) {
                    throw "Adaptive scoring mismatch: $($corpus.FullName)/corpus.json; sentence $($i + 1) has $characters characters, expected $($rule.ExpectedCharacterCount)."
                }
            }
        }
    }
    return [pscustomobject]@{CorpusCount=$corpora.Count;SentenceCount=$count}
}

function Read-SpeechAdaptiveScoringRule([string]$Directory) {
    $path = Join-Path $Directory 'corpus.json'
    $files = @(Get-ChildItem -LiteralPath $Directory -File)
    if (!($files.Name -ccontains 'corpus.json')) {
        if ($files.Name -icontains 'corpus.json') { throw "Configuration filename case: $path" }
        Write-Warning "No adaptive scoring configuration: $path (adaptive modes unavailable)."
        return $null
    }
    $document = $null
    try {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $json = $utf8.GetString([IO.File]::ReadAllBytes($path)).TrimStart([char]0xFEFF)
        $document = [Text.Json.JsonDocument]::Parse($json)
        $root = $document.RootElement
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'corpus.json must be an object.' }
        $rule = [Text.Json.JsonElement]::new()
        if (!$root.TryGetProperty('adaptiveScoring', [ref]$rule)) {
            if ([IO.Path]::GetFileName($Directory.TrimEnd('\','/')) -cin @('MSP','MHINT','MHINT-P')) { throw 'Built-in corpus requires adaptiveScoring.' }
            Write-Warning "No adaptiveScoring in $path (fixed modes remain available)."
            return $null
        }
        $expected = $rule.GetProperty('expectedCharacterCount').GetInt32()
        $minimum = $rule.GetProperty('minimumCorrect').GetInt32()
        if ($expected -le 0 -or $minimum -le 0 -or $minimum -gt $expected) {
            throw 'Expected character count must be positive; minimum correct must be between 1 and expected character count.'
        }
        return [pscustomobject]@{ ExpectedCharacterCount = $expected; MinimumCorrect = $minimum }
    } catch {
        throw "Invalid adaptive scoring configuration in ${Directory} (${path}): $($_.Exception.Message)"
    } finally {
        if ($null -ne $document) { $document.Dispose() }
    }
}
