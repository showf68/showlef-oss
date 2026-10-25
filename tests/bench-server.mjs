// Serveur du banc d'essai (correctifs de la revue Codex), sans dependance :
//   node tests/bench-server.mjs <port> <dossier de medias>
//   GET /m/<fichier>[?kbs=N|hold=S]  fichier avec plages (Range) et ETag ; kbs = debit limite ; hold = reponse retenue S s
//   GET /empty/<fichier>       annonce la taille (sonde 0-0) puis repond aux autres plages par un 206 VIDE
//   GET /two.html              page avec deux videos (petite et grosse) pour le bouton « la video survolee »
//   GET /fake-tools/...        faux github : yt-dlp.exe = page HTML de 150 Ko, SHA2-256SUMS = empreinte d'autre chose
//   GET /stats[?reset]         nombre de requetes et connexions simultanees maximales par chemin
import { createServer } from 'node:http';
import { readFileSync, statSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

const [port = '8790', dir = '.'] = process.argv.slice(2);
const stats = {};
const st = (p) => (stats[p] = stats[p] || { req: 0, open: 0, maxOpen: 0 });

function serveFile(req, res, file, opts) {
  const data = readFileSync(file);
  const etag = `"${statSync(file).mtimeMs}-${data.length}"`;
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  let start = 0, end = data.length - 1;
  if (m) { start = +m[1]; if (m[2]) end = Math.min(+m[2], end); }
  let body = data.subarray(start, end + 1);
  if (opts.empty && m && !(start === 0 && end === 0)) body = Buffer.alloc(0);   // 206 vide
  res.statusCode = m ? 206 : 200;
  res.setHeader('Accept-Ranges', 'bytes');
  if (req.headers.cookie) st('cookies:' + req.url).last = req.headers.cookie;
  res.setHeader('ETag', etag);
  res.setHeader('Content-Type', file.endsWith('.mp4') ? 'video/mp4' : file.endsWith('.exe') ? 'application/octet-stream' : 'application/octet-stream');
  res.setHeader('Content-Length', body.length);
  if (/[?&]dl=1/.test(req.url)) res.setHeader('Content-Disposition', 'attachment; filename="big.mp4"');
  if (m) res.setHeader('Content-Range', `bytes ${start}-${end}/${data.length}`);
  const kbs = opts.kbs;
  if (opts.hold && m && !(start === 0 && end === 0)) {   // reponse retenue (aucun octet) : lecture reseau bloquee cote client
    const t0 = setTimeout(() => { if (!res.destroyed) res.end(body); }, opts.hold * 1000);
    req.on('close', () => clearTimeout(t0));
    return;
  }
  if (!kbs) { res.end(body); return; }
  let off = 0; const step = Math.max(1, Math.floor(kbs * 1024 / 10));
  const t = setInterval(() => {
    if (off >= body.length || res.destroyed) { clearInterval(t); res.end(); return; }
    res.write(body.subarray(off, off + step)); off += step;
  }, 100);
  req.on('close', () => clearInterval(t));
}

createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  const p = u.pathname;
  if (p === '/stats') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(stats)); if (u.searchParams.has('reset')) for (const k in stats) delete stats[k]; return; }
  const s = st(p); s.req++; s.open++; s.maxOpen = Math.max(s.maxOpen, s.open);
  res.on('close', () => { s.open--; });
  if (p === '/two.html') {
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    res.end(`<!doctype html><meta charset="utf-8"><title>Deux videos</title><body style="font-family:sans-serif">
<h3>Petite video (rouge, 8 Ko)</h3><video id="a" src="/m/small.mp4" width="420" height="236" controls muted></video>
<h3>Grosse video (mire, 15 Mo)</h3><video id="b" src="/m/big.mp4" width="640" height="360" controls muted></video></body>`);
    return;
  }
  if (p === '/fail.html' || p === '/dl.html') {
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    res.end(p === '/fail.html'
      ? '<!doctype html><meta charset="utf-8"><title>Site qui refuse</title><body><h3>Video refusee aux programmes</h3><video src="/deny/small.mp4" width="560" height="315" controls muted></video></body>'
      : '<!doctype html><meta charset="utf-8"><title>Lien direct</title><body><a id="l" href="/m/big.mp4?kbs=400&dl=1">Telecharger big.mp4</a></body>');
    return;
  }
  // /deny/ : le navigateur (en-tetes Sec-Fetch-*) recoit le fichier, un programme recoit 403
  if (p.startsWith('/deny/') && !req.headers['sec-fetch-dest']) { res.statusCode = 403; res.end('refuse'); return; }
  if (p.startsWith('/fake-tools/')) {
    const html = Buffer.from('<!doctype html><html><body>' + 'filtre '.repeat(22000) + '</body></html>');
    if (p.endsWith('/SHA2-256SUMS')) { res.end(createHash('sha256').update('autre chose').digest('hex') + '  yt-dlp.exe\n'); return; }
    if (p.endsWith('/yt-dlp.exe')) { res.setHeader('Content-Type', 'text/html'); res.end(html); return; }
    res.statusCode = 404; res.end(); return;
  }
  const empty = p.startsWith('/empty/'), deny = p.startsWith('/deny/');
  if (p.startsWith('/m/') || empty || deny) {
    const f = join(dir, decodeURIComponent(p.slice(empty ? 7 : deny ? 6 : 3)));
    if (!existsSync(f)) { res.statusCode = 404; res.end(); return; }
    serveFile(req, res, f, { kbs: +u.searchParams.get('kbs') || 0, hold: +u.searchParams.get('hold') || 0, empty });
    return;
  }
  res.statusCode = 404; res.end();
}).listen(+port, '127.0.0.1', () => console.log('pret ' + port));
