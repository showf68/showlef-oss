// node run2x2.mjs <videoId> <quality> <visible|hidden> <fast 0|1> <inflight> <par> [maxSec]
// lance un telechargement YouTube direct dans le Chrome de test et journalise toutes les 5 s :
// reseau (Mo recus), envois au programme (Mo confirmes), file, latence du service worker
const [vid, quality, mode, fast, inflight, par, maxSec = '600'] = process.argv.slice(2);
const P = 'http://127.0.0.1:9334';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}, to = 20000) => new Promise((ok) => {
    const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params }));
    setTimeout(() => { if (wait[i]) { delete wait[i]; ok({ timeout: true }); } }, to);
  });
  return { ws, send };
}
const ev = (c, expr, to) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }, to)
  .then((r) => r.timeout ? 'TIMEOUT' : r.result.exceptionDetails ? 'EXC ' + JSON.stringify(r.result.exceptionDetails).slice(0, 300) : r.result.result.value);

const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
// onglets YouTube et blancs d'un essai precedent : fermes
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && (t.url.includes('youtube.com') || t.url === 'about:blank'));
const yt = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
for (const t of old) await b.send('Target.closeTarget', { targetId: t.id });   // apres l'ouverture : fermer le dernier onglet quitterait Chrome
await sleep(1500);
const list = await (await fetch(P + '/json/list')).json();
const page = await conn(list.find((x) => x.id === yt.result.targetId).webSocketDebuggerUrl);
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
if (mode !== 'hidden') await page.send('Emulation.setFocusEmulationEnabled', { enabled: true });
await page.send('Page.bringToFront');
// le lecteur doit avoir fait sa requete SABR (modele du mode direct)
for (let i = 0; i < 40; i++) { await sleep(500); if ((await ev(page, 'performance.getEntriesByType("resource").filter(function(e){return /videoplayback/.test(e.name)}).length')) > 0) break; }
await sleep(3000);
console.log('tune', await ev(page, `window.__showlefTune = ${fast === 'd' ? '{}' : `{ fast: ${fast === '1'}, inflight: ${+inflight}, par: ${+par} }`}; JSON.stringify(window.__showlefTune)`));
let blank = null;
if (mode === 'hidden') {   // onglet YouTube en arriere-plan : un autre onglet devant
  blank = await b.send('Target.createTarget', { url: 'about:blank' });
  await sleep(1000);
  const bl = (await (await fetch(P + '/json/list')).json()).find((x) => x.id === blank.result.targetId);
  const bc = await conn(bl.webSocketDebuggerUrl);
  await bc.send('Page.bringToFront');
  await sleep(1000);
  if (mode === 'hidden') {   // fenetre de l'onglet YouTube reduite : seule facon fiable d'avoir document.hidden
    const w = await b.send('Browser.getWindowForTarget', { targetId: yt.result.targetId });
    await b.send('Browser.setWindowBounds', { windowId: w.result.windowId, bounds: { windowState: 'minimized' } });
    await sleep(1500);
  }
} else await page.send('Page.setWebLifecycleState', { state: 'active' });
console.log('vis', await ev(page, 'document.visibilityState'));
const tabId = await ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
const t0 = Date.now();
console.log('start', await ev(sw, `ytDownload(${tabId}, '${quality}', false).then(function (r) { return JSON.stringify(r); })`, 60000));
let last = null, lastLine = '';
while (Date.now() - t0 < +maxSec * 1000) {
  await sleep(5000);
  const ts = Date.now();
  const swr = await ev(sw, '1', 15000);
  const swMs = Date.now() - ts;
  const st = await ev(page, 'JSON.stringify(window.__showlefStat && window.__showlefStat())', 15000);
  let s = null; try { s = JSON.parse(st); } catch (e) { }
  const sec = ((Date.now() - t0) / 1000).toFixed(0);
  if (!s) { console.log(sec + 's stat=' + String(st).slice(0, 80) + ' sw=' + (swr === 'TIMEOUT' ? 'TIMEOUT' : swMs + 'ms')); continue; }
  const dNet = last ? (s.net - last.net) / 5 / 1048576 : 0, dAck = last ? (s.ackB - last.ackB) / 5 / 1048576 : 0;
  lastLine = `${sec}s net=${(s.net / 1048576).toFixed(1)}Mo (+${dNet.toFixed(2)}/s) ack=${(s.ackB / 1048576).toFixed(1)}Mo (+${dAck.toFixed(2)}/s) req=${s.req} moyReq=${s.req ? (s.reqMs / s.req).toFixed(0) : 0}ms maxReq=${s.reqMax} envoi=${s.sent}/${s.acked} moyAck=${s.acked ? (s.ackMs / s.acked).toFixed(0) : 0}ms maxAck=${s.ackMax} qMax=${(s.qMax / 1048576).toFixed(1)}Mo sw=${swr === 'TIMEOUT' ? 'TIMEOUT' : swMs + 'ms'} fin=${s.end}`;
  console.log(lastLine);
  last = s;
  if (s.end) break;
}
if (last && last.end) console.log('RESULTAT', mode, 'fast=' + fast, 'inflight=' + inflight, 'par=' + par, 'duree=' + ((last.t1 - last.t0) / 1000).toFixed(1) + 's',
  'net=' + (last.net / 1048576).toFixed(1) + 'Mo', 'debit=' + (last.net / 1048576 / ((last.t1 - last.t0) / 1000)).toFixed(2) + 'Mo/s', last.end);
else console.log('RESULTAT', mode, 'fast=' + fast, 'PAS FINI en ' + maxSec + 's', lastLine);
process.exit(0);
