# Serves the WebGL build (webbuild/) on http://localhost:8080 and opens it in the browser.
# The page doesn't work from file:// - it needs any local HTTP server. Ctrl+C stops it.
# Usage: powershell -ExecutionPolicy Bypass -File serve-webbuild.ps1 [-Port 8080] [-NoBrowser]
param(
    [int]$Port = 8080,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot 'webbuild'
if (-not (Test-Path (Join-Path $root 'index.html'))) {
    throw "No WebGL build in $root - run 'EtG Web -> 4. Build WebGL Player' in Unity first."
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 not found (python / py).' }

$url = "http://localhost:$Port/"
Write-Host "Serving $root on $url (Ctrl+C to stop)"
if (-not $NoBrowser) {
    Start-Process $url
}
& $python.Source -m http.server $Port --bind 127.0.0.1 --directory $root
