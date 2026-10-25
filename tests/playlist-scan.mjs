// node tests/playlist-scan.mjs
// Lecture d'une playlist (0.7.35) sur une page FIXE qui imite la structure de YouTube (elements ytd-playlist-video-renderer,
// liste qui grandit au defilement, video privee, doublon). Ne prouve que la logique de lecture : sur une vraie page YouTube,
// voir README (« non eprouve sur une vraie playlist » tant que le filtre Techloq de la machine la bloque).
import http from 'node:http';
const html = `<!doctype html><meta charset="utf-8"><title>Ma liste - YouTube</title><body style="height:3000px">
<ytd-playlist-header-renderer><h1>Ma liste d'essai</h1></ytd-playlist-header-renderer><div id="list"></div>
<script>
var all = [['aaaaaaaaaaa','Premier'],['bbbbbbbbbbb','Deuxième'],['ccccccccccc','[Private video]'],['ddddddddddd','Quatrième'],['bbbbbbbbbbb','Deuxième'],
  ['eeeeeeeeeee','Cinquième'],['fffffffffff','Sixième'],['ggggggggggg','שביעי']];
var shown = 0;
function more(n) { for (var i = 0; i < n && shown < all.length; i++, shown++) {
  var r = document.createElement('ytd-playlist-video-renderer'); r.innerHTML = '<a id="video-title" href="/watch?v=' + all[shown][0] + '&list=PLx">' + all[shown][1] + '</a>';
  document.getElementById('list').appendChild(r); } }
more(5);
window.addEventListener('scroll', function () { more(3); });
</script>`;
const srv = http.createServer((q, r) => { r.setHeader('content-type', 'text/html; charset=utf-8'); r.end(html); }).listen(0, '127.0.0.1');
await new Promise((o) => srv.on('listening', o));
const url = `http://127.0.0.1:${srv.address().port}/playlist?list=PLx`;
const P = 'http://127.0.0.1:9334';
const list = await (await fetch(P + '/json/list')).json();
const ws = new WebSocket(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
await new Promise((o) => { ws.onopen = o; });
let id = 0; const wait = {};
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
const ev = (expr) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method: 'Runtime.evaluate', params: { expression: expr, awaitPromise: true, returnByValue: true } })); })
  .then((r) => r.result.exceptionDetails ? 'EXC ' + JSON.stringify(r.result.exceptionDetails).slice(0, 200) : r.result.result.value);
const tabId = await ev(`chrome.tabs.create({ url: '${url}', active: true }).then(function (t) { return t.id; })`);
await new Promise((r) => setTimeout(r, 1500));
// fenetre hors ecran = page « cachee » (pas d'evenement de defilement) : la page est rendue visible par le protocole de debogage
const pg = (await (await fetch(P + '/json/list')).json()).find((x) => x.type === 'page' && x.url.includes('/playlist?list=PLx'));
const pws = new WebSocket(pg.webSocketDebuggerUrl); await new Promise((o) => { pws.onopen = o; });
pws.send(JSON.stringify({ id: 1, method: 'Emulation.setFocusEmulationEnabled', params: { enabled: true } })); pws.send(JSON.stringify({ id: 2, method: 'Page.bringToFront' }));
await new Promise((r) => setTimeout(r, 1000));
const out = JSON.parse(await ev(`chrome.scripting.executeScript({ target: { tabId: ${tabId} }, func: scanPlaylist }).then(function (r) { return JSON.stringify(r[0].result); })`));
await ev(`chrome.tabs.remove(${tabId})`);
console.log(JSON.stringify(out));
const ids = out.items.map((x) => x.id).join(',');
const ok = out.name === "Ma liste d'essai" && out.list === 'PLx' && ids === 'aaaaaaaaaaa,bbbbbbbbbbb,ddddddddddd,eeeeeeeeeee,fffffffffff,ggggggggggg'
  && out.items.map((x) => x.idx).join(',') === '1,2,3,4,5,6';
console.log(ok ? 'OK : nom, liste, defilement, privee ecartee, doublon ecarte, numerotation 1..6' : 'ECHEC');
pws.close(); srv.close(); process.exit(ok ? 0 : 1);
