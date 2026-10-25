# Tape le chemin du dossier dans la fenetre « Selectionner le dossier » de Chrome (ouverte par le bouton « Charger
# l'extension non empaquetee »), comme le fait l'utilisateur guide par l'installeur : Ctrl+V du chemin, Entree, puis le
# bouton « Selectionner un dossier » (Entree seule ne fait qu'entrer dans le dossier : mesure 21/10).
param([string]$Dir)
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Set-Clipboard -Value $Dir
Start-Sleep -Milliseconds 1500
# la fenetre de choix de dossier est au premier plan (ouverte par Chrome) : on colle le chemin dans « Dossier : »
[System.Windows.Forms.SendKeys]::SendWait('^v')
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 1500
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
# la fenetre appartient a celle de Chrome (enfant dans l'arbre d'automatisation), pas au bureau
$dlg = $null
foreach ($w in $A::RootElement.FindAll($T::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'Chrome_WidgetWin_1')))) {
    $dlg = $w.FindFirst($T::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, '#32770')))
    if ($dlg) { break }
}
if (-not $dlg) { "fenetre deja fermee"; exit 0 }
# bouton de validation : enfant direct, classe « Button », identifiant « 1 » (IDOK) quelle que soit la langue. Expose comme
# un panneau sans action d'automatisation : vrai clic souris en son centre.
$ok = $dlg.FindFirst($T::Children, (New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, '1')),
    (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'Button')))))
if (-not $ok) { "bouton introuvable"; exit 1 }
Add-Type 'using System; using System.Runtime.InteropServices; public static class GuideMouse { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e); }'
$r = $ok.Current.BoundingRectangle
[void][GuideMouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 200
[GuideMouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [GuideMouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
"valide : " + $ok.Current.Name
