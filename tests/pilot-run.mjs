// node tests/pilot-run.mjs <videoId> <qualite 720|480|best> <audio 0|1> [maxSec] [hidden 0|1] [par]
// Banc du telechargement PILOTE PAR LE PROGRAMME (0.7.22 / 0.7.16) dans le Chrome de test (port 9334) :
// ouvre la video, attend que le programme l'annonce (popup/bouton), lance le telechargement comme le bouton,
// suit la file du programme jusqu'a la fin, puis donne la duree, le debit et l'empreinte md5 du fichier produit.
import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
const [vid, quality = '720', audio = '0', maxSec = '600', hidden, par] = process.argv.slice(2);
const P = 'http://127.0.0.1:' + (process.env.SHOWLEF_PORT || '9334');   // 9336 : profil vierge du parcours guide
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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

const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && (t.url.includes('youtube.com') || t.url === 'about:blank'));
const yt = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
for (const t of old) await b.send('Target.closeTarget', { targetId: t.id });
await sleep(1500);
const list = await (await fetch(P + '/json/list')).json();
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('mepcfiemlmpffknhaebjocpddbaahdfc/background.js')).webSocketDebuggerUrl);
const page = await conn(list.find((x) => x.id === yt.result.targetId).webSocketDebuggerUrl);
if (hidden === '1') {
  const bl = await b.send('Target.createTarget', { url: 'about:blank' });
  await sleep(800);
  const w = await b.send('Browser.getWindowForTarget', { targetId: yt.result.targetId });
  await b.send('Browser.setWindowBounds', { windowId: w.result.windowId, bounds: { windowState: 'minimized' } });
} else { await page.send('Emulation.setFocusEmulationEnabled', { enabled: true }); await page.send('Page.bringToFront'); }
const tabId = await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
// le programme annonce-t-il la video (titre, qualites) ?
let item = null; const ta = Date.now();
for (let i = 0; i < 40 && !item; i++) {
  await sleep(500);
  const s = await ev(sw, `readTab(${tabId}).then(function (st) { var i = st.items.find(function (x) { return x.kind === 'site'; }); return i ? JSON.stringify(i) : ''; })`);
  if (s) item = JSON.parse(s);
}
console.log('annonce', item ? `${((Date.now() - ta) / 1000).toFixed(1)}s ${item.site} « ${item.title} » ${item.variants.map((v) => v.label).join(' ')}` : 'AUCUNE en 20 s');
await ev(sw, `pilotTune = ${par ? `{ par: ${+par} }` : 'null'}; 1`);
const before = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items.map(function (i) { return i.id; })); })`));
const t0 = Date.now();
console.log('depart', await ev(sw, `siteDownload(${tabId}, '${quality}', ${audio === '1'}, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`, 60000));
let last = '', it = null;
while (Date.now() - t0 < +maxSec * 1000) {
  await sleep(2000);
  const r = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`));
  it = r.find((i) => !before.includes(i.id) || (i.status !== 'done' && i.status !== 'error' && i.kind === 'capture'));
  if (!it) continue;
  const line = `${((Date.now() - t0) / 1000).toFixed(0)}s ${it.status} ${(it.received / 1048576).toFixed(1)}/${(it.total / 1048576).toFixed(1)} Mo ${it.error || ''}`;
  if (line !== last) console.log(line);
  last = line;
  if (it.status === 'done' || it.status === 'error') break;
}
const dur = (Date.now() - t0) / 1000;
if (it && it.status === 'done') {
  await sleep(3000);
  const d = JSON.parse(readFileSync(join(process.env.LOCALAPPDATA, 'ShowLef', 'downloads.json'), 'utf8'));
  const items = Array.isArray(d) ? d : d.Items;
  const x = items.find((i) => i.Id === it.id);
  const f = join(x.Folder, x.Name);
  const md5 = existsSync(f) ? createHash('md5').update(readFileSync(f)).digest('hex') : 'ABSENT';
  console.log('RESULTAT ok', `duree=${dur.toFixed(1)}s`, `pistes=${x.CapAudio}+${x.CapVideo}`, `taille=${x.Total}`, `md5=${md5}`, `debit=${(x.Total / 1048576 / dur).toFixed(2)}Mo/s`);
} else console.log('RESULTAT', it ? it.status : 'rien', `duree=${dur.toFixed(1)}s`, it && it.error ? it.error : '');
process.exit(0);
