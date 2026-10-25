# Build-Setup.ps1 - fabrique dist\ShowLef-Setup.exe : l'installeur public (un seul fichier, sans droits admin).
# Compile ShowLef.exe et showlef-host.exe dans un dossier de travail (sans toucher a l'installation de ce PC),
# puis les embarque dans l'installeur (app/Setup.cs). Affiche taille et SHA-256 (publies sur ext.i-call.me).
$ErrorActionPreference = 'Stop'
$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path (Split-Path $dir) 'dist'
$work = Join-Path $dist 'setup-work'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$ver = ([regex]::Match((Get-Content (Join-Path $dir 'Common.cs') -Raw -Encoding UTF8), 'Version = "([0-9.]+)"')).Groups[1].Value
$setupVer = ([regex]::Match((Get-Content (Join-Path $dir 'Setup.cs') -Raw -Encoding UTF8), 'Version = "([0-9.]+)"')).Groups[1].Value
if ($ver -ne $setupVer) { throw "Versions differentes : Common.cs $ver, Setup.cs $setupVer" }

$info = Join-Path $work 'AssemblyInfo.cs'
[IO.File]::WriteAllText($info, @"
using System.Reflection;
[assembly: AssemblyTitle("ShowLef")]
[assembly: AssemblyDescription("ShowLef - free audio and video download manager")]
[assembly: AssemblyCompany("ICALL")]
[assembly: AssemblyProduct("ShowLef")]
[assembly: AssemblyCopyright("(c) 2026 ICALL")]
[assembly: AssemblyVersion("$ver.0")]
[assembly: AssemblyFileVersion("$ver.0")]
"@, (New-Object Text.UTF8Encoding $false))

function Csc([string]$label, [string[]]$a) {
    $out = & $csc @a 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "Compilation $label echouee (csc RC=$LASTEXITCODE)" }
}
$ico  = Join-Path $dir 'ShowLef.ico'
$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Core.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.VisualBasic.dll')
$src  = @('ShowLef.cs', 'Studio.cs', 'Common.cs', 'Engine.cs', 'Ipc.cs', 'Pilot.cs', 'ExtensionPresence.cs', 'SettingsUi.cs') | ForEach-Object { Join-Path $dir $_ }
$res  = @("/resource:$ico,ShowLef.ico") + (@('Rubik-Regular.ttf', 'Rubik-Medium.ttf', 'Fredoka-Variable.ttf') | ForEach-Object { "/resource:$(Join-Path $dir "fonts\$_"),$_" })
$exe  = Join-Path $work 'ShowLef.exe'
$hostExe = Join-Path $work 'showlef-host.exe'
Csc 'ShowLef' (@('/nologo', '/codepage:65001', '/target:winexe', '/optimize+', "/win32icon:$ico", "/out:$exe") + $refs + $res + $src + @($info))
Csc 'showlef-host' @('/nologo', '/codepage:65001', '/target:exe', '/optimize+', "/out:$hostExe", '/r:System.Core.dll', (Join-Path $dir 'Host.cs'), $info)

# (0.7.17) l'extension voyage AVEC le programme tant qu'elle n'est pas publiee sur le Store : paquet de developpement
# (avec la cle = identifiant fixe, autorise par l'hote natif), construit ici par build.ps1
& (Join-Path (Split-Path $dir) 'build.ps1') | Out-Null
$extManifest = [IO.File]::ReadAllText((Join-Path (Split-Path $dir) 'extension\manifest.json'))
$extVer = ([regex]'"version"\s*:\s*"([0-9.]+)"').Match($extManifest).Groups[1].Value
$extZip = Join-Path $dist "showlef-$extVer.zip"
if (-not (Test-Path $extZip)) { throw "Paquet de l'extension absent : $extZip" }
$setup = Join-Path $dist 'ShowLef-Setup.exe'
Csc 'ShowLef-Setup' @('/nologo', '/codepage:65001', '/target:winexe', '/optimize+', "/win32icon:$ico", "/out:$setup",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', "/resource:$extZip,extension.zip",
    "/resource:$exe,ShowLef.exe", "/resource:$hostExe,showlef-host.exe", "/resource:$ico,ShowLef.ico", "/resource:$(Join-Path (Split-Path $dir) 'extension\icons\icon_128.png'),logo.png",
    (Join-Path $dir 'Setup.cs'), (Join-Path $dir 'ExtensionPresence.cs'), $info)
Remove-Item $work -Recurse -Force
$h = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Host "OK - $setup ($((Get-Item $setup).Length) octets) v$ver sha256 $h"
