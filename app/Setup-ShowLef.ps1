# Setup-ShowLef.ps1 - compile, enregistre l'hote natif pour Chrome et Edge, cree le raccourci du menu Demarrer.
# Usage : powershell -ExecutionPolicy Bypass -File Setup-ShowLef.ps1 [-ExtraExtensionId <id du Store>]
param([string[]]$ExtraExtensionId = @())
$ErrorActionPreference = 'Stop'
$dir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $env:LOCALAPPDATA 'ShowLef'
& (Join-Path $dir 'Build-ShowLef.ps1')

# Identifiant fixe de l'extension non empaquetee (cle publique du manifeste) + ceux du Store le moment venu.
$ids = @('mepcfiemlmpffknhaebjocpddbaahdfc', 'hibookeofnmdllfleebckdkbgijpklfb') + $ExtraExtensionId   # non empaquetee, Chrome Web Store
$manifest = [ordered]@{
    name            = 'com.showlef.app'
    description     = 'ShowLef - download manager'
    path            = (Join-Path $outDir 'showlef-host.exe')
    type            = 'stdio'
    allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
}
$json = Join-Path $outDir 'com.showlef.app.json'
[IO.File]::WriteAllText($json, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

foreach ($b in 'Google\Chrome', 'Microsoft\Edge', 'BraveSoftware\Brave-Browser', 'Chromium') {
    $k = "HKCU:\Software\$b\NativeMessagingHosts\com.showlef.app"
    New-Item -Path $k -Force | Out-Null
    Set-ItemProperty -Path $k -Name '(default)' -Value $json
}

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'ShowLef.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $outDir 'ShowLef.exe'
$s.IconLocation = (Join-Path $outDir 'ShowLef.ico')
$s.Save()

Write-Host "OK - hote natif enregistre ($json), raccourci $lnk"
Write-Host "Extension : chrome://extensions > mode developpeur > charger $(Join-Path (Split-Path $dir) 'extension')"
