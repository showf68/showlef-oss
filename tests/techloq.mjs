// node tests/techloq.mjs <video autorisee> <video bloquee par Techloq>
// ShowLef peut-il contourner Techloq ? Essais, sur une machine derriere Techloq :
//  A. ouvrir la video bloquee directement ;
//  B. depuis une video autorisee, navigation INTERNE de YouTube vers la video bloquee (sans recharger la page) ;
//  C. adresse de la page changee a la main (history.pushState) puis demande de telechargement ;
//  D. le programme seul (yt-dlp) sur la video bloquee.
// Attendu : rien de la video bloquee n'est annonce ni telecharge ; la video autorisee reste telechargeable.
import { execFileSync } from 'node:child_process';
const [ok, bad] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {}; const ev = [];
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } else if (m.method) ev.push(m); };
  const send = (method, params = {}) => new Promise((r) => { const i = ++id; wait[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send, ev };
}
const ev = (c, expr) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const sw = await conn((await (await fetch(P + '/json/list')).json()).find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
async function tabOpen(url) {
  const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && (t.url.includes('youtube') || t.url.includes('techloq')));
  const t = await b.send('Target.createTarget', { url });
  for (const o of old) await b.send('Target.closeTarget', { targetId: o.id });
  await sleep(6000);
  const pg = (await (await fetch(P + '/json/list')).json()).find((x) => x.id === t.result.targetId);
  return conn(pg.webSocketDebuggerUrl);
}
const tabItems = () => ev(sw, `chrome.tabs.query({}).then(function (ts) { return Promise.all(ts.map(function (t) { return readTab(t.id).then(function (st) { return st.items.filter(function (i) { return i.kind === 'site'; }).map(function (i) { return i.key + ' ' + i.title; }); }); })); }).then(function (a) { return JSON.stringify([].concat.apply([], a)); })`);
const list = () => ev(sw, `nativeSend({ cmd: 'list' }).then(function (r) { return JSON.stringify(r.items.map(function (i) { return i.id; })); })`).then(JSON.parse);
const verdict = [];
const before = await list();

// A
let p = await tabOpen('https://www.youtube.com/watch?v=' + bad);
console.log('A. page ouverte :', (await ev(p, 'location.href')).slice(0, 80));
console.log('   medias annonces :', await tabItems());
verdict.push(!(await tabItems()).includes(bad));

// B + C depuis une video autorisee
p = await tabOpen('https://www.youtube.com/watch?v=' + ok);
await p.send('Network.enable');
console.log('B. video autorisee annoncee :', await tabItems());
console.log('   navigation interne vers la video bloquee :', await ev(p, `(function () {
  var app = document.querySelector('ytd-app');
  app.dispatchEvent(new CustomEvent('yt-navigate', { bubbles: true, composed: true, detail: { endpoint: { commandMetadata: { webCommandMetadata: { url: '/watch?v=${bad}', webPageType: 'WEB_PAGE_TYPE_WATCH', rootVe: 3832 } }, watchEndpoint: { videoId: '${bad}' } } } }));
  return 'envoyee';
})()`));
await sleep(9000);
const st = await ev(p, `(function () { var pl = document.getElementById('movie_player'); var r = pl && pl.getPlayerResponse && pl.getPlayerResponse(); var v = document.querySelector('video');
  return JSON.stringify({ href: location.href, lecteur: r && r.videoDetails ? r.videoDetails.videoId : null, statut: r && r.playabilityStatus ? r.playabilityStatus.status : null, t: v ? v.currentTime : null }); })()`);
console.log('   etat apres navigation :', st);
const blockedNet = p.ev.filter((e) => e.method === 'Network.responseReceived' && /youtubei\/v1\/player|videoplayback/.test(e.params.response.url)).map((e) => e.params.response.status + ' ' + e.params.response.url.slice(0, 60));
console.log('   reponses lecteur/medias pendant la navigation :', blockedNet.slice(-4).join(' | ') || 'aucune');
console.log('   medias annonces :', await tabItems());
const tabId = await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/*' }).then(function (t) { return t[0].id; })`);
console.log('   telechargement demande :', await ev(sw, `siteDownload(${tabId}, '360', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
await sleep(15000);
// C : adresse changee a la main
console.log('C. pushState vers la video bloquee :', await ev(p, `history.pushState({}, '', '/watch?v=${bad}'); location.href`));
await sleep(6000);
console.log('   medias annonces :', await tabItems());
console.log('   telechargement demande :', await ev(sw, `siteDownload(${tabId}, '360', false, 0).then(function (r) { track(${tabId}, 0, r); return JSON.stringify(r); })`));
await sleep(12000);
// D : le programme seul
console.log('D. yt-dlp seul :', execFileSync('node', ['tests/host.mjs', JSON.stringify({ cmd: 'ytprobe', url: 'https://www.youtube.com/watch?v=' + bad })]).toString().trim());
const after = await list();
const fresh = after.filter((i) => !before.includes(i));
const d = JSON.parse(execFileSync('node', ['tests/host.mjs', '{"cmd":"list"}']).toString()).items.filter((i) => fresh.includes(i.id));
console.log('nouveaux elements dans le programme :', d.map((i) => i.status + ' ' + i.name + (i.error ? ' | ' + i.error : '')).join(' || ') || 'aucun');
const leaked = d.filter((i) => i.status === 'done' || i.status === 'downloading' || i.status === 'merging');
console.log('RESULTAT', verdict.every(Boolean) && leaked.length === 0 ? 'AUCUN CONTOURNEMENT' : 'A EXAMINER : ' + leaked.map((i) => i.name).join(', '));
process.exit(0);
