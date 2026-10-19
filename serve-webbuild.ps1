# Serves a WebGL build on http://localhost:8080 and opens it in the browser (port/site/serve.py: adds the
# Content-Encoding headers Unity 6's compressed Build/*.gz need). The page doesn't work from file://. Ctrl+C stops it.
# Usage: powershell -ExecutionPolicy Bypass -File serve-webbuild.ps1 [-Dir webbuild6] [-Port 8080] [-NoBrowser]
#   -Dir: webbuild (default; the Unity 2017.4 build until the migration ends) or webbuild6 (Unity 6.6)
param(
    [string]$Dir = 'webbuild6',
    [int]$Port = 8080,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot $Dir
if (-not (Test-Path (Join-Path $root 'index.html'))) {
    throw "No WebGL build in $root - run 'EtG Web -> 4. Build WebGL Player' in Unity first."
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 not found (python / py).' }

$url = "http://localhost:$Port/"
if (-not $NoBrowser) {
    Start-Process $url
}
& $python.Source (Join-Path $PSScriptRoot 'port/site/serve.py') $root --port $Port --bind 127.0.0.1
