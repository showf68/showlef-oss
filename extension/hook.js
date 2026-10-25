// ShowLef - capture GENERIQUE dans le monde de la page (v0.7.22, « comme IDM »).
// Enregistre par le service worker seulement sur les hotes que le PROGRAMME ShowLef designe (rien dans le manifeste).
// Ne connait aucun site : il recoit des regles (hote, debut de chemin, morceaux d'adresse, methode, requete ou
// reponse, taille maximale) et recopie, pour le programme, les echanges fetch/XMLHttpRequest qui y correspondent.
// Il ne fait AUCUNE requete lui-meme (les requetes demandees par le programme partent du monde isole, bridge.js).
// Ce monde est partage avec la page : tout ce qui en sort est une DONNEE, controlee par le programme.
(function () {
  if (window.__showlefHook) return;
  window.__showlefHook = true;
  var rules = [];          // regles « capture » de ce site
  var TAG = '__showlef';

  function match(r, url, method) {
    try {
      var u = new URL(url, location.href), h = u.hostname.toLowerCase();
      if (!(h === r.host || h.endsWith('.' + r.host))) return false;
      if (r.path && u.pathname.indexOf(r.path) !== 0) return false;
      if (r.method && String(method || 'GET').toUpperCase() !== r.method) return false;
      var q = u.search;
      return (r.query || []).every(function (s) { return q.indexOf(s) >= 0; });
    } catch (e) { return false; }
  }
  function find(url, method, part) {
    for (var i = 0; i < rules.length; i++) if (rules[i].part === part && match(rules[i], url, method)) return rules[i];
    return null;
  }
  function emit(r, url, buf) {
    if (!buf || !buf.byteLength || buf.byteLength > (r.maxBytes || 1048576)) return;
    window.postMessage({ __showlef: 'cap', rule: r.id, url: String(url), body: buf, page: emit.page || location.href }, '*');
  }
  // capture rattachee a la page AU MOMENT de la requete (pas a celle d'apres une navigation interne)
  function emitAt(r, url, buf, page) { emit.page = page; try { emit(r, url, buf); } finally { emit.page = null; } }
  function bytesOf(body) {
    if (!body) return Promise.resolve(null);
    if (body instanceof ArrayBuffer) return Promise.resolve(body.slice(0));
    if (ArrayBuffer.isView(body)) return Promise.resolve(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength));
    if (typeof body === 'string') return Promise.resolve(new TextEncoder().encode(body).buffer);
    if (body instanceof Blob) return body.arrayBuffer();
    return Promise.resolve(null);
  }

  // ---------- fetch ----------
  var origFetch = window.fetch;
  window.fetch = function (input, init) {
    var url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input && input.url) || '';
    var method = (init && init.method) || (input instanceof Request ? input.method : 'GET');
    var rq = rules.length ? find(url, method, 'request') : null, here = location.href;
    if (rq) {
      try {
        var p = init && 'body' in init ? bytesOf(init.body) : input instanceof Request ? input.clone().arrayBuffer() : Promise.resolve(null);
        p.then(function (b) { emitAt(rq, url, b, here); }, function () { });
      } catch (e) { }
    }
    var res = origFetch.apply(this, arguments);
    var rs = rules.length ? find(url, method, 'response') : null;
    if (rs) res.then(function (r) { return r.clone().arrayBuffer(); }).then(function (b) { emitAt(rs, url, b, here); }, function () { });
    return res;
  };

  // ---------- XMLHttpRequest ----------
  var XO = XMLHttpRequest.prototype.open, XS = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url) {
    this.__showlefReq = { method: method, url: String(url) };
    return XO.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function (body) {
    var q = this.__showlefReq, x = this;
    if (q && rules.length) {
      q.page = location.href;
      var rq = find(q.url, q.method, 'request');
      if (rq) bytesOf(body).then(function (b) { emitAt(rq, q.url, b, q.page); }, function () { });
      if (!x.__showlefLoad) {
        x.__showlefLoad = true;
        x.addEventListener('load', function () {
          var c = x.__showlefReq, rs = c && find(c.url, c.method, 'response');
          if (!rs) return;
          try {
            var t = x.responseType, v = x.response;
            bytesOf(t === '' || t === 'text' ? x.responseText : t === 'json' ? JSON.stringify(v) : v).then(function (b) { emitAt(rs, c.url, b, c.page); }, function () { });
          } catch (e) { }
        });
      }
    }
    return XS.apply(this, arguments);
  };

  // ---------- lecture muette pour que le lecteur fasse SA requete (demandee par le programme) ----------
  // Generique : la plus grande <video> de la page, lue sans le son jusqu'a ce qu'une capture de la regle demandee
  // arrive (ou le delai), puis remise EXACTEMENT comme avant (position, pause, son).
  var warming = null;
  function mainVideo() {
    var best = null, area = 0;
    document.querySelectorAll('video').forEach(function (v) { var r = v.getBoundingClientRect(), a = r.width * r.height; if (a > area) { area = a; best = v; } });
    return best;
  }
  function warm(m) {
    var v = mainVideo();
    if (!v || warming) { window.postMessage({ __showlef: 'warmed', id: m.id, ok: false }, '*'); return; }
    var saved = { t: v.currentTime, paused: v.paused, muted: v.muted };
    warming = { id: m.id, until: m.until, done: function (ok) {
      if (!warming) return;
      clearTimeout(warming.tm); warming = null;
      if (saved.paused) v.pause();
      try { v.currentTime = saved.t; } catch (e) { }
      v.muted = saved.muted;
      window.postMessage({ __showlef: 'warmed', id: m.id, ok: ok }, '*');
    } };
    warming.tm = setTimeout(function () { if (warming) warming.done(false); }, Math.min(+m.timeout || 8000, 15000));
    v.muted = true;
    // position demandee (secondes) : le lecteur doit faire une requete NEUVE, ce qu'il ne fait pas tant qu'il lit ce qu'il a
    // deja en memoire (mesure 21/10 : 3 lectures muettes au debut, aucune requete)
    var at = +m.seek;
    if (at > 0 && isFinite(at) && (!v.duration || at < v.duration)) { try { v.currentTime = at; } catch (e) { } }
    v.play().catch(function () { });
  }

  window.addEventListener('message', function (e) {
    if (e.source !== window || !e.data) return;
    var d = e.data;
    if (d[TAG] === 'rules' && Array.isArray(d.capture)) {
      rules = d.capture.filter(function (r) { return r && typeof r.id === 'string' && typeof r.host === 'string' && (r.part === 'request' || r.part === 'response'); });
    } else if (d[TAG] === 'warm') warm(d);
    else if (d[TAG] === 'cap' && warming && d.rule === warming.until) warming.done(true);
  });
})();
