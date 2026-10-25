// Banc « filtre HTTPS » (NetFree, Techloq...) : ShowLef derriere un filtre qui inspecte le HTTPS doit telecharger
// quand Windows fait confiance a l'autorite du filtre, et dire clairement pourquoi il echoue sinon.
//   1. node tests/filtre-tls.mjs <dossier ca> 8899        (le faux filtre, dans un autre terminal)
//   2. node tests/filtre-tls-bench.mjs <bin> <work> <ca.crt> <yt-dlp.exe> <ffmpeg.exe> [scenario...]
// <bin> : programme compile a part (tests/build-bench.ps1), <work> : dossier de travail (une sous-donnee par scenario).
// Le ShowLef installe est arrete (une seule instance par utilisateur). Scenarios :
//   direct      : sans filtre (temoin : le chemin normal marche toujours)
//   refuse      : filtre, autorite INCONNUE de Windows -> erreur « tls » lisible, rien d'appris (temoin qui doit echouer)
//   filtre      : filtre, autorite connue de Windows (--trust-ca) -> telechargement complet, video + audio
//   filtre-page : idem en « telecharger la page » (yt-dlp analyse alamdu.org)
import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync, readFileSync, rmSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const [bin, work, ca, ytdlp, ffmpeg, ...wanted] = process.argv.slice(2);
const MPD = 'https://customer-brx72yc55gqr79sa.cloudflarestream.com/28a6291d2ca4730004622640cc8f82d3/manifest/video.mpd';
const PAGE = 'https://alamdu.org/lessons/%d7%a9%d7%99%d7%a2%d7%95%d7%a8-%d7%93%d7%a3-%d7%9b%d7%93-5/';
const PROXY = 'http://127.0.0.1:8899';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const ffprobe = join(ffmpeg, '..', 'ffprobe.exe');

function host(msg) {
  const b = Buffer.from(JSON.stringify(msg), 'utf8'); const len = Buffer.alloc(4); len.writeUInt32LE(b.length);
  const r = execFileSync(join(bin, 'showlef-host.exe'), ['chrome-extension://mepcfiemlmpffknhaebjocpddbaahdfc/'], { input: Buffer.concat([len, b]), timeout: 60000 });
  return JSON.parse(r.subarray(4, 4 + r.readUInt32LE(0)).toString('utf8'));
}
function stop() { for (const p of ['ShowLef.exe', 'yt-dlp.exe', 'ffmpeg.exe']) try { execFileSync('taskkill', ['/F', '/IM', p], { stdio: 'ignore' }); } catch { } }
async function start(data, proxy, trust) {
  stop(); await sleep(800);
  const env = { ...process.env }; delete env.HTTPS_PROXY; delete env.https_proxy;
  if (proxy) env.HTTPS_PROXY = PROXY;
  const args = ['--dir', data, '--lang', 'he', '--tray', ...(trust ? ['--trust-ca', ca] : [])];
  spawn(join(bin, 'ShowLef.exe'), args, { detached: true, stdio: 'ignore', env }).unref();
  for (let i = 0; i < 40; i++) { await sleep(500); try { const p = host({ cmd: 'ping' }); if (p.ok) return p; } catch { } }
  throw new Error('programme du banc injoignable');
}
function prepare(name) {
  const data = join(work, name);
  rmSync(data, { recursive: true, force: true }); mkdirSync(join(data, 'dl'), { recursive: true });
  writeFileSync(join(data, 'settings.json'), JSON.stringify({ Folder: join(data, 'dl'), SortByType: false, Connections: 8, MaxParallel: 1,
    Quality: '360', AudioFormat: 'mp3', FfmpegPath: ffmpeg, YtdlpPath: ytdlp, Clipboard: false, StartWithWindows: false, Notify: false,
    CloseToTray: true, Retries: 2, SiteFolders: {}, PingUsers: false }));
  return data;
}
async function until(id, ms) {
  const t = Date.now(); let it;
  while (Date.now() - t < ms) { it = host({ cmd: 'list' }).items.find((i) => i.id === id); if (it && (it.status === 'done' || it.status === 'error')) return it; await sleep(1000); }
  return it;
}
function streams(file) {
  const o = execFileSync(ffprobe, ['-v', 'error', '-show_entries', 'stream=codec_type', '-of', 'csv=p=0', file]).toString();
  return o.split(/\r?\n/).filter(Boolean).sort().join('+');
}
const log = (data) => { const f = join(data, 'showlef.log'); return existsSync(f) ? readFileSync(f, 'utf8') : ''; };

const S = {
  direct: { proxy: false, trust: false, url: MPD, kind: 'dash', expect: 'done' },
  refuse: { proxy: true, trust: false, url: MPD, kind: 'dash', expect: 'tls' },
  filtre: { proxy: true, trust: true, url: MPD, kind: 'dash', expect: 'done' },
  'filtre-page': { proxy: true, trust: true, url: PAGE, kind: 'page', expect: 'done' },
};
let fails = 0;
for (const name of (wanted.length ? wanted : Object.keys(S))) {
  const sc = S[name]; const data = prepare(name);
  const p = await start(data, sc.proxy, sc.trust);
  const t0 = Date.now();
  const add = host({ cmd: 'add', url: sc.url, kind: sc.kind, referer: 'https://alamdu.org/', title: 'banc ' + name });
  const it = await until(add.id, 15 * 60000);
  const sec = ((Date.now() - t0) / 1000).toFixed(1);
  const files = readdirSync(join(data, 'dl')).filter((f) => !f.endsWith('.part'));
  const L = log(data);
  let ok, detail;
  if (sc.expect === 'done') {
    const f = files[0] && join(data, 'dl', files[0]);
    const st = f ? streams(f) : '';
    ok = it && it.status === 'done' && f && statSync(f).size > 1e6 && st === 'audio+video';
    detail = `statut=${it && it.status} fichier=${files[0] || '-'} taille=${f ? statSync(f).size : 0} pistes=${st || '-'} appris=${(L.match(/autorite apprise/g) || []).length}`;
  } else {
    ok = it && it.status === 'error' && it.code === 'tls' && files.length === 0 && /refusee par Windows/.test(L);
    detail = `statut=${it && it.status} code=${it && it.code} erreur=${it && it.error} fichiers=${files.length} refus-windows=${/refusee par Windows/.test(L)}`;
  }
  console.log(`${ok ? 'OK  ' : 'ECHEC'} ${name} (programme ${p.version}, ${sec} s) ${detail}`);
  if (!ok) { fails++; console.log(L.split('\n').slice(-25).join('\n')); }
}
stop();
process.exit(fails ? 1 : 0);
