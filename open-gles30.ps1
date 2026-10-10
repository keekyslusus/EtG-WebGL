# Opens the ripped project in Unity 2017.4 rendering through OpenGL ES 3.0 (-force-gles30),
# which is much closer to the WebGL 2 player than the default Direct3D 11 - use it to
# reproduce browser-only rendering problems (lighting, render texture formats) in Play Mode.
# Usage: powershell -ExecutionPolicy Bypass -File open-gles30.ps1
$ErrorActionPreference = 'Stop'

$unity = 'C:\Program Files\Unity\Hub\Editor\2017.4.27f1\Editor\Unity.exe'
$project = Join-Path $PSScriptRoot 'ripped\ExportedProject'

if (-not (Test-Path $unity)) { throw "Unity 2017.4.27f1 not found: $unity" }
if (-not (Test-Path $project)) { throw "Project not found: $project" }

# A second editor can't open a project that is already open (Temp\UnityLockfile is held).
$running = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $unity }
if ($running) {
    Write-Host 'Unity 2017.4 is already running - close it first, then run this script again.' -ForegroundColor Yellow
    exit 1
}

Start-Process -FilePath $unity -ArgumentList @('-projectPath', "`"$project`"", '-force-gles30')
Write-Host "Started Unity with -force-gles30 for $project"
Write-Host 'The window title should end with <OpenGL ES 3.0> instead of <DX11> (first start recompiles shaders, can take a while).'
