// node tests/iso.mjs <morceau d'adresse> "<expression>"
// Evalue une expression dans le monde ISOLE de l'extension (scripts de contenu) d'un onglet du Chrome de test.
const PORT = 9334;
const [match, expr] = process.argv.slice(2);
const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const t = targets.find((x) => x.type === 'page' && (x.url.includes(match) || x.id.startsWith(match)));
if (!t) { console.error('aucun onglet pour ' + match); process.exit(1); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((ok, ko) => { ws.onopen = ok; ws.onerror = ko; });
let id = 0; const wait = {}; const ctx = [];
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } else if (m.method === 'Runtime.executionContextCreated') ctx.push(m.params.context); };
const send = (method, params = {}) => new Promise((ok) => { const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params })); });
await send('Runtime.enable');
await new Promise((r) => setTimeout(r, 500));
const iso = ctx.filter((c) => c.auxData && c.auxData.type === 'isolated' && c.auxData.frameId === t.id);
if (!iso.length) { console.log('aucun monde isole', JSON.stringify(ctx.map((c) => [c.name, c.auxData]))); process.exit(1); }
for (const c of iso) {
  const r = await send('Runtime.evaluate', { expression: expr, contextId: c.id, awaitPromise: true, returnByValue: true });
  console.log(c.name, JSON.stringify(r.result.result ? r.result.result.value : r.result));
}
process.exit(0);
