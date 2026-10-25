# Compile tests\PilotTest.cs avec les sources du programme (dossier de sortie a part, jamais l'installation du PC).
#   powershell -File tests\build-pilot-test.ps1 [-Out <dossier>]
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Out) { $Out = Join-Path $env:TEMP 'showlef-pilot-test' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Core.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.VisualBasic.dll')
$files = @('ShowLef.cs', 'Studio.cs', 'Common.cs', 'Engine.cs', 'Ipc.cs', 'Pilot.cs', 'ExtensionPresence.cs', 'SettingsUi.cs') | ForEach-Object { Join-Path $root "app\$_" }
$files += Join-Path $root 'tests\PilotTest.cs'
$exe = Join-Path $Out 'pilot-test.exe'
$o = & $csc /nologo /codepage:65001 /target:exe /main:PilotTest /optimize+ "/out:$exe" @refs @files 2>&1
if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "csc RC=$LASTEXITCODE" }
Write-Host "OK $exe"
