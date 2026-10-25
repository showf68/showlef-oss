// Banc « le filtre refuse le programme mais pas le navigateur » (NetFree, 418) - extension 0.7.30 + programme 0.7.52.
//   node tests/chrome-hls.mjs <bin du banc> <work> <ffmpeg.exe> [video|audio]
// 1. un faux filtre (serveur local) repond 418 a toute requete SANS en-tete Sec-Fetch-Site (= tout sauf un navigateur) ;
// 2. une page avec un iframe lit un flux HLS fMP4 (video + audio separe) comme un lecteur ;
// 3. l'extension (Chrome de test, port 9334) envoie le flux au programme du banc, qui prend 418 ;
// 4. l'extension doit refaire le telechargement par le cadre de la page et le programme assembler le fichier.
// Attendu : « OK » avec un MP4 video + audio de ~12 s. Temoin : le journal du faux filtre montre des 418 pour le programme
// ET des 200 « same-origin » pour le cadre. Prealable : node tests/chrome-test.mjs start
import { spawn, execFileSync } from 'node:child_process';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, writeFileSync, readFileSync, rmSync, readdirSync } from 'node:fs';
import { join, extname } from 'node:path';

const [bin, work, ffmpeg, scenario = 'video'] = process.argv.slice(2);   // scenario : video | audio (MP3) | ytdlp (le programme passe par yt-dlp : kind dash)
const PORT = 8791, P = 'http://127.0.0.1:9334', EXT = 'mepcfiemlmpffknhaebjocpddbaahdfc';
const ffprobe = join(ffmpeg, '..', 'ffprobe.exe');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const hlsDir = join(work, process.env.BENCH_DIR || 'hls'), data = join(work, 'data');

// ---------- fixture : HLS fMP4, video + audio separe (comme Cloudflare Stream) ----------
if (!existsSync(join(hlsDir, 'master.m3u8'))) {
  mkdirSync(hlsDir, { recursive: true });
  execFileSync(ffmpeg, ['-y', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=640x360:rate=25', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', process.env.BENCH_T || '12',
    '-map', '0:v', '-map', '1:a', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-g', '75', '-c:a', 'aac', '-f', 'hls', '-hls_time', process.env.BENCH_HT || '3', '-hls_playlist_type', 'vod',
    '-hls_segment_type', 'fmp4', '-master_pl_name', 'master.m3u8', '-var_stream_map', 'v:0,agroup:aud,name:video a:0,agroup:aud,name:audio,default:yes',
    '-hls_segment_filename', join(hlsDir, 's_%v_%03d.m4s'), join(hlsDir, 'p_%v.m3u8')], { cwd: hlsDir });
}

// ---------- faux filtre ----------
const seen = [];
const TYPES = { '.m3u8': 'application/vnd.apple.mpegurl', '.m4s': 'video/iso.segment', '.mp4': 'video/mp4' };
const PLAYER = `<!doctype html><meta charset=utf-8><script>
(async () => {
  const base = '/hls/';
  const m = await (await fetch(base + 'master.m3u8')).text();
  for (const f of ['p_video.m3u8', 'p_audio.m3u8']) await (await fetch(base + f)).text();
  await (await fetch(base + 's_video_000.m4s')).arrayBuffer();
  document.title = 'lu';
})();
</script>`;
const srv = createServer((q, r) => {
  if (q.url === '/page.html') { r.setHeader('content-type', 'text/html'); return r.end('<!doctype html><meta charset=utf-8><title>cours</title><h1>cours</h1><iframe src="/player.html" width=400 height=300></iframe>'); }
  if (q.url === '/player.html') { r.setHeader('content-type', 'text/html'); return r.end(PLAYER); }
  if (q.url === '/log') { r.setHeader('content-type', 'application/json'); return r.end(JSON.stringify(seen)); }
  const m = /^\/hls\/([\w.]+)$/.exec(q.url.split('?')[0]);
  const f = m && join(hlsDir, m[1]);
  const site = q.headers['sec-fetch-site'] || '';
  seen.push({ p: q.url, site, ua: (q.headers['user-agent'] || '').slice(0, 24) });
  // BENCH_NOBLOCK=1 : aucun refus (temoin : yt-dlp seul doit rendre le fichier complet, verdict ECHEC attendu faute de 418) ; BENCH_SEG418=1 : listes servies a tous, seuls les MORCEAUX refuses au programme (cas NetFree / Cloudflare Stream de Motti, 25/10)
  if (!site && !process.env.BENCH_NOBLOCK && (!process.env.BENCH_SEG418 || /\.m4s$/.test(q.url.split('?')[0]))) { r.statusCode = 418; return r.end('Blocked by NetFree'); }
  if (!f || !existsSync(f)) { r.statusCode = 404; return r.end(); }
  r.setHeader('content-type', TYPES[extname(f)] || 'application/octet-stream');
  r.end(readFileSync(f));
}).listen(PORT, '127.0.0.1');

// ---------- programme du banc ----------
function host(msg) {
  const b = Buffer.from(JSON.stringify(msg), 'utf8'); const len = Buffer.alloc(4); len.writeUInt32LE(b.length);
  const r = execFileSync(join(bin, 'showlef-host.exe'), ['chrome-extension://' + EXT + '/'], { input: Buffer.concat([len, b]), timeout: 60000 });
  return JSON.parse(r.subarray(4, 4 + r.readUInt32LE(0)).toString('utf8'));
}
for (const p of ['ShowLef.exe']) try { execFileSync('taskkill', ['/F', '/IM', p], { stdio: 'ignore' }); } catch { }
await sleep(800);
rmSync(data, { recursive: true, force: true }); mkdirSync(join(data, 'dl'), { recursive: true });
writeFileSync(join(data, 'settings.json'), JSON.stringify({ Folder: join(data, 'dl'), SortByType: false, Connections: 4, MaxParallel: 1, Quality: '360', AudioFormat: 'mp3',
  FfmpegPath: ffmpeg, YtdlpPath: join(ffmpeg, '..', 'yt-dlp.exe'), Clipboard: false, StartWithWindows: false, Notify: false, CloseToTray: true, Retries: 1, SiteFolders: {}, PingUsers: false }));
// 0.7.53 : sans compte Google lie le programme refuse tout ; le banc reprend celui de l'installation (bin = dossier installe)
if (existsSync(join(bin, 'account.json'))) writeFileSync(join(data, 'account.json'), readFileSync(join(bin, 'account.json')));
spawn(join(bin, 'ShowLef.exe'), ['--dir', data, '--lang', 'he', '--tray'], { detached: true, stdio: 'ignore' }).unref();
let ping;
for (let i = 0; i < 40 && !ping; i++) { await sleep(500); try { const p = host({ cmd: 'ping' }); if (p.ok) ping = p; } catch { } }
if (!ping) throw new Error('programme du banc injoignable');
console.log('programme', ping.version);

// ---------- Chrome de test ----------
async function conn(url) {
  const ws = new WebSocket(url); await new Promise((o, k) => { ws.onopen = o; ws.onerror = k; });
  let id = 0; const wait = {};
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } };
  const send = (method, params = {}, to = 20000) => new Promise((ok) => {
    const i = ++id; wait[i] = ok; ws.send(JSON.stringify({ id: i, method, params }));
    setTimeout(() => { if (wait[i]) { delete wait[i]; ok({ timeout: true }); } }, to);
  });
  return { ws, send };
}
const b = await conn((await (await fetch(P + '/json/version')).json()).webSocketDebuggerUrl);
for (const t of (await (await fetch(P + '/json/list')).json()).filter((x) => x.type === 'page' && x.url.includes('127.0.0.1:' + PORT))) await b.send('Target.closeTarget', { targetId: t.id });   // pages d'un essai precedent
await b.send('Target.createTarget', { url: 'http://127.0.0.1:' + PORT + '/page.html' });
await sleep(3500);
const targets = await (await fetch(P + '/json/list')).json();
const sw = targets.find((t) => t.type === 'service_worker' && t.url.includes(EXT));
if (!sw) throw new Error('service worker de l extension introuvable (node tests/chrome-test.mjs start ?)');
const c = await conn(sw.webSocketDebuggerUrl);
const ev = (expr, to) => c.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }, to)
  .then((r) => r.timeout ? 'TIMEOUT' : r.result.exceptionDetails ? 'EXC ' + JSON.stringify(r.result.exceptionDetails).slice(0, 400) : r.result.result.value);
const first = await ev(`(async () => {
  const [t] = await chrome.tabs.query({ url: 'http://127.0.0.1:${PORT}/page.html' });
  const st = await readTab(t.id);
  const it = st.items.find((i) => i.kind === 'hls' && /master/.test(i.url)) || st.items.find((i) => i.kind === 'hls' && !i.audio);   // la liste maitre d'abord (video + audio)
  if (!it) return JSON.stringify({ err: 'aucun flux HLS detecte', items: st.items.map((i) => [i.kind, i.url]) });
  const r = ${scenario === 'ytdlp'
    ? "await sendToApp(t.id, { url: it.url, kind: 'dash', frameId: it.frame || 0 }).then((x) => { track(t.id, it.frame || 0, x); return x; })"
    : "await handleDownload({ type: 'download', itemId: it.id, audioOnly: " + (scenario === 'audio') + " }, t.id, it.frame || 0)"};
  return JSON.stringify({ tab: t.id, item: [it.kind, it.url, it.frame], r });
})()`);
console.log('lancement', first);
let item, t0 = Date.now();
while (Date.now() - t0 < 90000) {
  await sleep(1500);
  const l = host({ cmd: 'list' }).items;
  item = l.find((i) => i.kind === 'capture') || l[l.length - 1];
  if (item && (item.status === 'done' || (item.status === 'error' && item.code !== 'blocked'))) break;
}
console.log('element', JSON.stringify(item));
const log = await (await fetch('http://127.0.0.1:' + PORT + '/log')).json();
const n418 = log.filter((x) => !x.site).length, nSame = log.filter((x) => x.site === 'same-origin').length;
console.log('filtre : ' + n418 + ' refus (programme), ' + nSame + ' requetes du cadre (same-origin)');
let verdict = 'ECHEC';
if (item && item.status === 'done') {
  const f = join(data, 'dl', item.name);
  const files = existsSync(join(data, 'dl')) ? readdirSync(join(data, 'dl'), { recursive: true }) : [];
  console.log('fichiers', JSON.stringify(files));
  const out = files.map((x) => join(data, 'dl', x)).find((x) => /\.(mp4|mkv|mp3)$/i.test(x));
  if (out) {
    const s = execFileSync(ffprobe, ['-v', 'error', '-show_entries', 'stream=codec_type:format=duration', '-of', 'default=nw=1', out]).toString().split(/\r?\n/).filter(Boolean).sort().join(' ');
    console.log('ffprobe', s);
    const wantVideo = scenario !== 'audio';
    const dur = parseFloat((/duration=([\d.]+)/.exec(s) || [])[1] || '0'), want = 0.8 * Number(process.env.BENCH_T || 12);   // un fichier fait des seuls en-tetes a les pistes mais pas la duree
    if (/codec_type=audio/.test(s) && /codec_type=video/.test(s) === wantVideo && dur >= want && n418 > 0 && nSame > 0) verdict = 'OK';
  }
}
console.log(verdict);
srv.close();
process.exit(verdict === 'OK' ? 0 : 1);
