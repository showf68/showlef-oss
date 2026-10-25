// ShowLef - relais GENERIQUE entre une page et le programme (monde isole, v0.7.22, « comme IDM »).
// Enregistre par le service worker seulement sur les hotes que le PROGRAMME designe. Ne connait aucun site :
//  - transmet les regles de capture a hook.js (monde de la page) et renvoie ses captures au programme ;
//  - recopie le script de la page qui contient un marqueur donne par le programme ;
//  - signale les changements d'adresse (le programme dit s'il y a une video et laquelle) ;
//  - execute les requetes que le programme demande pendant un telechargement. Elles partent d'ICI (monde isole,
//    origine de la page) et jamais de la page : seulement vers les hotes autorises par le programme, ou la page
//    elle-meme ; methode GET ou POST ; delai et duree bornes ; reponse rendue au programme seul.
(function () {
  if (window.__showlefBridge || window !== window.top) return;
  window.__showlefBridge = true;
  var site = null, TAG = '__showlef';

  function hostIn(h, list) { h = (h || '').toLowerCase(); return (list || []).some(function (x) { return h === x || h.endsWith('.' + x); }); }
  function b64(buf) {
    var b = new Uint8Array(buf), s = '', CH = 0x8000;
    for (var i = 0; i < b.length; i += CH) s += String.fromCharCode.apply(null, b.subarray(i, i + CH));
    return btoa(s);
  }
  function unb64(s) { var t = atob(s || ''), b = new Uint8Array(t.length); for (var i = 0; i < t.length; i++) b[i] = t.charCodeAt(i); return b; }
  // extension rechargee / mise a jour pendant que la page restait ouverte : sendMessage LEVE (« Extension context invalidated »)
  // au lieu de rendre une promesse refusee. Page orpheline : on ne fait plus rien (rechargez la page pour reprendre).
  var dead = false;
  function bg(m) {
    if (dead) return Promise.resolve({ ok: false, error: 'orphan' });
    try { return chrome.runtime.sendMessage(m).catch(function (e) { return { ok: false, error: e.message }; }); }
    catch (e) { dead = true; return Promise.resolve({ ok: false, error: 'orphan' }); }
  }

  var rules;
  try { rules = chrome.storage.local.get('pilotRules'); }
  catch (e) { dead = true; return; }
  rules.then(function (r) {
    if (dead) return;
    var sites = (r.pilotRules && r.pilotRules.sites) || [];
    site = sites.find(function (s) { return hostIn(location.hostname, s.hosts); }) || null;
    if (!site || (typeof showlefIsBlocked === 'function' && showlefIsBlocked(location.href))) { site = null; return; }
    rulesToPage();
    document.addEventListener('DOMContentLoaded', function () { rulesToPage(); scan(); });
    if (document.readyState !== 'loading') scan();
    watchUrl();
  }, function () { });

  function rulesToPage() { window.postMessage({ __showlef: 'rules', capture: site.capture || [] }, '*'); }

  // ---------- captures de hook.js -> programme ----------
  // debit borne par regle (une page qui inonde ne fait pas travailler le programme) : 30 envois par minute au plus
  var sent = {}, pending = {};
  function allow(id) {
    var now = Date.now(), l = (sent[id] || []).filter(function (t) { return now - t < 60000; });
    if (l.length >= 30) { sent[id] = l; return false; }
    l.push(now); sent[id] = l; return true;
  }
  window.addEventListener('message', function (e) {
    if (e.source !== window || !e.data || !site) return;
    var d = e.data;
    if (d[TAG] === 'cap') {
      // la page peut forger ce message : regle connue et taille bornee seulement ; le programme controle le reste
      var r = (site.capture || []).find(function (x) { return x.id === d.rule; });
      if (!r || !(d.body instanceof ArrayBuffer) || d.body.byteLength > (r.maxBytes || 1048576) || !allow(r.id)) return;
      // garde la promesse : « lecture terminee » ne repond au programme qu'APRES que cette capture lui est arrivee
      // (sinon le programme peut lire « pas de modele » alors qu'il est en route)
      pending[r.id] = bg({ type: 'pilotData', rule: r.id, url: String(d.url || '').slice(0, 4096), body: b64(d.body),
        hint: typeof d.page === 'string' ? d.page.slice(0, 4096) : '' });
    } else if (d[TAG] === 'warmed' && job && job.warm && job.warm.rid === d.id) {
      var w = job.warm, jw = job;
      Promise.resolve(pending[w.until]).then(function () { if (jw.warm === w) { jw.warm = null; w.resolve({ rid: w.rid, ok: !!d.ok }); } });
    }
  });

  // ---------- script de la page portant les infos (premier chargement) ----------
  function scan() {
    (site.scan || []).forEach(function (sc) {
      var els = document.querySelectorAll('script:not([src])');
      for (var i = 0; i < els.length; i++) {
        var t = els[i].textContent || '';
        if (t.indexOf(sc.marker) < 0 || t.length > (sc.maxBytes || 1048576)) continue;
        bg({ type: 'pilotData', rule: sc.id, url: location.href, body: b64(new TextEncoder().encode(t).buffer) });
        return;
      }
    });
  }

  // ---------- adresse de la page : le programme dit ce qu'il y a a telecharger ----------
  function isMedia(href) {
    try {
      var u = new URL(href);
      return (site.media || []).some(function (m) {
        return u.pathname.indexOf(m.path) === 0 && (m.query || []).every(function (q) { return u.search.indexOf(q) >= 0; });
      });
    } catch (e) { return false; }
  }
  var lastHref = '';
  function watchUrl() {
    setInterval(check, 1000);
    window.addEventListener('popstate', check);
    check();
  }
  function check() {
    if (dead || location.href === lastHref) return;
    lastHref = location.href;
    if (!isMedia(lastHref)) return;
    var here = lastHref;
    // laisse aux captures le temps d'arriver ; sans infos, le programme demande la page elle-meme (meme origine)
    setTimeout(function () { if (location.href === here) bg({ type: 'pilotPage' }); }, 1200);
    setTimeout(function () {
      if (location.href !== here) return;
      bg({ type: 'pilotPage', allowFetch: true }).then(function (r) {
        var f = r && r.fetch;
        if (!f || f.method !== 'GET' || !sameOrigin(f.url) || location.href !== here) return;
        fetch(f.url, { credentials: 'include', redirect: 'error' }).then(function (x) { return x.ok ? readCapped(x, 16 * 1048576) : null; }).then(function (b) {
          if (b && b.byteLength < 16 * 1048576) bg({ type: 'pilotData', rule: f.rule, url: f.url, body: b64(b) });
        }).catch(function () { });
      });
    }, 3500);
  }
  function sameOrigin(u) { try { return new URL(u).origin === location.origin; } catch (e) { return false; } }
  function fetchOk(u) {
    try { var x = new URL(u); return x.protocol === 'https:' && hostIn(x.hostname, site.fetchHosts); } catch (e) { return false; }
  }

  // ---------- telechargement pilote par le programme ----------
  var job = null;
  // corps rendu au programme : 40 Mo au plus (le base64 + l'enveloppe restent sous la limite de 64 Mo de la messagerie
  // native ; une reponse SABR mesuree fait 2 a 7 Mo)
  var MAX_BODY = 40 * 1048576;
  // corps lu morceau par morceau ; null des que la limite est depassee (rien de plus n'est garde en memoire)
  function readCapped(res, max) {
    if (!res.body || !res.body.getReader) return res.arrayBuffer().then(function (b) { return b.byteLength > max ? null : b; });
    var rd = res.body.getReader(), parts = [], n = 0;
    function pump() {
      return rd.read().then(function (x) {
        if (x.done) {
          var out = new Uint8Array(n), o = 0;
          parts.forEach(function (p) { out.set(p, o); o += p.length; });
          return out.buffer;
        }
        n += x.value.length;
        if (n > max) { rd.cancel().catch(function () { }); return null; }
        parts.push(x.value);
        return pump();
      });
    }
    return pump();
  }
  function finish() {
    var j = job;
    if (!j) return;
    job = null;
    j.cancelled = true;
    clearInterval(j.beat);
    Object.keys(j.ctl).forEach(function (k) { try { j.ctl[k].abort(); } catch (e) { } });
    if (j.warm) j.warm.resolve({ rid: j.warm.rid, ok: false });
  }
  // une reponse du programme : nouvelles requetes a lancer, fin, echec
  function onReply(j, r) {
    if (r && r.stale) return;   // pas parti avant la fin du travail : sans effet
    if (r && r.transport && job === j && j.id) {
      if (++j.lost <= 20) return;
      finish(); bg({ type: 'pilotFail', id: j.id, error: 'app' }); return;
    }
    if (r && r.ok) j.lost = 0;
    if (job !== j) {
      // demarrage annule (onglet ferme, arret) AVANT que le programme ait rendu son identifiant : on l'arrete quand meme
      // (revue Codex 21/10, I2) ; un telechargement ordinaire (yt-dlp, via:'app') continue comme les autres
      if (j.cancelled && r && r.ok && r.id && r.via !== 'app') bg({ type: 'pilotAbort', id: r.id });
      return;
    }
    if (!r || !r.ok || r.done || r.via === 'app') { finish(); return; }
    if (r.id) j.id = r.id;
    (r.actions || []).forEach(function (a) { run(j, a); });
  }
  function step(j, results) {
    j.steps++;
    return bg({ type: 'pilotStep', id: j.id, results: results }).then(function (r) { j.steps--; onReply(j, r); });
  }
  function run(j, a) {
    var rid = a.rid;
    if (a.op === 'warm') {
      new Promise(function (resolve) {
        j.warm = { rid: rid, until: String(a.until || ''), resolve: resolve };
        var lim = Math.min(+a.timeout || 8000, 15000);
        window.postMessage({ __showlef: 'warm', id: rid, until: String(a.until || ''), timeout: lim, seek: Math.max(0, +a.seek || 0) }, '*');
        // si la page ne repond jamais « lecture terminee » (hook absent, page qui l'empeche) : echec apres le delai
        setTimeout(function () { if (j.warm && j.warm.rid === rid) { var w = j.warm; j.warm = null; w.resolve({ rid: rid, ok: false }); } }, lim + 3000);
      }).then(function (res) { if (job === j) step(j, [res]); });
      return;
    }
    if (a.op !== 'fetch') { step(j, [{ rid: rid, ok: false, status: 'op' }]); return; }
    var method = a.method === 'POST' ? 'POST' : 'GET';
    if (!(a.same ? sameOrigin(a.url) : fetchOk(a.url))) { step(j, [{ rid: rid, ok: false, status: 'host' }]); return; }
    var delay = Math.max(0, Math.min(+a.delay || 0, 60000)), limit = Math.max(1000, Math.min(+a.timeout || 45000, 120000));
    setTimeout(function () {
      if (job !== j) return;
      var ac = new AbortController(), key = String(rid);
      j.ctl[key] = ac;
      var tm = setTimeout(function () { ac.abort(); }, limit);
      fetch(a.url, { method: method, body: method === 'POST' && a.body ? unb64(a.body) : undefined, credentials: 'include', cache: 'no-store',
        redirect: 'error', signal: ac.signal })
        .then(function (res) {
          if (!res.ok) return { rid: rid, ok: false, status: res.status };
          var len = +res.headers.get('content-length') || 0;
          if (len > MAX_BODY) { ac.abort(); return { rid: rid, ok: false, status: 'size' }; }
          return readCapped(res, MAX_BODY).then(function (b) {
            if (!b) { ac.abort(); return { rid: rid, ok: false, status: 'size' }; }
            return { rid: rid, ok: true, status: res.status, body: b64(b) };
          });
        })
        .catch(function () { return { rid: rid, ok: false, status: 'net' }; })
        .then(function (out) {
          clearTimeout(tm); delete j.ctl[key];
          if (job === j) step(j, [out]);
        });
    }, delay);
  }

  try { chrome.runtime.onMessage.addListener(function (m, sender, reply) {
    if (dead || !m) return;
    if (m.type === 'pilotGo') {
      if (!site) { reply({ ok: false, error: 'notMedia' }); return; }
      if (job) { reply({ ok: false, error: 'busy' }); return; }
      var j = job = { id: '', steps: 0, ctl: {}, warm: null, lost: 0 };
      // battement : progression, et annulation vue en moins de 2 s meme quand toutes les requetes sont en vol
      j.beat = setInterval(function () { if (job === j && j.id && !j.steps) step(j, []); }, 1000);
      reply({ ok: true });
      bg({ type: 'pilotStart', quality: m.quality, audioOnly: !!m.audioOnly }).then(function (r) { onReply(j, r); });
    } else if (m.type === 'pilotStop') {
      if (job) { var id = job.id; finish(); if (id) bg({ type: 'pilotAbort', id: id }); }
      reply({ ok: true });
    }
  }); } catch (e) { dead = true; return; }
  window.addEventListener('pagehide', function () {
    if (!job) return;
    var id = job.id; finish();
    if (id) bg({ type: 'pilotAbort', id: id });
  });
})();
