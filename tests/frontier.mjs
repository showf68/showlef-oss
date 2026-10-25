// node tests/frontier.mjs <dossier jeu d'essai> <sortie>
// FRONTIERE avant/apres (etape 1 du chantier « comme IDM ») : rejoue le code JavaScript de l'extension 0.7.21
// (yt_main.js, extrait tel quel du fichier, rien de reecrit) sur un jeu d'essai reel et ecrit, pour chaque cas, le
// corps de requete SABR qu'il aurait envoye. Le moteur C# du programme (tests/PilotTest.cs) produit les memes cas :
// les deux doivent etre IDENTIQUES octet pour octet.
import { readFileSync, writeFileSync, readdirSync, mkdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const [fix, out, srcArg] = process.argv.slice(2);
const SRC = srcArg || resolve(dirname(fileURLToPath(import.meta.url)), '..', 'extension', 'yt_main.js');
const js = readFileSync(SRC, 'utf8');
const cut = (a, b) => { const i = js.indexOf(a), j = js.indexOf(b, i); if (i < 0 || j < 0) throw new Error('extrait introuvable : ' + a); return js.slice(i, j); };
// fonctions de premier niveau (decodage UMP, protobuf) et les deux fonctions internes de directRun
const helpers = cut('  function vi(b, p)', '  // Protobuf minimal') + cut('  function rv(b, p)', '  function sleep(ms)');
const bodyFn = cut('    function body(ms) {', '    function control(b, ms) {');
const controlFn = cut('    function control(b, ms) {', '    function at(ms) {');
const make = new Function('c', 'd', 'fm', 'tpl', 'dec', 'var height = fm.v ? fm.v.height : 144;' + helpers + bodyFn + controlFn + '; return { body: body, control: control, pbList: pbList };');

mkdirSync(out, { recursive: true });
const pl = JSON.parse(readFileSync(join(fix, 'player.json'), 'utf8'));
const fmts = {};
pl.streamingData.adaptiveFormats.forEach((f) => { fmts[f.itag] = { itag: f.itag, lmt: f.lastModified, len: +f.contentLength, audio: f.mimeType.startsWith('audio/'), height: f.height || 0 }; });
const reqs = readdirSync(fix).filter((n) => /^req\d+\.bin$/.test(n)).sort();
const ress = readdirSync(fix).filter((n) => /^res\d+\.bin$/.test(n)).sort();
const cases = [
  { name: 'v720', a: 251, v: 298, audioOnly: false }, { name: 'v144', a: 251, v: 160, audioOnly: false },
  { name: 'aud', a: 251, v: null, audioOnly: true }, { name: 'v1080', a: 140, v: 299, audioOnly: false },
];
let n = 0;
for (const cs of cases) {
  for (const rq of reqs) {
    const tplBytes = new Uint8Array(readFileSync(join(fix, rq)));
    const fm = { a: fmts[cs.a], v: cs.v ? fmts[cs.v] : null };
    const c = { audioOnly: cs.audioOnly, st: {}, wanted: (itag) => itag === cs.a || itag === cs.v };
    const d = { cookie: null, ctx: {}, end: {}, notBefore: 0, span: 20000 };
    let tpl;
    const f = make(c, d, fm, null, new TextDecoder());
    tpl = f.pbList(tplBytes);
    const g = make(c, d, fm, tpl, new TextDecoder());
    const tag = cs.name + '_' + rq.replace('.bin', '');
    writeFileSync(join(out, tag + '_t0.bin'), g.body(0));                // avant toute reponse : modele du lecteur seul
    for (const rs of ress) g.control(new Uint8Array(readFileSync(join(fix, rs))), 0);   // cookie, contextes, fins de piste
    writeFileSync(join(out, tag + '_t1.bin'), g.body(123456));
    writeFileSync(join(out, tag + '_state.json'), JSON.stringify({ end: d.end, span: d.span, cookie: d.cookie ? Buffer.from(d.cookie).toString('hex') : null,
      ctx: Object.fromEntries(Object.entries(d.ctx).map(([k, v]) => [k, Buffer.from(v).toString('hex')])) }));
    n++;
  }
}
console.log(JSON.stringify({ cases: n, files: n * 3 }));
