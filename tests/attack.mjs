// node tests/attack.mjs <videoId> <md5 attendu en 720p>
// Etape 5 du chantier « comme IDM » : une page (ici la page video elle-meme, monde de la page = celui de hook.js)
// forge tout ce qu'elle peut : fausses regles, fausses captures vers un serveur pirate local, faux ordres de lecture,
// inondation. Puis un vrai telechargement est lance. Attendu : AUCUNE requete vers le serveur pirate, l'inondation
// bornee, et le fichier final identique a celui d'un telechargement sans attaque.
import http from 'node:http';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
const [vid, want] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let hits = [];
const srv = http.createServer((q, s) => { hits.push(q.method + ' ' + q.url); s.setHeader('Access-Control-Allow-Origin', '*'); s.end('pirate'); }).listen(9555);
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send };
}
const ev = (c, expr) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && t.url.includes('youtube.com'));
const yt = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
for (const t of old) await b.send('Target.closeTarget', { targetId: t.id });
await sleep(4000);
const list = await (await fetch(P + '/json/list')).json();
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
const page = await conn(list.find((x) => x.id === yt.result.targetId).webSocketDebuggerUrl);
const d0 = await ev(sw, 'pilotStats.data');
// --- l'attaque, executee DANS la page (monde partage avec hook.js) ---
console.log('attaque', await ev(page, `(function () {
  var evil = 'http://127.0.0.1:9555', n = 0, body = new Uint8Array(200).buffer;
  // 1. fausses regles : tout capturer, y compris vers le serveur pirate
  window.postMessage({ __showlef: 'rules', capture: [{ id: 'sabr', host: '127.0.0.1', path: '/', part: 'request', maxBytes: 1e9 }] }, '*');
  // 2. fausses captures « modele du lecteur » pointant vers le serveur pirate (http, https, sosie)
  ['/videoplayback?sabr=1', ''].forEach(function (p) {
    [evil + p, 'https://127.0.0.1:9555' + p, 'https://googlevideo.com.127.0.0.1.nip.io' + p].forEach(function (u) {
      window.postMessage({ __showlef: 'cap', rule: 'sabr', url: u, body: body }, '*'); n++;
    });
  });
  // 3. regle inexistante, corps geant, mauvais type
  window.postMessage({ __showlef: 'cap', rule: 'exec', url: evil, body: body }, '*'); n++;
  window.postMessage({ __showlef: 'cap', rule: 'player', url: evil, body: new Uint8Array(9e6).buffer }, '*'); n++;
  window.postMessage({ __showlef: 'cap', rule: 'sabr', url: evil, body: 'pas un tableau' }, '*'); n++;
  // 4. faux ordres : lecture muette, fin de lecture, a repetition
  for (var i = 0; i < 20; i++) { window.postMessage({ __showlef: 'warm', id: i, until: 'sabr' }, '*'); window.postMessage({ __showlef: 'warmed', id: i, ok: true }, '*'); }
  // 5. inondation : 200 captures d'une regle connue
  for (var k = 0; k < 200; k++) window.postMessage({ __showlef: 'cap', rule: 'sabr', url: evil + '/videoplayback?sabr=1&k=' + k, body: body }, '*');
  return 'messages forges : ' + (n + 240);
})()`));
await sleep(5000);
const relayed = (await ev(sw, 'pilotStats.data')) - d0;
console.log('captures relayees au programme pendant l\'attaque :', relayed, '(plafond attendu : 30 par regle et par minute, plus les vraies captures)');
// --- un vrai telechargement apres l'attaque ---
const tabId = await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
const before = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items.map(function (i) { return i.id; })); })`));
console.log('depart', await ev(sw, `siteDownload(${tabId}, '720', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
let it = null;
for (let k = 0; k < 300; k++) {
  await sleep(2000);
  const items = JSON.parse(await ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`));
  it = items.find((i) => !before.includes(i.id));
  if (it && (it.status === 'done' || it.status === 'error')) break;
}
await sleep(3000);   // la liste dit « fini » avant que downloads.json soit ecrit
const d = JSON.parse(readFileSync(join(process.env.LOCALAPPDATA, 'ShowLef', 'downloads.json'), 'utf8'));
const x = it && (Array.isArray(d) ? d : d.Items).find((i) => i.Id === it.id);
const md5 = x && x.Status === 'done' ? createHash('md5').update(readFileSync(join(x.Folder, x.Name))).digest('hex') : '-';
console.log('telechargement apres attaque :', it ? it.status : 'rien', 'md5=' + md5, md5 === want ? '(identique)' : '(DIFFERENT)');
console.log('requetes recues par le serveur pirate :', hits.length, hits.slice(0, 5).join(' | '));
console.log('RESULTAT', hits.length === 0 && relayed <= 40 && md5 === want ? 'OK' : 'ECHEC');
srv.close();
process.exit(0);
