// node tests/pilot-resume.mjs <videoId> <md5 attendu>
// Reprise apres coupure (telechargement pilote) : lance, ferme l'onglet apres 8 s, verifie que le programme garde les
// morceaux et que la page rouverte propose « Reprendre », relance, puis compare l'empreinte du fichier fini a celle
// d'un telechargement sans coupure.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
const [vid, want] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send };
}
const ev = (c, expr) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const sw = await conn((await (await fetch(P + '/json/list')).json()).find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
async function open() {
  const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && t.url.includes('youtube.com'));
  const t = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
  for (const o of old) await b.send('Target.closeTarget', { targetId: o.id });
  await sleep(4000);
  return t.result.targetId;
}
const items = () => ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`).then(JSON.parse);
const tab = () => ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
let target = await open();
let tabId = await tab();
console.log('depart', await ev(sw, `siteDownload(${tabId}, '720', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
await sleep(8000);
const blank = await b.send('Target.createTarget', { url: 'about:blank' });   // fermer le dernier onglet quitterait Chrome
await b.send('Target.closeTarget', { targetId: target });
await sleep(3000);
let it = (await items()).find((i) => i.kind === 'capture');
console.log('apres fermeture :', it.status, (it.received / 1048576).toFixed(1) + '/' + (it.total / 1048576).toFixed(1) + ' Mo', it.error || '');
target = await open();
tabId = await tab();
const res = await ev(sw, `readTab(${tabId}).then(function (st) { var i = st.items.find(function (x) { return x.kind === 'site'; }); return JSON.stringify(i && i.resume); })`);
console.log('page rouverte, reprise proposee :', res);
const t0 = Date.now();
console.log('reprise', await ev(sw, `siteDownload(${tabId}, '720', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
for (let k = 0; k < 300; k++) {
  await sleep(2000);
  it = (await items()).find((i) => i.id === it.id);
  if (it && (it.status === 'done' || it.status === 'error')) break;
}
await sleep(3000);
const d = JSON.parse(readFileSync(join(process.env.LOCALAPPDATA, 'ShowLef', 'downloads.json'), 'utf8'));
const x = (Array.isArray(d) ? d : d.Items).find((i) => i.Id === it.id);
const md5 = x && x.Status === 'done' ? createHash('md5').update(readFileSync(join(x.Folder, x.Name))).digest('hex') : '-';
console.log('RESULTAT', it.status, 'reprise en ' + ((Date.now() - t0) / 1000).toFixed(1) + ' s', 'md5=' + md5, md5 === want ? 'IDENTIQUE au telechargement sans coupure' : 'DIFFERENT');
await b.send('Target.closeTarget', { targetId: blank.result.targetId });
process.exit(0);
