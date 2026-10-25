#!/bin/bash
# bash tests/guide-fresh.sh <nom du profil> [video]
# Parcours complet d'un nouvel utilisateur guide par l'installeur, dans un Chrome a profil VIERGE (port 9336) :
# ferme le Chrome de test precedent, ouvre le profil neuf, mode developpeur + « Charger l'extension non empaquetee » sur le
# dossier depose par le programme, controle l'extension, puis telecharge la video (premiere visite de YouTube du profil).
set -u
cd "$(dirname "$0")/.."
PROF="$TEMP/showlef-guide-$1"; VID="${2:-SfxNQPhEnM8}"
EXT="$(cygpath -w "$LOCALAPPDATA")\\ShowLef\\extension"
WS=$(curl -s http://127.0.0.1:9336/json/version | sed -nE 's/.*"webSocketDebuggerUrl": "([^"]+)".*/\1/p')
[ -n "$WS" ] && node -e "const w=new WebSocket(process.argv[1]);w.onopen=()=>{w.send(JSON.stringify({id:1,method:'Browser.close'}));setTimeout(()=>process.exit(0),1500)}" "$WS" 2>/dev/null && sleep 2
rm -rf "$PROF"
"/c/Program Files/Google/Chrome/Application/chrome.exe" --user-data-dir="$(cygpath -w "$PROF")" --remote-debugging-port=9336 --no-first-run --no-default-browser-check about:blank >/dev/null 2>&1 &
sleep 4
node tests/guide-real.mjs 9336 load 2>&1 | grep -v Assertion
powershell -NoProfile -ExecutionPolicy Bypass -File tests/guide-load.ps1 -Dir "$EXT"
sleep 3
L=$(node tests/guide-real.mjs 9336 list 2>&1 | grep -v Assertion); echo "$L"
echo "$L" | grep -q mepcfiemlmpffknhaebjocpddbaahdfc || { echo "EXTENSION NON AJOUTEE : arret (aucune visite de YouTube faite)"; exit 1; }
SHOWLEF_PORT=9336 timeout 400 node tests/pilot-run.mjs "$VID" 720 0 380 2>&1 | grep -v Assertion | tail -2
grep -a "pilot :" "$LOCALAPPDATA/ShowLef/showlef.log" | tail -6
