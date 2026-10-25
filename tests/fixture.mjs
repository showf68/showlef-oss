// node tests/fixture.mjs <videoId> <dossier> [secondes]
// Enregistre, dans le Chrome de test (port 9334), ce que le lecteur YouTube echange REELLEMENT : requetes SABR
// (adresse + corps binaire) et leurs reponses UMP, plus la reponse du lecteur (getPlayerResponse). Sert de jeu
// d'essai au moteur du programme (tests rejouables sans reseau). Rien n'est envoye nulle part.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
const [vid, out, secs = '25'] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {}; const ev = [];
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } else if (m.method) ev.push(m); };
  const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send, ev };
}
mkdirSync(out, { recursive: true });
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const t = await b.send('Target.createTarget', { url: 'about:blank' });
await sleep(800);
const pg = (await (await fetch(P + '/json/list')).json()).find((x) => x.id === t.result.targetId);
const c = await conn(pg.webSocketDebuggerUrl);
await c.send('Network.enable', { maxPostDataSize: 1 << 20, maxResourceBufferSize: 64 << 20, maxTotalBufferSize: 512 << 20 });
await c.send('Page.navigate', { url: 'https://www.youtube.com/watch?v=' + vid });
await sleep(+secs * 1000);
const reqs = {};
for (const e of c.ev) {
  if (e.method === 'Network.requestWillBeSent' && /videoplayback/.test(e.params.request.url) && e.params.request.method === 'POST') reqs[e.params.requestId] = { url: e.params.request.url };
  if (e.method === 'Network.loadingFinished' && reqs[e.params.requestId]) reqs[e.params.requestId].done = true;
}
let n = 0;
for (const [rid, r] of Object.entries(reqs)) {
  if (!r.done) continue;
  const pd = await c.send('Network.getRequestPostData', { requestId: rid });
  const body = await c.send('Network.getResponseBody', { requestId: rid });
  if (!pd.result || !body.result) continue;
  n++;
  const k = String(n).padStart(2, '0');
  // postData : chaine « binaire » (un caractere = un octet) ou base64 selon la version de Chrome
  const raw = pd.result.base64Encoded ? Buffer.from(pd.result.postData, 'base64') : Buffer.from(pd.result.postData, 'latin1');
  writeFileSync(join(out, `req${k}.bin`), raw);
  writeFileSync(join(out, `req${k}.url`), r.url);
  writeFileSync(join(out, `res${k}.bin`), Buffer.from(body.result.body, body.result.base64Encoded ? 'base64' : 'latin1'));
}
const pr = await c.send('Runtime.evaluate', { expression: 'JSON.stringify(document.getElementById("movie_player").getPlayerResponse())', returnByValue: true });
writeFileSync(join(out, 'player.json'), pr.result.result.value || '');
const html = await c.send('Runtime.evaluate', { expression: 'fetch(location.href,{credentials:"include"}).then(function(r){return r.text()})', awaitPromise: true, returnByValue: true });
writeFileSync(join(out, 'page.html'), html.result.result.value || '');
console.log(JSON.stringify({ exchanges: n, seen: Object.keys(reqs).length }));
await b.send('Target.closeTarget', { targetId: t.result.targetId });
process.exit(0);
