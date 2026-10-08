[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$NginxExecutable
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repoRoot 'TestArtifacts\Nginx\runtime'
$configPath = Join-Path $runtimeRoot 'nginx.conf'
$pidPath = Join-Path $runtimeRoot 'nginx.pid'
$resolvedNginx = [System.IO.Path]::GetFullPath($NginxExecutable)

if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
    Write-Host 'Local update server is not running.'
    return
}
if (-not (Test-Path -LiteralPath $resolvedNginx -PathType Leaf)) {
    throw "nginx executable was not found: $resolvedNginx"
}

$nginxPrefix = [System.IO.Path]::GetFullPath($runtimeRoot).Replace('\', '/')
$resolvedConfig = [System.IO.Path]::GetFullPath($configPath).Replace('\', '/')
& $resolvedNginx -p "$nginxPrefix/" -c $resolvedConfig -s quit
if ($LASTEXITCODE -ne 0) { throw 'nginx stop failed.' }
Write-Host 'Local update server stop signal sent.'
