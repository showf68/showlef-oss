// node tests/guide-drop.mjs <port> <dossier extension>
// Simule, dans un Chrome NEUF (profil vierge), exactement ce que l'installeur demande a l'utilisateur quand l'extension
// n'est pas encore publiee sur le Store : ouvrir chrome://extensions, activer le mode developpeur, faire GLISSER le
// dossier de l'extension dans la page. Puis lit le profil : l'extension est-elle enregistree (persistante) ?
const [port = '9335', dir] = process.argv.slice(2);
const P = 'http://127.0.0.1:' + port;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}) => new Promise((r) => { const i = ++id; wait[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send };
}
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
const t = await b.send('Target.createTarget', { url: 'chrome://extensions/' });
await sleep(2500);
const pg = await conn((await (await fetch(P + '/json/list')).json()).find((x) => x.id === t.result.targetId).webSocketDebuggerUrl);
const ev = (e) => pg.send('Runtime.evaluate', { expression: e, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
console.log('page', await ev('location.href'));
// geste 1 : le commutateur « Mode developpeur » (clic sur le vrai element de la page)
console.log('mode dev', await ev(`(function () {
  var m = document.querySelector('extensions-manager'), tb = m && m.shadowRoot.querySelector('extensions-toolbar');
  var t = tb && tb.shadowRoot.querySelector('#devMode');
  if (!t) return 'commutateur introuvable';
  if (!t.checked) t.click();
  return 'active=' + t.checked;
})()`));
await sleep(1500);
// geste 2 : glisser le dossier dans la page
await pg.send('Page.bringToFront');
const drag = { items: [], files: [dir], dragOperationsMask: 1 };
for (const type of ['dragEnter', 'dragOver', 'drop']) {
  const r = await pg.send('Input.dispatchDragEvent', { type, x: 640, y: 400, data: drag });
  if (r.error) console.log(type, JSON.stringify(r.error));
  await sleep(400);
}
await sleep(4000);
console.log('extensions dans la page', await ev(`chrome.developerPrivate ? new Promise(function (ok) { chrome.developerPrivate.getExtensionsInfo({ includeDisabled: true }, function (l) { ok(JSON.stringify(l.filter(function (x) { return x.location === 'UNPACKED'; }).map(function (x) { return [x.id, x.name, x.version, x.state]; }))); }); }) : 'api absente'`));
process.exit(0);
