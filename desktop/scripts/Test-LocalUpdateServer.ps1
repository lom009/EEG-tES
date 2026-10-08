[CmdletBinding()]
param(
    [string]$SourceBaseAddress = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
$sourceBase = $SourceBaseAddress.Trim().TrimEnd('/')
$sourceUri = [Uri]$sourceBase
$healthUri = "{0}://{1}/health" -f $sourceUri.Scheme, $sourceUri.Authority
$health = Invoke-WebRequest -Uri $healthUri -UseBasicParsing -TimeoutSec 5
if ($health.StatusCode -ne 200) { throw "Health check failed: $healthUri" }

$feeds = @(
    'win/x86/releases.win.json',
    'win/x64/releases.win.json',
    'win/arm64/releases.win.json',
    'linux/x64/releases.linux.json',
    'linux/arm64/releases.linux.json',
    'osx/x64/releases.osx.json',
    'osx/arm64/releases.osx.json')

$available = 0
foreach ($relativeFeed in $feeds) {
    $uri = "$sourceBase/$relativeFeed"
    try {
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5
        if ($response.StatusCode -eq 200) {
            $available++
            Write-Host "OK $uri"
        }
    }
    catch {
        Write-Host "MISSING $uri"
    }
}

if ($available -eq 0) {
    throw 'The server is healthy, but no Velopack release feeds were found.'
}
Write-Host "$available Velopack feed(s) are available."
