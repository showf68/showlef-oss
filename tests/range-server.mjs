// Serveur de test avec plages (Range) et ETag, pour éprouver la reprise et le contrôle « fichier modifié ».
//   node tests/range-server.mjs <port> <fichier> [debit_ko_s]
//   GET /f      -> le fichier, avec ETag = taille + date de modification
//   GET /swap   -> remplace le contenu par une autre version (même taille, autre ETag)
import { createServer } from 'node:http';
import { readFileSync, statSync } from 'node:fs';
import { randomBytes } from 'node:crypto';

const [port = '8767', file, rate = '0'] = process.argv.slice(2);
let data = readFileSync(file), version = 1;
const etag = () => `"v${version}-${data.length}"`;

createServer((req, res) => {
  if (req.url === '/swap') { data = randomBytes(data.length); version++; res.end('swapped ' + etag()); return; }
  if (!req.url.startsWith('/f')) { res.statusCode = 404; res.end(); return; }
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  let start = 0, end = data.length - 1;
  if (m) { start = +m[1]; if (m[2]) end = Math.min(+m[2], end); }
  const body = data.subarray(start, end + 1);
  res.statusCode = m ? 206 : 200;
  res.setHeader('Accept-Ranges', 'bytes');
  res.setHeader('ETag', etag());
  res.setHeader('Content-Type', 'application/octet-stream');
  res.setHeader('Content-Length', body.length);
  if (m) res.setHeader('Content-Range', `bytes ${start}-${end}/${data.length}`);
  const kbs = req.url.includes('fast') ? 0 : +rate;   // ?fast : sans limite (verification)
  if (!kbs) { res.end(body); return; }
  // débit limité : laisse le temps de mettre en pause au milieu
  let off = 0; const step = kbs * 1024 / 10;
  const t = setInterval(() => {
    if (off >= body.length || res.destroyed) { clearInterval(t); res.end(); return; }
    res.write(body.subarray(off, off + step)); off += step;
  }, 100);
  req.on('close', () => clearInterval(t));
}).listen(+port, '127.0.0.1', () => console.log('pret ' + port + ' ' + etag()));
