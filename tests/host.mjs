// Envoie un message au programme comme le ferait Chrome (native messaging) : node tests/host.mjs '{"cmd":"ping"}'
import { spawn } from 'node:child_process';
import { join } from 'node:path';
const exe = join(process.env.SHOWLEF_BIN || join(process.env.LOCALAPPDATA, 'ShowLef'), 'showlef-host.exe');   // SHOWLEF_BIN : banc
const msg = Buffer.from(process.argv[2] || '{"cmd":"ping"}', 'utf8');
const p = spawn(exe, ['chrome-extension://mepcfiemlmpffknhaebjocpddbaahdfc/']);
const chunks = [];
p.stdout.on('data', d => chunks.push(d));
p.on('close', code => {
  const b = Buffer.concat(chunks);
  if (b.length < 4) { console.error('reponse vide, rc=' + code); process.exit(1); }
  const n = b.readUInt32LE(0);
  console.log(b.slice(4, 4 + n).toString('utf8'));
});
const len = Buffer.alloc(4); len.writeUInt32LE(msg.length);
p.stdin.write(Buffer.concat([len, msg])); p.stdin.end();
