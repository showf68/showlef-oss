// node resume.mjs <videoId> <quality> <audio 0|1> <couperApresSec>
// 1) lance un telechargement, 2) recharge la page au bout de N s (coupure), 3) verifie « Reprendre » dans l'etat de
// l'onglet, 4) relance : mesure ce qui est re-telecharge et la fin
const [vid, quality, audio, cutSec] = process.argv.slice(2);
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
const old = (await (await fetch(P + '/json/list')).json()).filter((t) => t.type === 'page' && (t.url.includes('youtube.com') || t.url === 'about:blank'));
const yt = await b.send('Target.createTarget', { url: 'https://www.youtube.com/watch?v=' + vid });
for (const t of old) await b.send('Target.closeTarget', { targetId: t.id });
await sleep(1500);
const list = await (await fetch(P + '/json/list')).json();
const page = await conn(list.find((x) => x.id === yt.result.targetId).webSocketDebuggerUrl);
const sw = await conn(list.find((x) => x.type === 'service_worker' && x.url.includes('background.js')).webSocketDebuggerUrl);
await page.send('Emulation.setFocusEmulationEnabled', { enabled: true });
await page.send('Page.bringToFront');
const ready = async () => {
  for (let i = 0; i < 40; i++) { await sleep(500); if ((await ev(page, 'performance.getEntriesByType("resource").filter(function(e){return /videoplayback/.test(e.name)}).length')) > 0) break; }
  await sleep(3000);
};
const tabId = async () => ev(sw, `chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (t) { return t[0].id; })`);
const go = async () => ev(sw, `ytDownload(${await tabId()}, '${quality}', ${audio === '1'}).then(function (r) { return JSON.stringify(r); })`, 60000);
const stat = async () => { try { return JSON.parse(await ev(page, 'JSON.stringify(window.__showlefStat && window.__showlefStat())', 15000)); } catch (e) { return null; } };

await ready();
console.log('1er', await go());
const t0 = Date.now();
let s = null;
while (Date.now() - t0 < +cutSec * 1000) { await sleep(5000); s = await stat(); if (s) console.log(((Date.now() - t0) / 1000).toFixed(0) + 's net=' + (s.net / 1048576).toFixed(1) + 'Mo ack=' + (s.ackB / 1048576).toFixed(1) + 'Mo fin=' + s.end); if (s && s.end) break; }
console.log('COUPURE (rechargement de la page)');
await page.send('Page.reload', {});
await ready();
await sleep(4000);
const st = await ev(sw, `chrome.storage.session.get('tab_${await tabId()}').then(function (o) { var t = o['tab_${await tabId()}']; var it = t && t.items.find(function (i) { return i.kind === 'yt'; }); return JSON.stringify(it && it.resume); })`);
console.log('etat onglet resume =', st);
console.log('2e', await go());
const t1 = Date.now();
while (Date.now() - t1 < 600000) {
  await sleep(5000); s = await stat();
  if (s) console.log(((Date.now() - t1) / 1000).toFixed(0) + 's net=' + (s.net / 1048576).toFixed(1) + 'Mo ack=' + (s.ackB / 1048576).toFixed(1) + 'Mo req=' + s.req + ' fin=' + s.end);
  if (s && s.end) break;
}
console.log('tours', JSON.stringify(s && s.rounds)); console.log('RESULTAT reprise', s && s.end, 'duree=' + (s ? ((s.t1 - s.t0) / 1000).toFixed(1) : '?') + 's', 'net=' + (s ? (s.net / 1048576).toFixed(1) : '?') + 'Mo', 'livre=' + (s ? (s.ackB / 1048576).toFixed(1) : '?') + 'Mo');
process.exit(0);
