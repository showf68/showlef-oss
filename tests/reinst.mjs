// Reinstalle l extension dans le Chrome de test (port 9334) : desinstallation puis chargement (loadUnpacked seul garde l ancien service worker).
const v = await (await fetch('http://127.0.0.1:9334/json/version')).json();
const call = (method, params = {}) => new Promise(async (ok) => { const ws = new WebSocket(v.webSocketDebuggerUrl); await new Promise((o) => ws.onopen = o); ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id === 1) { ws.close(); ok(m); } }; ws.send(JSON.stringify({ id: 1, method, params })); });
await call('Extensions.uninstall', { id: 'mepcfiemlmpffknhaebjocpddbaahdfc' }); await new Promise((r) => setTimeout(r, 1500));
console.log(JSON.stringify(await call('Extensions.loadUnpacked', { path: 'C:/dev/EXTENSIONS/showlef/extension' })));
process.exit(0);
