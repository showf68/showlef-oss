import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source = fs.readFileSync(new URL('../extension/background.js', import.meta.url), 'utf8');
const start = source.indexOf('function wakeLoop()');
const code = source.slice(start, source.indexOf('function wakeFail(', start));
const token = 'a'.repeat(32), messages = [], videos = [];
const context = { wakeOn: false, setTimeout() {}, wakeResume: w => videos.push(w), nativeSend: msg => {
  messages.push(msg);
  return Promise.resolve(msg.cmd === 'pilot_wait' ? { ok: true, wakes: [{ type: 'extension_probe', token }, { type: 'extension_probe', token: '../bad' }, { url: 'https://www.youtube.com/watch?v=test' }] } : { ok: true });
} };
vm.runInNewContext(code + '\nwakeLoop();', context);
await new Promise(resolve => setImmediate(resolve));
assert.equal(messages.filter(m => m.cmd === 'extension_probe_reply').length, 1);
assert.equal(messages[1].token, token);
assert.equal(videos.length, 1);
assert.ok(videos[0].url.includes('watch?v=test'));
console.log('OK 4 connection probe checks: reply to fresh challenge, no tab for probes, video wakes preserved');
