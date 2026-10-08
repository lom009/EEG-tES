<#
.SYNOPSIS
Packages the current working-tree source files for sharing.
.DESCRIPTION
Includes tracked files (with local changes) and non-ignored untracked files.
Deleted files are skipped. Tracked files remain included even if ignored.
Requires Git. Supports Windows PowerShell 5.1 and PowerShell 7.
.EXAMPLE
.\scripts\Pack-Source.ps1
.EXAMPLE
.\scripts\Pack-Source.ps1 -OutputPath 'D:\Shared Sources\EGGtCSPlatform.zip'
#>
[CmdletBinding()]
param(
    # Relative paths are resolved against the caller's current directory.
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$archiveRoot = 'EGGtCSPlatform'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $OutputPath = Join-Path $projectRoot "artifacts\source\$archiveRoot-source-$stamp.zip"
}
$outputFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if (Test-Path -LiteralPath $outputFullPath) {
    throw "Output already exists: $outputFullPath"
}

# Read NUL-delimited UTF-8 paths directly; native pipeline decoding differs
# between Windows PowerShell and PowerShell 7, particularly for Unicode names.
$gitCommand = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $gitCommand.Source
$startInfo.Arguments = 'ls-files --cached --others --exclude-standard -z'
$startInfo.WorkingDirectory = $projectRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
try {
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $pathsText = $stdoutTask.GetAwaiter().GetResult()
    $gitError = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "Git could not list source files: $gitError"
    }
}
finally {
    $process.Dispose()
}

$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$sourceFiles = @(
    foreach ($relativePath in $pathsText.Split([char]0)) {
        if (-not $relativePath -or -not $seen.Add($relativePath)) { continue }
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $relativePath))
        if ([string]::Equals($fullPath, $outputFullPath, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not (Test-Path -LiteralPath $fullPath)) { continue }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Cannot package a non-file Git entry (for example, a submodule): $relativePath"
        }
        [pscustomobject]@{ FullPath = $fullPath; EntryName = "$archiveRoot/$relativePath" }
    }
)
if ($sourceFiles.Count -eq 0) {
    throw 'No source files were found to package.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputFullPath))
$stream = $null
$archive = $null
$created = $false
try {
    # CreateNew also protects against another process creating the same path.
    $stream = [System.IO.File]::Open($outputFullPath, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $created = $true
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($sourceFile in $sourceFiles) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $sourceFile.FullPath, $sourceFile.EntryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        try {
            if ($null -ne $archive) { $archive.Dispose() }
        }
        finally {
            $stream.Dispose()
        }
    }
}
catch {
    if ($created) { Remove-Item -LiteralPath $outputFullPath -Force }
    throw
}

$size = (Get-Item -LiteralPath $outputFullPath).Length
Write-Host "Archive: $outputFullPath"
Write-Host "Files: $($sourceFiles.Count)"
Write-Host ('Size: {0:N2} MB ({1} bytes)' -f ($size / 1MB), $size)
