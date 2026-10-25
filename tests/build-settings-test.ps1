$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $env:TEMP 'showlef-settings-check'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$refs = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Core.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.VisualBasic.dll','/r:Microsoft.CSharp.dll')
$files = @('ShowLef.cs','Studio.cs','Common.cs','Engine.cs','Ipc.cs','Pilot.cs','ExtensionPresence.cs','SettingsUi.cs','Setup.cs') | ForEach-Object { Join-Path $root "app/$_" }
$exe = Join-Path $out 'settings-test.exe'
& $csc /nologo /codepage:65001 /target:exe /main:SettingsTest "/out:$exe" @refs @files (Join-Path $root 'tests/SettingsTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc echouee' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Banc detection/reglages en echec' }
