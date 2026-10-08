[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$NginxExecutable,

    [string]$PublishPath = '',

    [string]$ListenAddress = '127.0.0.1',

    [ValidateRange(1, 65535)]
    [int]$Port = 8080,

    [ValidateRange(0, 2147483647)]
    [int]$DownloadRateLimitKBps = 0
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path $repoRoot 'TestArtifacts\Publish'
}
$resolvedNginx = [System.IO.Path]::GetFullPath($NginxExecutable)
$documentRoot = [System.IO.Path]::GetFullPath($PublishPath)
$runtimeRoot = Join-Path $repoRoot 'TestArtifacts\Nginx\runtime'
$logsRoot = Join-Path $runtimeRoot 'logs'
$tempRoot = Join-Path $runtimeRoot 'temp'
$templatePath = Join-Path $repoRoot 'deploy\nginx\nginx.conf.template'
$configPath = Join-Path $runtimeRoot 'nginx.conf'
$pidPath = Join-Path $runtimeRoot 'nginx.pid'

if (-not (Test-Path -LiteralPath $resolvedNginx -PathType Leaf)) {
    throw "nginx executable was not found: $resolvedNginx"
}
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "nginx configuration template was not found: $templatePath"
}
if (-not (Test-Path -LiteralPath $documentRoot -PathType Container)) {
    throw "PublishPath must already exist and be a directory: $documentRoot"
}
$parsedAddress = $null
if (-not [System.Net.IPAddress]::TryParse($ListenAddress, [ref]$parsedAddress)) {
    throw 'ListenAddress must be an IPv4 or IPv6 address.'
}

New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
@('client_body_temp', 'proxy_temp', 'fastcgi_temp', 'uwsgi_temp', 'scgi_temp') |
    ForEach-Object {
        New-Item -ItemType Directory -Path (Join-Path $tempRoot $_) -Force | Out-Null
    }

function ConvertTo-NginxPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).Replace('\', '/')
}

$configuration = Get-Content -LiteralPath $templatePath -Raw
$nginxListenAddress = if ($parsedAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
    "[$ListenAddress]"
}
else {
    $ListenAddress
}
$configuration = $configuration.Replace('{{LISTEN_ADDRESS}}', $nginxListenAddress)
$configuration = $configuration.Replace(
    '{{PORT}}', $Port.ToString([System.Globalization.CultureInfo]::InvariantCulture))
$nginxDownloadRate = if ($DownloadRateLimitKBps -eq 0) {
    '0'
}
else {
    $DownloadRateLimitKBps.ToString([System.Globalization.CultureInfo]::InvariantCulture) + 'k'
}
$configuration = $configuration.Replace('{{DOWNLOAD_RATE_LIMIT}}', $nginxDownloadRate)
$configuration = $configuration.Replace('{{DOCUMENT_ROOT}}', (ConvertTo-NginxPath $documentRoot))
$configuration = $configuration.Replace(
    '{{ERROR_LOG_PATH}}', (ConvertTo-NginxPath (Join-Path $logsRoot 'error.log')))
$configuration = $configuration.Replace(
    '{{ACCESS_LOG_PATH}}', (ConvertTo-NginxPath (Join-Path $logsRoot 'access.log')))
$configuration = $configuration.Replace('{{PID_PATH}}', (ConvertTo-NginxPath $pidPath))
[System.IO.File]::WriteAllText(
    $configPath,
    $configuration,
    [System.Text.UTF8Encoding]::new($false))

$nginxPrefix = ConvertTo-NginxPath $runtimeRoot
$resolvedConfig = ConvertTo-NginxPath $configPath
& $resolvedNginx -p "$nginxPrefix/" -c $resolvedConfig -t
if ($LASTEXITCODE -ne 0) {
    throw 'nginx configuration validation failed.'
}

$nginxIsRunning = $false
if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
    $processId = 0
    $pidContent = [string](Get-Content -LiteralPath $pidPath -Raw)
    if (-not [string]::IsNullOrWhiteSpace($pidContent) `
        -and [int]::TryParse($pidContent.Trim(), [ref]$processId)) {
        $nginxIsRunning = $null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)
    }
}

if ($nginxIsRunning) {
    & $resolvedNginx -p "$nginxPrefix/" -c $resolvedConfig -s reload
    if ($LASTEXITCODE -ne 0) { throw 'nginx reload failed.' }
}
else {
    if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
        Remove-Item -LiteralPath $pidPath -Force
    }
    Start-Process `
        -FilePath $resolvedNginx `
        -ArgumentList @('-p', "`"$nginxPrefix/`"", '-c', "`"$resolvedConfig`"") `
        -WindowStyle Hidden
}

$healthAddress = if ($ListenAddress -in @('0.0.0.0', '::')) { '127.0.0.1' } else { $ListenAddress }
$healthUri = "http://${healthAddress}:$Port/health"
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $healthUri -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            Write-Host "Local update server is running: $healthUri"
            Write-Host "Publish path: $documentRoot"
            $downloadRateDescription = if ($DownloadRateLimitKBps -eq 0) {
                'unlimited'
            }
            else {
                "$DownloadRateLimitKBps KB/s"
            }
            Write-Host "Download rate limit: $downloadRateDescription"
            $expectedReleaseFeeds = @(
                'win/x86/releases.win.json',
                'win/x64/releases.win.json',
                'win/arm64/releases.win.json',
                'linux/x64/releases.linux.json',
                'linux/arm64/releases.linux.json',
                'osx/x64/releases.osx.json',
                'osx/arm64/releases.osx.json')
            $releaseFeeds = @($expectedReleaseFeeds | ForEach-Object {
                $candidate = Join-Path $documentRoot $_
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    Get-Item -LiteralPath $candidate
                }
            })
            if ($releaseFeeds.Count -eq 0) {
                Write-Warning "Nginx is healthy, but no Velopack release feed exists directly under '$documentRoot'. Build with the same -PublishPath before starting the application."
            }
            else {
                $normalizedDocumentRoot = $documentRoot.TrimEnd(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
                $serverBaseAddress = $healthUri.Substring(
                    0,
                    $healthUri.Length - '/health'.Length)
                foreach ($releaseFeed in $releaseFeeds) {
                    $relativeFeed = $releaseFeed.FullName.Substring(
                        $normalizedDocumentRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    Write-Host "Available release index: $serverBaseAddress/$relativeFeed"
                }
            }
            $legacyUpdatesDirectory = Join-Path $documentRoot 'updates'
            if (Test-Path -LiteralPath $legacyUpdatesDirectory) {
                Write-Warning "Legacy directory '$legacyUpdatesDirectory' is not served. Remove it after confirming that no old test package needs it."
            }
            return
        }
    }
    catch {
        Start-Sleep -Milliseconds 250
    }
}

throw "nginx did not become healthy at $healthUri. Check $logsRoot."
