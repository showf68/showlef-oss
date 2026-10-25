// Banc avant / apres des correctifs (revue Codex). Lance UNE instance du programme compile dans <bin>, avec ses
// donnees a part (--dir), rejoue un scenario et affiche la mesure. Le ShowLef installe doit etre arrete (une seule
// instance par utilisateur).
//   node tests/bench.mjs <bin> <data> <scenario> [port]
// Serveur : node tests/bench-server.mjs <port> <medias>  (small.mp4, big.mp4, long.mp4, tool.exe)
import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

const [bin, data, scen, port = '8790'] = process.argv.slice(2);
const SRV = `http://127.0.0.1:${port}`;
const out = join(data, 'dl');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function host(msg) {
  const exe = join(bin, 'showlef-host.exe');
  const b = Buffer.from(JSON.stringify(msg), 'utf8'); const len = Buffer.alloc(4); len.writeUInt32LE(b.length);
  const r = execFileSync(exe, ['chrome-extension://mepcfiemlmpffknhaebjocpddbaahdfc/'], { input: Buffer.concat([len, b]), timeout: 60000 });
  return JSON.parse(r.subarray(4, 4 + r.readUInt32LE(0)).toString('utf8'));
}
const list = () => host({ cmd: 'list' }).items;
const item = (id) => list().find((i) => i.id === id);
async function until(id, pred, ms = 60000) {
  const t = Date.now();
  while (Date.now() - t < ms) { const i = item(id); if (i && pred(i)) return i; await sleep(300); }
  return item(id);
}
const stats = async (reset) => (await fetch(SRV + '/stats' + (reset ? '?reset' : ''))).json();
const md5 = (f) => createHash('md5').update(readFileSync(f)).digest('hex');
const ffprobe = (f) => execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_type,codec_name', '-of', 'csv=p=0', f]).toString().trim().replace(/\r?\n/g, ' | ');

async function start(settings = {}, extraArgs = []) {
  rmSync(data, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  writeFileSync(join(data, 'settings.json'), JSON.stringify(Object.assign({ Folder: out, SortByType: false, Connections: 8, MaxParallel: 3,
    Retries: 10, Quality: '720', AudioFormat: 'mp3', Notify: false, CloseToTray: true, SiteFolders: {} }, settings)));
  const p = spawn(join(bin, 'ShowLef.exe'), ['--dir', data, '--lang', 'fr', '--tray', ...extraArgs], { detached: true, stdio: 'ignore' });
  p.unref();
  for (let i = 0; i < 60; i++) { await sleep(250); try { if (host({ cmd: 'ping' }).ok) return; } catch { } }
  throw new Error('programme muet');
}
function stop() { try { execFileSync('taskkill', ['/F', '/IM', 'ShowLef.exe'], { stdio: 'ignore' }); } catch { } }
const files = () => readdirSync(out).filter((f) => !f.endsWith('.kpart'));

const S = {
  // n° 6 : pause puis reprise rapides pendant qu'une reponse est retenue -> combien de connexions a la fois ?
  async pause() {
    await start({ Connections: 4 });
    await stats(true);
    const { id } = host({ cmd: 'add', url: SRV + '/m/long.mp4?hold=6', kind: 'file' });
    await sleep(1500);
    host({ cmd: 'control', id, action: 'pause' });
    host({ cmd: 'control', id, action: 'resume' });
    await sleep(2500);
    const s = await stats();
    const i = await until(id, (x) => x.status === 'done' || x.status === 'error', 60000);
    const f = join(out, i.name);
    console.log('connexions simultanees max :', s['/m/long.mp4'].maxOpen, '(reglage : 4) | requetes :', s['/m/long.mp4'].req,
      '| statut :', i.status, '| empreinte :', existsSync(f) && md5(f) === md5(join(SRV_DIR(), 'long.mp4')) ? 'IDENTIQUE' : 'DIFFERENTE');
  },
  // n° 7 : conversion MP3 tuee en cours, puis reprise -> que contient le « .mp3 » ?
  async mp3() {
    await start();
    const { id } = host({ cmd: 'add', url: SRV + '/m/long.mp4', kind: 'file', audioOnly: true, audioFormat: 'mp3' });
    await until(id, (x) => x.status === 'merging', 60000);
    await sleep(800);
    try { execFileSync('taskkill', ['/F', '/IM', 'ffmpeg.exe'], { stdio: 'ignore' }); } catch { }
    let i = await until(id, (x) => x.status === 'error' || x.status === 'done', 30000);
    console.log('apres arret de ffmpeg :', i.status, i.name, '| fichiers :', files().join(', '));
    host({ cmd: 'control', id, action: 'resume' });
    i = await until(id, (x) => x.status === 'done' || x.status === 'error', 120000);
    console.log('apres reprise :', i.status, i.name, '| pistes :', ffprobe(join(out, i.name)), '| fichiers :', files().join(', '));
  },
  // n° 8 : un fichier temoin apparait au meme nom pendant le telechargement -> survit-il ?
  async overwrite() {
    await start();
    const { id } = host({ cmd: 'add', url: SRV + '/m/big.mp4?kbs=200', kind: 'file', filename: 'temoin.mp4' });
    await sleep(1500);
    writeFileSync(join(out, 'temoin.mp4'), 'TEMOIN');
    const i = await until(id, (x) => x.status === 'done' || x.status === 'error', 60000);
    const t = existsSync(join(out, 'temoin.mp4')) ? readFileSync(join(out, 'temoin.mp4')).subarray(0, 6).toString() : '(absent)';
    console.log('statut :', i.status, i.name, '| temoin.mp4 commence par :', JSON.stringify(t), '| fichiers :', files().join(', '));
  },
  // n° 14 : le serveur repond aux plages par un 206 vide -> combien de requetes en 20 s ?
  async empty() {
    await start({ Retries: 3, Connections: 2 });
    await stats(true);
    const { id } = host({ cmd: 'add', url: SRV + '/empty/big.mp4', kind: 'file' });
    await sleep(20000);
    const s = await stats(); const i = item(id);
    console.log('requetes en 20 s :', s['/empty/big.mp4'].req, '| statut :', i.status, '| erreur :', i.error, '| code :', i.code);
  },
  // n° 16 : MP3 puis M4A de la meme source
  async dup() {
    await start();
    const a = host({ cmd: 'add', url: SRV + '/m/big.mp4', kind: 'file', audioOnly: true, audioFormat: 'mp3' });
    await until(a.id, (x) => x.status === 'done', 60000);
    const b = host({ cmd: 'add', url: SRV + '/m/big.mp4', kind: 'file', audioOnly: true, audioFormat: 'm4a' });
    if (!b.ok) { console.log('M4A refuse :', b.error, '| fichiers :', files().join(', ')); return; }
    await until(b.id, (x) => x.status === 'done', 60000);
    console.log('M4A accepte | fichiers :', files().join(', '));
  },
  // n° 4 : provenance Internet d'un .exe
  async zone() {
    await start();
    const { id } = host({ cmd: 'add', url: SRV + '/m/tool.exe', kind: 'file' });
    const i = await until(id, (x) => x.status === 'done', 30000);
    let z = '';
    try { z = execFileSync('powershell', ['-NoProfile', '-Command', `Get-Content -LiteralPath '${join(out, i.name)}' -Stream Zone.Identifier -ErrorAction Stop`], { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim(); } catch { z = ''; }
    console.log('Zone.Identifier de', i.name, ':', z ? z.replace(/\r?\n/g, ' / ') : '(AUCUN)');
  },
  // n° 9 : capture abandonnee, un seul telechargement a la fois -> le fichier suivant part-il ?
  async capture() {
    await start({ MaxParallel: 1 });
    const c = host({ cmd: 'capture_begin', title: 'capture test', pageUrl: 'https://www.youtube.com/watch?v=x', quality: '720', audioOnly: false, total: 1000000 });
    const f = host({ cmd: 'add', url: SRV + '/m/small.mp4', kind: 'file' });
    await sleep(3000);
    console.log('sans annulation : capture', item(c.id).status, '| fichier suivant', item(f.id).status);
    const r = host({ cmd: 'capture_abort', id: c.id });
    await sleep(2500);
    console.log('capture_abort ->', JSON.stringify(r), '| capture', item(c.id).status, '(' + item(c.id).code + ') | fichier suivant', item(f.id).status);
  },
  // n° 2 + n° 1 : cookies gardes en clair ? cookie Secure envoye en HTTP ?
  async cookies() {
    await start();
    await stats(true);
    const ck = [{ name: 'session', value: 'VALEUR_TEMOIN_42', domain: '127.0.0.1', path: '/', secure: false, hostOnly: true },
      { name: 'secret', value: 'SECURE_SEULEMENT', domain: '127.0.0.1', path: '/', secure: true, hostOnly: true }];
    const { id } = host({ cmd: 'add', url: SRV + '/m/small.mp4', kind: 'file', cookies: ck });
    await until(id, (x) => x.status === 'done', 30000);
    stop(); await sleep(800);
    const j = readFileSync(join(data, 'downloads.json'), 'utf8');
    const s = await stats();
    const sent = Object.keys(s).filter((k) => k.startsWith('cookies:')).map((k) => s[k].last);
    console.log('downloads.json contient la valeur temoin :', j.includes('VALEUR_TEMOIN_42') ? 'OUI' : 'non',
      '| cookie Secure envoye en HTTP :', sent.some((c) => c.includes('SECURE_SEULEMENT')) ? 'OUI' : 'non', '| en-tetes recus :', JSON.stringify([...new Set(sent)]));
  },
  // ListForExtension : un telechargement en cours reste-t-il visible apres 52 plus recents ?
  async list50() {
    await start({ MaxParallel: 10 });
    const slow = host({ cmd: 'add', url: SRV + '/m/long.mp4?kbs=50', kind: 'file' });
    await sleep(800);
    host({ cmd: 'control', id: slow.id, action: 'pause' });
    for (let k = 0; k < 52; k++) { const t = Date.now(); host({ cmd: 'add', url: SRV + '/m/small.mp4?n=' + k, kind: 'file', force: true }); if (Date.now() - t > 3000) console.log('ajout', k, 'lent :', Date.now() - t, 'ms'); }
    await sleep(6000);
    const l = list();
    console.log('elements renvoyes :', l.length, '| le telechargement en pause y est :', l.some((x) => x.id === slow.id) ? 'OUI' : 'NON');
  },
  // YouTube : peut-on telecharger directement depuis CE PC ?
  async ytprobe() {
    await start();
    for (const u of ['https://www.youtube.com/watch?v=SfxNQPhEnM8', SRV + '/m/small.mp4']) {
      const t = Date.now(); const r = host({ cmd: 'ytprobe', url: u });
      console.log(u, '->', JSON.stringify(r), ((Date.now() - t) / 1000).toFixed(1) + ' s');
    }
  },
};
function SRV_DIR() { return process.env.BENCH_MEDIA; }

S.run = async () => { await start({ MaxParallel: 3 }); console.log('programme du banc lance (' + bin + ')'); };
try { await S[scen](); } catch (e) { console.log('ERREUR banc :', e.message); } finally { if (scen !== 'run') stop(); }
