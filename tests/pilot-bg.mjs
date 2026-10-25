// node tests/pilot-bg.mjs <videoId> [audio 0|1] [quality]
// Essai « onglet de fond » : la page YouTube est ouverte par l'extension elle-meme (chrome.tabs.create, active:false) dans
// une fenetre ou un autre onglet est au premier plan (visibilityState reel = hidden, ce que CDP ne donne pas), puis
// le telechargement pilote est lance sans jamais afficher l'onglet. Resultat : visibilite, annonce, duree, etat final.
import { setTimeout as sleep } from 'node:timers/promises';
const [vid, audio = '1', quality = '360'] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}, to = 30000) => new Promise((ok) => {
    const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params }));
    setTimeout(() => { if (wait[i]) { delete wait[i]; ok({ timeout: true }); } }, to);
  });
  return { ws, send };
}
const ev = (c, expr, to) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }, to)
  .then((r) => r.timeout ? 'TIMEOUT' : r.result.exceptionDetails ? 'EXC ' + JSON.stringify(r.result.exceptionDetails).slice(0, 300) : r.result.result.value);
const list = await (await fetch(P + '/json/list')).json();
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
// un onglet au premier plan (about:blank existe deja), puis la video en fond
const tabId = await ev(sw, `chrome.tabs.create({ url: 'https://www.youtube.com/watch?v=${vid}', active: ${process.env.FG === '1'} }).then(function (t) { return t.id; })`);
await sleep(1500);
const l2 = await (await fetch(P + '/json/list')).json();
const yt = l2.find((x) => x.type === 'page' && x.url.includes(vid));
const page = await conn(yt.webSocketDebuggerUrl);
console.log('visibilite', await ev(page, 'document.visibilityState'));
let item = null; const ta = Date.now();
for (let i = 0; i < 60 && !item; i++) {
  await sleep(500);
  const s = await ev(sw, `readTab(${tabId}).then(function (st) { var i = st.items.find(function (x) { return x.kind === 'site'; }); return i ? JSON.stringify(i) : ''; })`);
  if (s) item = JSON.parse(s);
}
console.log('annonce', item ? `${((Date.now() - ta) / 1000).toFixed(1)}s « ${item.title} »` : 'AUCUNE en 30 s');
const before = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items.map(function (i) { return i.id; })); })`));
const t0 = Date.now();
console.log('depart', await ev(sw, `siteDownload(${tabId}, '${quality}', ${audio === '1'}, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`, 60000));
let last = '', it = null;
while (Date.now() - t0 < 180000) {
  await sleep(2000);
  const raw = await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`);
  if (!raw.startsWith('[')) { console.log('liste', raw.slice(0, 80)); continue; }
  const r = JSON.parse(raw);
  it = r.find((i) => !before.includes(i.id)) || it;
  if (!it) continue;
  const line = `${((Date.now() - t0) / 1000).toFixed(0)}s ${it.status} ${(it.received / 1048576).toFixed(1)}/${(it.total / 1048576).toFixed(1)} Mo ${it.error || ''}`;
  if (line !== last) console.log(line);
  last = line;
  if (it.status === 'done' || it.status === 'error') break;
}
console.log('visibilite fin', await ev(page, 'document.visibilityState'), 'RESULTAT', it ? it.status : 'aucun', it && it.error || '');
await ev(sw, `chrome.tabs.remove(${tabId})`);
process.exit(0);
