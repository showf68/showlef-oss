// node tests/playlist-run.mjs   (le programme doit tourner avec --dir <dossier> : « auto » n'est accepte que par un banc)
// Playlist de bout en bout (0.7.35) : 3 videos dont une qui n'existe pas. Attendu : les videos partent UNE A LA FOIS, chacune
// dans un onglet de fond ouvert puis referme par l'extension, la fausse est sautee, fichiers « 01 - ... » et « 03 - ... »
// dans un dossier au nom de la playlist, aucun onglet laisse ouvert.
import { setTimeout as sleep } from 'node:timers/promises';
import { readdirSync, existsSync } from 'node:fs';
const P = 'http://127.0.0.1:9334';
const dir = process.argv[2];   // dossier de donnees du banc (pour lire les fichiers produits)
const list = await (await fetch(P + '/json/list')).json();
const ws = new WebSocket(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
await new Promise((o) => { ws.onopen = o; });
let id = 0; const wait = {};
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
const ev = (expr) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method: 'Runtime.evaluate', params: { expression: expr, awaitPromise: true, returnByValue: true } })); setTimeout(() => ok({ result: { result: { value: 'TIMEOUT' } } }), 60000); })
  .then((r) => r.result.exceptionDetails ? 'EXC ' + JSON.stringify(r.result.exceptionDetails).slice(0, 200) : r.result.result.value);
console.log('ping', await ev(`appCheck(true).then(function (a) { return a.state + ' ' + a.version; })`));
await sleep(1500);
const items = [{ id: 'zgXUmQzInxI', title: 'Premier', idx: 1 }, { id: 'AAAAAAAAAAA', title: 'Inexistante', idx: 2 }, { id: 'kP1jrKkSZfE', title: 'Troisieme', idx: 3 }];
const t0 = Date.now();
console.log('playlist_add', await ev(`nativeSend({ cmd: 'playlist_add', name: 'Essai: playlist', list: 'PLx', items: ${JSON.stringify(items)}, quality: '360', audioOnly: true, audioFormat: 'mp3', auto: true }).then(function (r) { return JSON.stringify(r); })`));
let maxTabs = 0, last = '';
for (let i = 0; i < 100; i++) {
  await sleep(3000);
  const tabs = JSON.parse(await ev(`chrome.tabs.query({}).then(function (t) { return JSON.stringify(t.map(function (x) { return x.url; })); })`));
  const yt = tabs.filter((u) => u.includes('youtube.com/watch')).length; maxTabs = Math.max(maxTabs, yt);
  const raw = await ev(`nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items); })`);
  const its = raw.startsWith('[') ? JSON.parse(raw) : [];
  const line = `${((Date.now() - t0) / 1000).toFixed(0)}s onglets video=${yt} ` + its.slice(0, 4).map((x) => x.status + ':' + x.name.slice(0, 22)).join(' | ');
  if (line !== last) console.log(line); last = line;
  if (its.filter((x) => x.status === 'done').length >= 2 && yt === 0) break;
}
await sleep(2000);
console.log('onglets video max en meme temps :', maxTabs);
if (dir) {
  const walk = (d, o = []) => { for (const e of readdirSync(d, { withFileTypes: true })) { const p = d + '/' + e.name; e.isDirectory() ? walk(p, o) : o.push(p); } return o; };
  const base = process.argv[3];
  if (base && existsSync(base)) console.log(walk(base).map((p) => p.replace(base, '')).join('\n'));
}
process.exit(0);
