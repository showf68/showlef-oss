// node tests/guide-real.mjs <port> load|list|devoff
// Parcours reel de l'utilisateur guide par l'installeur 0.7.17, dans un Chrome a profil vierge :
//  load  : ouvre chrome://extensions, active « Mode developpeur » (clic sur le vrai commutateur), clique « Charger
//          l'extension non empaquetee » (ouvre la fenetre Windows de choix de dossier, remplie par guide-load.ps1)
//  list  : extensions non empaquetees connues du profil (id, nom, version, etat, dossier)
const [port = '9336', step = 'list'] = process.argv.slice(2);
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
let tgt = (await (await fetch(P + '/json/list')).json()).find((x) => x.url.startsWith('chrome://extensions'));
if (!tgt) {
  const t = await b.send('Target.createTarget', { url: 'chrome://extensions/' });
  await sleep(2500);
  tgt = (await (await fetch(P + '/json/list')).json()).find((x) => x.id === t.result.targetId);
}
const pg = await conn(tgt.webSocketDebuggerUrl);
const ev = (e) => pg.send('Runtime.evaluate', { expression: e, awaitPromise: true, returnByValue: true }).then((r) => r.result.result && r.result.result.value);
const TB = `document.querySelector('extensions-manager').shadowRoot.querySelector('extensions-toolbar').shadowRoot`;
if (step === 'load') {
  await pg.send('Page.bringToFront');
  console.log('mode dev', await ev(`(function () { var t = ${TB}.querySelector('#devMode'); if (!t.checked) t.click(); return 'active=' + t.checked; })()`));
  await sleep(1500);
  // le bouton apparait avec le mode developpeur ; on ne l'attend pas (la fenetre de dossier est modale)
  console.log('bouton', await ev(`(function () { var x = ${TB}.querySelector('#loadUnpacked'); if (!x) return 'introuvable'; setTimeout(function () { x.click(); }, 50); return 'clic'; })()`));
} else {
  console.log(await ev(`new Promise(function (ok) { chrome.developerPrivate.getExtensionsInfo({ includeDisabled: true, includeTerminated: true }, function (l) {
    ok(JSON.stringify(l.filter(function (x) { return x.location === 'UNPACKED'; }).map(function (x) { return { id: x.id, name: x.name, version: x.version, state: x.state, path: x.prettifiedPath, errors: (x.runtimeErrors || []).length + (x.manifestErrors || []).length }; })));
  }); })`));
  console.log('mode dev', await ev(`${TB}.querySelector('#devMode').checked`));
}
process.exit(0);
