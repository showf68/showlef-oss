// node tests/pilot-two.mjs <video1> <md5 1> <video2> <md5 2>
// Deux telechargements pilotes EN MEME TEMPS dans deux onglets (meme connexion au programme, fins a des moments
// differents) : les deux doivent finir avec la bonne empreinte.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
const [v1, m1, v2, m2] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}) => new Promise((r) => { const i = ++id; wait[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send };
}
const ev = (c, expr) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && t.url.includes('youtube'));
await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + v1 });
await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + v2 });
for (const o of old) await b.send('Target.closeTarget', { targetId: o.id });
await sleep(7000);
const sw = await conn((await (await fetch(P + '/json/list')).json()).find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
const ids = JSON.parse(await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return JSON.stringify(t.map(function (x) { return [x.id, x.url]; })); })`));
// elements deja FINIS avant le depart (un element en erreur peut etre REPRIS par ce banc : il compte)
const before = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items.filter(function (i) { return i.status === 'done'; }).map(function (i) { return i.id; })); })`));
const t0 = Date.now();
for (const [id] of ids) console.log('depart', id, await ev(sw, `siteDownload(${id}, '720', false, 0).then(function (r) { track(${id}, 0, r); return JSON.stringify(r); })`));
let items = [];
for (let k = 0; k < 300; k++) {
  await sleep(2000);
  items = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`)).filter((i) => !before.includes(i.id));
  if (items.length === 2 && items.every((i) => i.status === 'done' || i.status === 'error')) break;
}
await sleep(3000);
const d = JSON.parse(readFileSync(join(process.env.LOCALAPPDATA, 'ShowLef', 'downloads.json'), 'utf8'));
const want = [m1, m2]; let good = 0;
for (const it of items) {
  const x = (Array.isArray(d) ? d : d.Items).find((i) => i.Id === it.id);
  const md5 = x && x.Status === 'done' ? createHash('md5').update(readFileSync(join(x.Folder, x.Name))).digest('hex') : '-';
  if (want.includes(md5)) good++;
  console.log(it.status, md5, it.name);
}
console.log('RESULTAT', good === 2 ? 'OK' : 'ECHEC', `${good}/2 identiques en ${((Date.now() - t0) / 1000).toFixed(0)} s`);
process.exit(0);
