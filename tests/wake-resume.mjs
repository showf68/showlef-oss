// node tests/wake-resume.mjs <videoId> [audio 0|1] [quality]
// Reprise sans second clic (0.7.35) : lance un telechargement dans un onglet de fond, ferme l'onglet (coupure), puis fait
// comme le bouton « Reprendre » du programme (control/resume). Attendu : l'extension rouvre la page EN ARRIERE-PLAN toute
// seule, la capture reprend sur le MEME element, finit, et l'onglet qu'elle a ouvert est refermé.
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
const items = async () => { const r = await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`, 60000); return r.startsWith('[') ? JSON.parse(r) : []; };
const tabs = async () => JSON.parse(await ev(sw, `chrome.tabs.query({}).then(function (t) { return JSON.stringify(t.map(function (x) { return { id: x.id, url: x.url, active: x.active }; })); })`));
console.log('ping', (await ev(sw, `appCheck(true).then(function (a) { return a.state + ' ' + a.version; })`)));
await sleep(2000);

// lignes en erreur laissees par d'autres essais : retirees (sinon la capture reprend une ancienne ligne et on ne la reconnait plus)
for (const x of (await items()).filter((i) => i.status === 'error')) await ev(sw, `nativeSend({ cmd: 'control', id: '${x.id}', action: 'remove' })`);
const before = (await items()).map((i) => i.id);
const tabId = await ev(sw, `chrome.tabs.create({ url: 'https://www.youtube.com/watch?v=${vid}', active: false }).then(function (t) { return t.id; })`);
for (let i = 0; i < 60; i++) {
  const s = await ev(sw, `readTab(${tabId}).then(function (st) { return st.items.some(function (x) { return x.kind === 'site'; }) ? 'y' : ''; })`);
  if (s === 'y') break; await sleep(500);
}
console.log('depart', await ev(sw, `siteDownload(${tabId}, '${quality}', ${audio === '1'}, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`, 60000));
await sleep(9000);
await ev(sw, `chrome.tabs.remove(${tabId})`);   // coupure
let it = null;
for (let i = 0; i < 30; i++) {
  await sleep(1500);
  it = (await items()).find((x) => !before.includes(x.id));
  if (it && it.status === 'error') break;
}
console.log('apres coupure :', it && it.status, it && (it.received / 1048576).toFixed(1) + '/' + (it.total / 1048576).toFixed(1) + ' Mo');
if (!it || it.status !== 'error') { console.log('ECHEC : pas de ligne en erreur'); process.exit(1); }
const nTabs0 = (await tabs()).length;
const t0 = Date.now();
console.log('« Reprendre » (programme) ->', JSON.stringify(await ev(sw, `nativeSend({ cmd: 'control', id: '${it.id}', action: 'resume' })`)));
let seenOwn = false, last = '';
for (let i = 0; i < 90; i++) {
  await sleep(2000);
  const ts = await tabs();
  if (ts.some((t) => (t.url || '').includes(vid))) seenOwn = true;
  const now = (await items()).find((x) => x.id === it.id);
  const line = now ? `${((Date.now() - t0) / 1000).toFixed(0)}s ${now.status} ${(now.received / 1048576).toFixed(1)}/${(now.total / 1048576).toFixed(1)} Mo ${now.error || ''}` : 'ligne disparue';
  if (line !== last) console.log(line); last = line;
  if (now && (now.status === 'done' || (now.status === 'error' && Date.now() - t0 > 45000))) break;
}
await sleep(3000);
const ts = await tabs();
console.log('onglet de fond ouvert par l\'extension :', seenOwn, '| onglets ouverts a la fin :', ts.length, '(avant', nTabs0 + ')', '| onglet video encore la :', ts.some((t) => (t.url || '').includes(vid)));
console.log('actif :', JSON.stringify(ts.filter((t) => t.active).map((t) => t.url)));
process.exit(0);
