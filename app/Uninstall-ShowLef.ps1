# Uninstall-ShowLef.ps1 - retire ShowLef (programme, hote natif, raccourci, demarrage). Les fichiers telecharges restent.
$ErrorActionPreference = 'Continue'
Get-Process -Name ShowLef, showlef-host -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($b in 'Google\Chrome', 'Microsoft\Edge', 'BraveSoftware\Brave-Browser', 'Chromium') {
    Remove-Item "HKCU:\Software\$b\NativeMessagingHosts\com.showlef.app" -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ShowLef -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'ShowLef.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'ShowLef') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "ShowLef retire."
