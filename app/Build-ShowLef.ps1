# Build-ShowLef.ps1 - compile ShowLef.exe (interface) et showlef-host.exe (hote natif) dans %LOCALAPPDATA%\ShowLef
# Aucun package externe : csc.exe est livre avec le .NET Framework de Windows (C# 5).
$ErrorActionPreference = 'Stop'
$dir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $env:LOCALAPPDATA 'ShowLef'
$exe    = Join-Path $outDir 'ShowLef.exe'
$host_  = Join-Path $outDir 'showlef-host.exe'
$ico    = Join-Path $outDir 'ShowLef.ico'
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Core.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.VisualBasic.dll')
$src  = @('ShowLef.cs', 'Studio.cs', 'Common.cs', 'Engine.cs', 'Ipc.cs', 'Pilot.cs', 'ExtensionPresence.cs', 'SettingsUi.cs') | ForEach-Object { Join-Path $dir $_ }
# Logo (app/ShowLef.ico, multi-tailles) et polices de v1 (app/fonts, licence OFL) embarques dans l'exe : rien a installer a cote.
$srcIco = Join-Path $dir 'ShowLef.ico'
$res  = @("/resource:$srcIco,ShowLef.ico") + (@('Rubik-Regular.ttf', 'Rubik-Medium.ttf', 'Fredoka-Variable.ttf') | ForEach-Object { "/resource:$(Join-Path $dir "fonts\$_"),$_" })

# L'exe peut etre verrouille : instance en cours, relance par l'hote natif (l'extension interroge le
# programme chaque seconde quand sa popup est ouverte), ou antivirus. On arrete et on reessaie.
function Compile([string]$label, [string[]]$cscArgs) {
    for ($i = 1; $i -le 15; $i++) {
        Get-Process -Name ShowLef, showlef-host -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
        $out = & $csc @cscArgs 2>&1
        if ($LASTEXITCODE -eq 0) { return }
        if (-not ($out -match 'CS0016')) { $out | Write-Host; throw "Compilation $label echouee (csc RC=$LASTEXITCODE)" }
        Start-Sleep -Seconds 1
    }
    throw "Compilation $label : fichier de sortie verrouille"
}

Copy-Item $srcIco $ico -Force   # raccourci du menu Demarrer
Compile 'ShowLef' (@('/nologo', '/codepage:65001', '/target:winexe', '/optimize+', "/win32icon:$srcIco", "/out:$exe") + $refs + $res + $src)
Compile 'showlef-host' @('/nologo', '/codepage:65001', '/target:exe', '/optimize+', "/out:$host_", '/r:System.Core.dll', (Join-Path $dir 'Host.cs'))

Write-Host "OK - $exe ($((Get-Item $exe).Length) octets), $host_ ($((Get-Item $host_).Length) octets)"
