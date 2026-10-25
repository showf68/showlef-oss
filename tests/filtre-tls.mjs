// node tests/filtre-tls.mjs <dossier avec ca.crt + ca.key> [port=8899]
// Simulation d'un filtre qui INSPECTE le HTTPS (NetFree, Techloq...) : proxy CONNECT qui termine chaque connexion TLS
// avec un certificat fabrique pour l'hote demande, signe par SA propre autorite, puis relaie vers le vrai serveur.
// Un client qui ne connait pas cette autorite echoue en CERTIFICATE_VERIFY_FAILED, exactement comme yt-dlp chez un
// utilisateur de NetFree (journal de Motti, 24/10/2026). On y fait passer yt-dlp par HTTPS_PROXY.
// Creer l'autorite (une fois) :
//   MSYS2_ARG_CONV_EXCL='*' openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 7 \
//     -subj "/CN=ShowLef Bench Filtre" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
// Rien n'est installe dans Windows : l'autorite reste dans ce dossier.
import http from 'node:http';
import tls from 'node:tls';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const dir = process.argv[2];
const port = +(process.argv[3] || 8899);
if (!dir || !fs.existsSync(path.join(dir, 'ca.crt'))) { console.error('usage : node tests/filtre-tls.mjs <dossier ca> [port]'); process.exit(2); }
const openssl = process.env.OPENSSL || 'C:/Program Files/Git/mingw64/bin/openssl.exe';
const env = { ...process.env, MSYS2_ARG_CONV_EXCL: '*' };
const ctxs = new Map();
let n = 0;

function ctxFor(host) {
  if (ctxs.has(host)) return ctxs.get(host);
  const safe = host.replace(/[^a-z0-9.-]/gi, '_');
  const key = path.join(dir, safe + '.key'), csr = path.join(dir, safe + '.csr'), crt = path.join(dir, safe + '.crt'), ext = path.join(dir, safe + '.ext');
  if (!fs.existsSync(crt)) {
    fs.writeFileSync(ext, 'subjectAltName=DNS:' + host + '\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\n');
    execFileSync(openssl, ['req', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', csr, '-subj', '/CN=' + host], { env, stdio: 'ignore' });
    execFileSync(openssl, ['x509', '-req', '-in', csr, '-CA', path.join(dir, 'ca.crt'), '-CAkey', path.join(dir, 'ca.key'),
      '-CAcreateserial', '-out', crt, '-days', '3', '-extfile', ext], { env, stdio: 'ignore' });
  }
  const c = tls.createSecureContext({ key: fs.readFileSync(key), cert: fs.readFileSync(crt) });
  ctxs.set(host, c);
  return c;
}

const srv = http.createServer((req, res) => { res.writeHead(405); res.end('CONNECT seulement'); });
srv.on('connect', (req, client, head) => {
  const [host, p] = req.url.split(':');
  const id = ++n;
  let ctx;
  try { ctx = ctxFor(host); } catch (e) { client.end('HTTP/1.1 502 Bad Gateway\r\n\r\n'); return; }
  client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
  const front = new tls.TLSSocket(client, { isServer: true, secureContext: ctx });
  const back = tls.connect({ host, port: +(p || 443), servername: host });
  if (head && head.length) front.unshift(head);
  front.pipe(back); back.pipe(front);
  const close = () => { front.destroy(); back.destroy(); };
  front.on('error', (e) => { console.log('#' + id + ' ' + host + ' client : ' + (e.code || e.message)); close(); });
  back.on('error', (e) => { console.log('#' + id + ' ' + host + ' serveur : ' + (e.code || e.message)); close(); });
  front.on('secure', () => console.log('#' + id + ' ' + host + ' : TLS intercepte'));
});
srv.listen(port, '127.0.0.1', () => console.log('filtre TLS simule sur 127.0.0.1:' + port + ' (autorite ' + path.join(dir, 'ca.crt') + ')'));
