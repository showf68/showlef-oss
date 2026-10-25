// Execute the whole content script, including initialization, with a revoked Chrome context.
import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source = fs.readFileSync(new URL('../extension/bridge.js', import.meta.url), 'utf8');
let checks = 0;
async function run(fail) {
  const events = {}, intervals = [], timeouts = [];
  let calls = 0, listener;
  const window = { addEventListener: (key, cb) => { events[key] = cb; }, postMessage() {} }; window.top = window;
  const invalid = () => { throw new Error('Extension context invalidated.'); };
  const chrome = {
    storage: { local: { get: fail === 'storage' ? invalid : () => Promise.resolve({ pilotRules: { sites: [{ hosts: ['example.test'], media: [{ path: '/watch' }] }] } }) } },
    runtime: { sendMessage: () => { calls++; if (fail === 'send') return invalid(); if (fail === 'reject') return Promise.reject(new Error('Extension context invalidated.')); return Promise.resolve({ ok: true }); },
      onMessage: { addListener: fail === 'listener' ? invalid : cb => { listener = cb; } } }
  };
  const context = { window, chrome, location: { hostname: 'example.test', href: 'https://example.test/watch' }, document: { readyState: 'complete', addEventListener() {}, querySelectorAll: () => [] },
    setInterval: cb => { intervals.push(cb); return intervals.length; }, clearInterval() {}, setTimeout: cb => { timeouts.push(cb); return timeouts.length; }, clearTimeout() {}, URL, Promise, TextEncoder, Uint8Array, ArrayBuffer };
  vm.runInNewContext(source, context);
  await new Promise(r => setImmediate(r));
  timeouts.splice(0).forEach(cb => cb());
  await new Promise(r => setImmediate(r));
  if (fail === 'send') {
    const before = calls; context.location.href += '?next=1'; intervals.forEach(cb => cb()); timeouts.splice(0).forEach(cb => cb());
    await new Promise(r => setImmediate(r)); assert.equal(calls, before, 'orphan does not send again'); checks++;
  }
  if (!fail) { assert.equal(typeof listener, 'function'); assert.ok(calls > 0); checks += 2; }
  checks++;
}
for (const fail of ['storage', 'listener', 'send', 'reject', null]) await run(fail);
console.log(`OK ${checks} whole-bridge context checks`);
