// node tests/pilot-cancel.mjs <videoId> [delete|pause]
// Annulation : lance un telechargement pilote, le supprime (ou le met en pause) DANS LE PROGRAMME apres 6 s, puis mesure
// combien de temps la page continue d'envoyer des requetes (objectif : plus aucune requete nouvelle apres 2 s).
const [vid, how = 'delete'] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {}; const ev = [];
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } else if (m.method) ev.push(m); };
  const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send, ev };
}
const ev = (c, expr) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && t.url.includes('youtube.com'));
const yt = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
for (const t of old) await b.send('Target.closeTarget', { targetId: t.id });
await sleep(1500);
const list = await (await fetch(P + '/json/list')).json();
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
const page = await conn(list.find((x) => x.id === yt.result.targetId).webSocketDebuggerUrl);
await page.send('Network.enable');
await sleep(3000);
const tabId = await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
console.log('depart', await ev(sw, `siteDownload(${tabId}, '720', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
await sleep(6000);
const id = await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { var i = r.items.find(function (x) { return x.status === 'downloading' && x.kind === 'capture'; }); return i ? i.id : ''; })`);
const tc = Date.now();
console.log(how, id, await ev(sw, `nativeSend({ cmd: 'control', id: '${id}', action: '${how}' }).then(JSON.stringify)`));
await sleep(6000);
// seulement les requetes de ShowLef (numero rn >= 1001) : le lecteur de la page fait aussi les siennes en lisant
const reqs = page.ev.filter((e) => e.method === 'Network.requestWillBeSent' && /videoplayback/.test(e.params.request.url) && e.params.request.method === 'POST'
  && +((/[?&]rn=(\d+)/.exec(e.params.request.url) || [])[1] || 0) >= 1001);
const t0 = reqs.length ? reqs[0].params.wallTime * 1000 : tc;
const after = reqs.filter((e) => e.params.wallTime * 1000 > tc);
console.log('requetes SABR', reqs.length, '; apres l\'annulation :', after.map((e) => '+' + (e.params.wallTime * 1000 - tc).toFixed(0) + 'ms').join(' ') || 'aucune');
const late = after.filter((e) => e.params.wallTime * 1000 - tc > 2000).length;
console.log('RESULTAT', late === 0 ? 'OK' : 'ECHEC', 'requetes lancees plus de 2 s apres l\'annulation :', late);
process.exit(0);
