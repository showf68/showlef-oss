// Petit client CDP pour le Chrome de test (port 9334) :
//   node tests/cdp.mjs list
//   node tests/cdp.mjs console <morceau d'adresse> [ms]   -> recharge l'onglet et affiche erreurs/avertissements
//   node tests/cdp.mjs eval <morceau d'adresse> "<expression>"
//   node tests/cdp.mjs shot <morceau d'adresse> <fichier.png>
//   node tests/cdp.mjs nav <morceau d'adresse> <nouvelle adresse>
const PORT = 9334;
const [cmd, match, arg, arg2] = process.argv.slice(2);
const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
if (cmd === 'list') { for (const t of targets) console.log(t.type.padEnd(16), t.id, t.url.slice(0, 140)); process.exit(0); }
const t = targets.find((x) => (x.type === 'page' || x.type === 'service_worker') && x.url.includes(match));
if (!t) { console.error('aucun onglet pour ' + match); process.exit(1); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((ok, ko) => { ws.onopen = ok; ws.onerror = ko; });
let id = 0; const wait = {}; const events = [];
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } else if (m.method) events.push(m); };
const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
if (cmd === 'console') {
  await send('Runtime.enable'); await send('Log.enable');
  await send('Page.reload', { ignoreCache: false });
  await new Promise((r) => setTimeout(r, +arg || 8000));
  for (const ev of events) {
    if (ev.method === 'Log.entryAdded') console.log('[log]', ev.params.entry.level, ev.params.entry.source, ev.params.entry.text.slice(0, 300));
    if (ev.method === 'Runtime.exceptionThrown') console.log('[exc]', JSON.stringify(ev.params.exceptionDetails).slice(0, 300));
    if (ev.method === 'Runtime.consoleAPICalled' && /error|warn/.test(ev.params.type)) console.log('[con]', ev.params.type, ev.params.args.map((a) => a.value || a.description).join(' ').slice(0, 300));
  }
} else if (cmd === 'focus') {   // page « visible » meme fenetre cachee ou hors ecran (CDP)
  await send('Emulation.setFocusEmulationEnabled', { enabled: true }); await send('Page.bringToFront');
  console.log('ok focus');
  await new Promise((r) => setTimeout(r, +arg || 60000));   // reste attache tant que l'emulation doit durer
} else if (cmd === 'eval') {
  const r = await send('Runtime.evaluate', { expression: arg, awaitPromise: true, returnByValue: true });
  console.log(JSON.stringify(r.result.result ? r.result.result.value : r.result, null, 1));
} else if (cmd === 'shot') {
  await send('Page.bringToFront');
  const r = await send('Page.captureScreenshot', { format: 'png' });
  (await import('node:fs')).writeFileSync(arg, Buffer.from(r.result.data, 'base64'));
  console.log('ok ' + arg);
} else if (cmd === 'watch') {
  // ecoute la console pendant <ms> sans recharger, en promenant la souris au centre de la page
  await send('Runtime.enable'); await send('Log.enable');
  const end = Date.now() + (+arg || 6000);
  for (let k = 0; Date.now() < end; k++) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 300 + (k % 10) * 20, y: 300 });
    await new Promise((r) => setTimeout(r, 300));
  }
  for (const ev of events) {
    if (ev.method === 'Log.entryAdded') console.log('[log]', ev.params.entry.level, ev.params.entry.text.slice(0, 220));
    if (ev.method === 'Runtime.exceptionThrown') console.log('[exc]', JSON.stringify(ev.params.exceptionDetails).slice(0, 300));
  }
} else if (cmd === 'move' || cmd === 'click') {   // souris reelle (Input) : x y
  const x = +arg, y = +arg2;
  await send('Page.bringToFront');
  for (let k = 0; k < 4; k++) { await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x - 20 + k * 6, y }); await new Promise((r) => setTimeout(r, 120)); }
  await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
  if (cmd === 'click') {
    await new Promise((r) => setTimeout(r, 300));
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
  }
  console.log('ok ' + cmd + ' ' + x + ',' + y);
} else if (cmd === 'hover') {   // souris SIMULEE (evenement DOM) : marche meme ecran eteint, ou Input ne repond plus
  await send('Runtime.evaluate', { expression: `document.dispatchEvent(new MouseEvent('mousemove', { clientX: ${+arg}, clientY: ${+arg2}, bubbles: true }))` });
  console.log('ok hover');
} else if (cmd === 'press') {   // clic sur un element du shadow DOM FERME du bouton (DOM.getDocument pierce) : selecteur CSS
  const doc = await send('DOM.getDocument', { depth: -1, pierce: true });
  const found = [];
  (function walk(n) {
    if (!n) return;
    if (n.nodeName === 'SHOWLEF-UI' && n.shadowRoots) found.push(n.shadowRoots[0]);
    (n.children || []).forEach(walk); (n.shadowRoots || []).forEach(walk);
  })(doc.result.root);
  if (!found.length) { console.log('pas de showlef-ui'); process.exit(1); }
  const q = await send('DOM.querySelectorAll', { nodeId: found[0].nodeId, selector: arg });
  const ids = (q.result && q.result.nodeIds) || [];
  if (!ids.length) { console.log('rien pour ' + arg); process.exit(1); }
  const o = await send('DOM.resolveNode', { nodeId: ids[+arg2 || 0] });
  await send('Runtime.callFunctionOn', { objectId: o.result.object.objectId, functionDeclaration: 'function () { this.click(); }' });
  console.log('ok press ' + arg);
} else if (cmd === 'nav') {
  await send('Page.navigate', { url: arg });
  console.log('ok');
}
ws.close();
