// Chrome de test (port 9334, profil C:\dev-tools\chrome-showlef-test) avec l'extension chargee.
// Chrome 155 ignore --load-extension : chargement par l'appel de debogage Extensions.loadUnpacked.
//   node tests/chrome-test.mjs start | reload | open <url>
import { spawn } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const PORT = 9334, EXT = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'extension');
const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const [cmd, arg] = process.argv.slice(2);
const up = async () => { try { return await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); } catch { return null; } };
async function browser(method, params = {}) {
  const v = await up();
  const ws = new WebSocket(v.webSocketDebuggerUrl);
  await new Promise((ok, ko) => { ws.onopen = ok; ws.onerror = ko; });
  const r = await new Promise((ok) => { ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id === 1) ok(m); }; ws.send(JSON.stringify({ id: 1, method, params })); });
  ws.close();
  return r;
}
if (cmd === 'start') {
  if (!(await up())) {
    // lance par PowerShell : un spawn detache depuis Git Bash ne demarre pas Chrome
    const args = [`--remote-debugging-port=${PORT}`, '--user-data-dir=C:/dev-tools/chrome-showlef-test', '--enable-unsafe-extension-debugging',
      '--no-first-run', '--no-default-browser-check', '--autoplay-policy=no-user-gesture-required', '--window-size=1280,900', ...(process.env.OFFSCREEN ? ['--window-position=-32000,-32000'] : []), 'about:blank'];
    spawn('powershell', ['-NoProfile', '-Command', "Start-Process '" + CHROME + "' -ArgumentList '" + args.join("','") + "'"], { stdio: 'ignore' });
    for (let i = 0; i < 120 && !(await up()); i++) await new Promise((r) => setTimeout(r, 250));
  }
  console.log(JSON.stringify(await browser('Extensions.loadUnpacked', { path: EXT })));
} else if (cmd === 'reload') {
  console.log(JSON.stringify(await browser('Extensions.loadUnpacked', { path: EXT })));
} else if (cmd === 'load') {   // autre copie de l'extension (ancienne version a comparer) : meme identifiant (cle)
  console.log(JSON.stringify(await browser('Extensions.loadUnpacked', { path: resolve(arg) })));
} else if (cmd === 'open') {
  console.log(JSON.stringify(await browser('Target.createTarget', { url: arg })));
}
