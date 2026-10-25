# build.ps1 - empaquette l'extension ShowLef dans dist\.
#   .\build.ps1          -> dist\showlef-<version>.zip        (complet, avec la cle = identifiant fixe)
#   .\build.ps1 -Store   -> dist\showlef-<version>-store.zip  (sans "key" : le Store donne l'identifiant ; AVEC YouTube)
#   .\build.ps1 -Store -NoYouTube -> idem SANS YouTube (STORE_BUILD=true + hotes bloques) : version de secours
# Depuis la 0.7.22 (« comme IDM ») l'extension ne contient aucune connaissance de site : c'est le programme qui dit
# ou capturer et quoi demander. La version de secours bloque ces hotes meme si le programme les propose.
# Choix du 20/10 (GO ShowF, « comme IDM ») : l'extension Store garde YouTube, sa fiche est non listee et c'est
# l'installeur du programme qui l'ajoute a Chrome (cle Windows, comme IDM Integration Module).
# Ne publie RIEN : tout envoi au Chrome Web Store attend le GO de ShowF pour CETTE version.
param([switch]$Store, [switch]$NoYouTube)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'extension'
$dist = Join-Path $root 'dist'
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path $sevenZip)) { throw "7-Zip introuvable : $sevenZip" }

$manifestText = [IO.File]::ReadAllText((Join-Path $src 'manifest.json'))
$version = ([regex]'"version"\s*:\s*"([0-9.]+)"').Match($manifestText).Groups[1].Value
if (-not $version) { throw 'Version introuvable dans manifest.json' }

$stage = Join-Path $env:TEMP ("showlef-build-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    Copy-Item -Path (Join-Path $src '*') -Destination $stage -Recurse
    Remove-Item -Force (Join-Path $stage 'make_icons.js'), (Join-Path $stage 'icons\icon_256.png') -ErrorAction SilentlyContinue

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    if ($Store -and $NoYouTube) {
        $cfgPath = Join-Path $stage 'config.js'
        $cfg = [IO.File]::ReadAllText($cfgPath)
        $cfg2 = $cfg -replace 'var STORE_BUILD = false;', 'var STORE_BUILD = true;'
        if ($cfg2 -eq $cfg) { throw 'STORE_BUILD introuvable dans config.js' }
        $cfg3 = $cfg2 -replace 'var SHOWLEF_BLOCKED_HOSTS = \[\];', "var SHOWLEF_BLOCKED_HOSTS = ['youtube.com', 'youtu.be', 'googlevideo.com', 'youtube-nocookie.com'];"
        if ($cfg3 -eq $cfg2) { throw 'SHOWLEF_BLOCKED_HOSTS introuvable dans config.js' }
        $cfg2 = $cfg3
        [IO.File]::WriteAllText($cfgPath, $cfg2, $utf8)
    }
    if ($Store) {

        # La cle sert seulement a fixer l'identifiant en developpement ; le Store attribue le sien.
        $mPath = Join-Path $stage 'manifest.json'
        $lines = [IO.File]::ReadAllLines($mPath) | Where-Object { $_ -notmatch '^\s*"key"\s*:' }
        [IO.File]::WriteAllText($mPath, ($lines -join "`n") + "`n", $utf8)
        if ([IO.File]::ReadAllText($mPath) -match '"key"') { throw 'La cle est restee dans le manifeste Store' }

        if ($NoYouTube) {
            # version de secours : les hotes du site sont bloques dans config.js (aucune capture, aucun envoi)
            if ([IO.File]::ReadAllText((Join-Path $stage 'config.js')) -notmatch "SHOWLEF_BLOCKED_HOSTS = \['youtube.com'") { throw 'Hotes bloques absents de la version de secours' }
            $zip = Join-Path $dist "showlef-$version-store-noyt.zip"
        } else {
            $zip = Join-Path $dist "showlef-$version-store.zip"
        }
    } else {
        $zip = Join-Path $dist "showlef-$version.zip"
    }

    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Push-Location $stage
    try { & $sevenZip a -tzip $zip * | Out-Null; $rc = $LASTEXITCODE } finally { Pop-Location }
    if ($rc -ne 0) { throw "7z a echoue (RC=$rc)" }
    $len = (Get-Item $zip).Length
    if ($len -lt 10000) { throw "Zip anormalement petit ($len octets)" }
    Write-Host "OK - $zip ($len octets)"
} finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
