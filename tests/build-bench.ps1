# Banc d'essai : compile ShowLef.exe + showlef-host.exe dans un dossier A PART (jamais l'installation du PC).
#   powershell -File tests\build-bench.ps1 -Out <dossier> [-Src <dossier app>]
# -Src permet de compiler une ancienne version (copie de app/ tiree d'un commit) pour comparer avant / apres.
param([Parameter(Mandatory = $true)][string]$Out, [string]$Src = '')
$ErrorActionPreference = 'Stop'
if (-not $Src) { $Src = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'app' }
$AppDir = $Src
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Core.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.VisualBasic.dll')
$files = @('ShowLef.cs', 'Studio.cs', 'Common.cs', 'Engine.cs', 'Ipc.cs', 'Pilot.cs', 'ExtensionPresence.cs', 'SettingsUi.cs') | ForEach-Object { Join-Path $AppDir $_ } | Where-Object { Test-Path $_ }
$ico = Join-Path $AppDir 'ShowLef.ico'
$res = @("/resource:$ico,ShowLef.ico") + (@('Rubik-Regular.ttf', 'Rubik-Medium.ttf', 'Fredoka-Variable.ttf') | ForEach-Object { "/resource:$(Join-Path $AppDir "fonts\$_"),$_" })
$o = & $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$(Join-Path $Out 'ShowLef.exe')" @refs @res @files 2>&1
if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "csc ShowLef RC=$LASTEXITCODE" }
$o = & $csc /nologo /codepage:65001 /target:exe /optimize+ "/out:$(Join-Path $Out 'showlef-host.exe')" /r:System.Core.dll (Join-Path $AppDir 'Host.cs') 2>&1
if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "csc host RC=$LASTEXITCODE" }
Write-Host "OK $Out"
