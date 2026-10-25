// Affiche la file du programme : node tests/list.mjs
import { execFileSync } from 'node:child_process';
const out = execFileSync('node', [new URL('./host.mjs', import.meta.url).pathname.slice(1), '{"cmd":"list"}']).toString();
for (const i of JSON.parse(out).items)
  console.log(i.status.padEnd(12), String(i.received).padStart(11), String(i.total).padStart(11), (Math.round(i.speed / 1024) + ' Ko/s').padStart(11), i.name, i.error ? '| ' + i.error.slice(0, 160) : '');
