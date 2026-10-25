// ShowLef - service worker : detection des medias, menus, interception, relais vers le programme.
// Etat par onglet dans chrome.storage.session (survit a l'arret du worker), efface a la navigation.
importScripts('config.js', 'i18n.js');

var HOST = 'com.showlef.app';
var MEDIA_EXT = ['mp4', 'webm', 'mkv', 'mov', 'm4v', 'mp3', 'm4a', 'aac', 'ogg', 'opus', 'wav', 'flac', 'm3u8', 'mpd'];
var AUDIO_EXT = ['mp3', 'm4a', 'aac', 'ogg', 'opus', 'wav', 'flac'];
var SEG_EXT = ['ts', 'm4s'];
var RANGE_PARAMS = ['range', 'bytes', 'rn', 'rbuf'];
var FRAG_RE = /(?:^|[\/_\-.])(?:seg|segment|frag|fragment|chunk)[-_]?\d+/i;
// Un fichier de moins de 3 Mo sur un onglet qui a deja un manifeste est presque toujours un morceau du flux.
var FRAG_MAX_WITH_MANIFEST = 3 * 1024 * 1024;
var INTERCEPT_EXT = ['zip', 'rar', '7z', 'exe', 'msi', 'iso', 'img', 'apk', 'dmg', 'tar', 'gz', 'bz2', 'xz',
  'mp4', 'mkv', 'avi', 'mov', 'webm', 'wmv', 'flv', 'mp3', 'm4a', 'flac', 'wav', 'ogg', 'pdf'];
var DEFAULTS = { lang: '', overlay: true, intercept: false, minSize: 300, quality: 'normal', audioFormat: 'mp3', advanced: false };

// ---------- reglages ----------
var settingsCache = null;
function getSettings() {
  if (settingsCache) return Promise.resolve(settingsCache);
  return chrome.storage.local.get('settings').then(function (r) {
    settingsCache = Object.assign({}, DEFAULTS, r.settings || {});
    return settingsCache;
  });
}
chrome.storage.onChanged.addListener(function (ch, area) {
  if (area !== 'local' || !ch.settings) return;
  var oldLang = settingsCache ? settingsCache.lang : null;
  settingsCache = Object.assign({}, DEFAULTS, ch.settings.newValue || {});
  if (settingsCache.lang !== oldLang) SHOWLEF_I18N.init().then(buildMenus);
});

// ---------- outils URL ----------
function urlExt(url) {
  try {
    var p = new URL(url).pathname;
    var m = /\.([a-z0-9]{1,5})$/i.exec(p);
    return m ? m[1].toLowerCase() : '';
  } catch (e) { return ''; }
}
function normUrl(url) {
  try {
    var u = new URL(url);
    RANGE_PARAMS.forEach(function (p) { u.searchParams.delete(p); });
    u.hash = '';
    return u.toString();
  } catch (e) { return url; }
}
function hashKey(s) {
  var h = 0;
  for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return (h >>> 0).toString(36);
}
function header(list, name) {
  name = name.toLowerCase();
  for (var i = 0; i < (list || []).length; i++) if (list[i].name.toLowerCase() === name) return list[i].value || '';
  return '';
}
function fileNameFromUrl(url) {
  try {
    var seg = decodeURIComponent(new URL(url).pathname.split('/').pop() || '');
    return /\.[a-z0-9]{2,5}$/i.test(seg) ? seg : '';
  } catch (e) { return ''; }
}
function guessKind(url) {
  var e = urlExt(url);
  if (e === 'm3u8') return 'hls';
  if (e === 'mpd') return 'dash';
  return 'file';
}
function classify(ct, ext) {
  if (ext === 'm3u8' || ct.indexOf('mpegurl') >= 0) return 'hls';
  if (ext === 'mpd' || ct === 'application/dash+xml') return 'dash';
  if (ct === 'video/mp2t' || SEG_EXT.indexOf(ext) >= 0) return null;
  if (ct.indexOf('video/') === 0 || ct.indexOf('audio/') === 0 || ct === 'application/mp4') return 'file';
  if (MEDIA_EXT.indexOf(ext) >= 0 && !/^(text\/|application\/(json|javascript|xml))/.test(ct)) return 'file';
  return null;
}

// ---------- etat par onglet (file d'ecriture serielle) ----------
var chain = Promise.resolve();
function emptyState() { return { items: [], children: [] }; }
function readTab(tabId) {
  var k = 'tab_' + tabId;
  return chrome.storage.session.get(k).then(function (r) { return r[k] || emptyState(); });
}
function withTab(tabId, fn) {
  chain = chain.then(function () {
    var k = 'tab_' + tabId;
    return readTab(tabId).then(function (st) {
      if (fn(st) === false) return;
      var o = {}; o[k] = st;
      return chrome.storage.session.set(o).then(function () { updateBadge(tabId, st); });
    });
  }).catch(function (e) { console.warn('ShowLef withTab', e); });
  return chain;
}
function clearTab(tabId) {
  return withTab(tabId, function (st) { st.items = []; st.children = []; });
}
function updateBadge(tabId, st) {
  var n = st.items.length;
  // programme absent ou trop vieux : « ! » rouge sur l'icone, quel que soit l'onglet (le compte ne servirait a rien)
  if (appInfo.state === 'old' || appInfo.state === 'missing') {
    chrome.action.setBadgeText({ tabId: tabId, text: '!' }).catch(function () { });
    chrome.action.setBadgeBackgroundColor({ tabId: tabId, color: '#d93025' }).catch(function () { });
    chrome.action.setTitle({ tabId: tabId, title: SHOWLEF_I18N.t(appInfo.state === 'old' ? 'fixOldTitle' : 'fixMissingTitle') }).catch(function () { });
    return;
  }
  if (appInfo.update) {   // mise a jour du programme disponible : pastille orange (le compte reste si la page a des medias)
    chrome.action.setBadgeText({ tabId: tabId, text: n ? String(n) : '↑' }).catch(function () { });
    chrome.action.setBadgeBackgroundColor({ tabId: tabId, color: '#e8710a' }).catch(function () { });
    chrome.action.setTitle({ tabId: tabId, title: SHOWLEF_I18N.t('updTitle', { v: SHOWLEF_APP_LATEST }) }).catch(function () { });
    return;
  }
  chrome.action.setBadgeText({ tabId: tabId, text: n ? String(n) : '' }).catch(function () { });
  chrome.action.setBadgeBackgroundColor({ tabId: tabId, color: '#4f46e5' }).catch(function () { });
  // au survol de l'icone, en clair (idee prise chez Stream Recorder) : « 3 medias dans la page »
  chrome.action.setTitle({ tabId: tabId, title: n ? SHOWLEF_I18N.t('badgeTitle', { n: n }) : 'ShowLef' }).catch(function () { });
}
function hasManifest(st) { return st.items.some(function (i) { return i.kind === 'hls' || i.kind === 'dash'; }); }
function dirOf(url) { var k = normUrl(url).split('?')[0]; return k.slice(0, k.lastIndexOf('/') + 1); }
// Morceau de flux : nom en seg/frag/chunk+numero, ou petit fichier range dans le dossier d'un manifeste.
function looksLikeFragment(item, st) {
  if (item.kind !== 'file') return false;
  if (FRAG_RE.test(item.url)) return true;
  if (!(item.size > 0 && item.size < FRAG_MAX_WITH_MANIFEST)) return false;
  var d = dirOf(item.url);
  return st.items.some(function (i) { return i.kind !== 'file' && dirOf(i.url) === d; }) ||
    st.children.some(function (c) { return dirOf(c) === d; });
}
function dropFragments(st) {
  st.items = st.items.filter(function (i) { return !looksLikeFragment(i, st); });
}

chrome.webRequest.onBeforeRequest.addListener(function (d) {
  if (d.tabId >= 0) { clearTab(d.tabId); dropTabJobs(d.tabId); }
}, { urls: ['<all_urls>'], types: ['main_frame'] });
chrome.tabs.onRemoved.addListener(function (tabId) { chrome.storage.session.remove('tab_' + tabId); dropTabJobs(tabId); });

// ---------- suivi des telechargements lances depuis un onglet ----------
// id du programme -> { tabId, frameId, capture } : on interroge `list` chaque seconde (seulement tant qu'il
// reste un telechargement suivi) et on pousse l'etat au cadre qui a lance, qui l'affiche sur son bouton.
// Garde dans storage.session : le service worker peut s'arreter entre deux interrogations.
var jobs = {}, capPending = {}, jobsLoaded = null, polling = false, pollFails = 0;
function loadJobs() {
  if (!jobsLoaded) jobsLoaded = chrome.storage.session.get(['jobs', 'capPending']).then(function (r) {
    jobs = r.jobs || {}; capPending = r.capPending || {};
  }, function () { });
  return jobsLoaded;
}
function saveJobs() { return chrome.storage.session.set({ jobs: jobs, capPending: capPending }).catch(function () { }); }
function pushJob(tabId, frameId, state) {
  if (tabId == null || tabId < 0) return;
  chrome.tabs.sendMessage(tabId, { type: 'job', state: state }, { frameId: frameId || 0 }).catch(function () { });
}
function abortCapture(id) { nativeSend({ cmd: 'pilot_abort', id: id }).catch(function () { }); }
function dropTabJobs(tabId) {
  ownLoad().then(function () { if (ownTabs[tabId]) { delete ownTabs[tabId]; ownSave(); } });
  loadJobs().then(function () {
    var n = 0;
    Object.keys(jobs).forEach(function (id) {
      if (jobs[id].tabId !== tabId) return;
      // capture encore en route (la page envoyait les morceaux) : le programme l'arrete au lieu d'attendre pour rien
      if (jobs[id].capture) abortCapture(id);
      delete jobs[id]; n++;
    });
    if (capPending[tabId]) { delete capPending[tabId]; n++; }
    if (n) saveJobs();
  });
}
// Resultat d'un lancement (sendToApp / startCapture) -> premier etat affiche, puis suivi.
function track(tabId, frameId, res) {
  if (tabId == null || tabId < 0 || !res) return;
  frameId = frameId || 0;
  if (res.ok && res.via === 'capture') {
    loadJobs().then(function () { capPending[tabId] = { frameId: frameId }; saveJobs(); });
    pushJob(tabId, frameId, { status: 'capture', pct: 0 });
    return;
  }
  if (res.ok && res.id) {
    loadJobs().then(function () { jobs[res.id] = { tabId: tabId, frameId: frameId, site: res.site || '', req: res.req }; saveJobs(); startPoll(); });
    pushJob(tabId, frameId, { status: 'queued', id: res.id });
    return;
  }
  if (res.ok) { pushJob(tabId, frameId, { status: res.via === 'chrome' ? 'chrome' : 'sent', bySite: !!res.bySite }); return; }
  if (res.error === 'duplicate') { pushJob(tabId, frameId, { status: 'dup', id: res.id }); return; }
  pushJob(tabId, frameId, { status: 'error', error: res.error });
}
function startPoll() {
  if (polling) return;
  polling = true;
  setTimeout(pollJobs, 600);
}
function pollJobs() {
  loadJobs().then(function () {
    var ids = Object.keys(jobs);
    if (!ids.length) { polling = false; return; }
    return nativeSend({ cmd: 'list' }).then(function (r) {
      if (!r || !r.ok) throw new Error((r && r.error) || 'list');
      pollFails = 0;
      ids.forEach(function (id) {
        var j = jobs[id];
        if (!j) return;
        var d = (r.items || []).find(function (x) { return x.id === id; });
        if (!d) { pushJob(j.tabId, j.frameId, { status: 'gone', id: id }); delete jobs[id]; return; }
        if (j.capture && d.status !== 'error') return;   // pendant la capture, la page envoie sa propre progression
        // le filtre refuse la requete du programme : on refait le travail par le navigateur, une seule fois
        if (d.status === 'error' && d.code === 'blocked' && j.req && !j.capture) { delete jobs[id]; chromeHls(id, j); return; }
        pushJob(j.tabId, j.frameId, { status: d.status, id: d.id, name: d.name, received: d.received, total: d.total,
          speed: d.speed, eta: d.eta, error: d.error, code: d.code });
        if (d.status === 'error') siteFailed(j.site, j.tabId, d.code);
        else if (d.status === 'done') siteOk(j.site);
        if (d.status === 'done' || d.status === 'error') { closeOwn(j.tabId); delete jobs[id]; }
      });
      return saveJobs();
    }).catch(function () {
      // programme injoignable : on reessaie un moment, puis on le dit sur le bouton
      if (++pollFails < 20) return;
      Object.keys(jobs).forEach(function (id) { pushJob(jobs[id].tabId, jobs[id].frameId, { status: 'error', error: 'needApp' }); });
      jobs = {}; pollFails = 0;
      return saveJobs();
    });
  }).then(function () {
    if (!Object.keys(jobs).length) { polling = false; return; }
    setTimeout(pollJobs, 1000);
  }, function () { setTimeout(pollJobs, 1000); });
}
loadJobs().then(function () { if (Object.keys(jobs).length) startPoll(); });

// ---------- « laisser Chrome faire sur ce site » apres 2 echecs (idee prise chez Free Download Manager) ----------
// Un site qui refuse ShowLef deux fois de suite (acces refuse, lien qui expire...) : on propose, dans la page, de
// laisser Chrome telecharger sur ce site. Reversible dans les reglages de la popup (« Sur ce site »).
var CHROME_CODES = ['denied', 'gone', 'generic', 'net'];
function siteFailed(site, tabId, code) {
  if (!site || CHROME_CODES.indexOf(code || 'generic') < 0) return;
  chrome.storage.session.get('siteFails').then(function (r) {
    var f = r.siteFails || {};
    f[site] = (f[site] || 0) + 1;
    return chrome.storage.session.set({ siteFails: f }).then(function () { return f[site]; });
  }).then(function (n) {
    return getSettings().then(function (s) {
      if (n < 2 || (s.siteChrome || []).indexOf(site) >= 0 || tabId == null || tabId < 0) return;
      chrome.tabs.sendMessage(tabId, { type: 'toast', text: SHOWLEF_I18N.t('siteChromeAsk'), ok: false,
        action: { label: SHOWLEF_I18N.t('siteChromeBtn'), msg: { type: 'siteChrome', site: site, on: true } } }, { frameId: 0 }).catch(function () { });
    });
  }).catch(function () { });
}
function siteOk(site) {
  if (!site) return;
  chrome.storage.session.get('siteFails').then(function (r) {
    var f = r.siteFails || {};
    if (!f[site]) return;
    delete f[site];
    return chrome.storage.session.set({ siteFails: f });
  }).catch(function () { });
}
function setSiteChrome(site, on) {
  return chrome.storage.local.get('settings').then(function (r) {
    var st = r.settings || {};
    st.siteChrome = (st.siteChrome || []).filter(function (h) { return h !== site; });
    if (on) st.siteChrome.push(site);
    return chrome.storage.local.set({ settings: st });
  });
}

// ---------- detection ----------
var hlsInFlight = {};

// onResponseStarted (et non onHeadersReceived) : il se declenche AUSSI pour une reponse servie par le
// cache, cas courant d'une video deja vue ou d'une page rechargee.
chrome.webRequest.onResponseStarted.addListener(function (d) {
  try { onHeaders(d); } catch (e) { console.warn('ShowLef onHeaders', e); }
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object'] }, ['responseHeaders']);

// Source directe d'une balise <video>/<audio> signalee par le script de contenu (filet si la requete
// a echappe a webRequest, ex. service worker de la page).
function addReported(tabId, url, isAudio) {
  if (!/^https?:/i.test(url) || showlefIsBlocked(url)) return Promise.resolve();
  var kind = guessKind(url), key = normUrl(url);
  if (kind === 'hls') { processHls(tabId, url, key); return Promise.resolve(); }
  return withTab(tabId, function (st) {
    if (st.children.indexOf(key) >= 0 || st.items.some(function (i) { return i.key === key; })) return false;
    var ext = urlExt(url);
    st.items.push({ id: hashKey(key), key: key, url: url, kind: kind, mime: '', ext: ext, size: 0,
      audio: !!isAudio || AUDIO_EXT.indexOf(ext) >= 0, variants: [], ts: Date.now() });
  });
}

function onHeaders(d) {
  // 304 : media revalide depuis le cache (rechargement de page), a detecter aussi.
  if (d.tabId < 0 || ((d.statusCode < 200 || d.statusCode >= 300) && d.statusCode !== 304)) return;
  var url = d.url;
  if (!/^https?:/i.test(url) || showlefIsBlocked(url)) return;
  var ct = header(d.responseHeaders, 'content-type').toLowerCase().split(';')[0].trim();
  var ext = urlExt(url);
  var kind = classify(ct, ext);
  if (!kind) return;
  var key = normUrl(url);
  var ref = d.initiator && /^https?:/.test(d.initiator) ? d.initiator + '/' : '';

  if (kind === 'hls') { processHls(d.tabId, url, key, d.frameId, ref); return; }
  if (kind === 'dash') {
    withTab(d.tabId, function (st) {
      if (st.items.some(function (i) { return i.key === key; })) return false;
      st.items.push({ id: hashKey(key), key: key, url: url, kind: 'dash', mime: ct, ext: 'mpd', size: 0, audio: false, variants: [], referer: ref, frame: d.frameId, ts: Date.now() });
      dropFragments(st);
    });
    return;
  }

  // Fichier direct : taille par Content-Range (206) ou Content-Length.
  var size = 0, partialTail = false;
  var cr = header(d.responseHeaders, 'content-range');
  if (d.statusCode === 206 && cr) {
    var m = /bytes\s+(\d+)-(\d+)\/(\d+|\*)/i.exec(cr);
    if (m) { if (m[3] !== '*') size = +m[3]; partialTail = +m[1] !== 0; }
  } else {
    size = +header(d.responseHeaders, 'content-length') || 0;
  }
  getSettings().then(function (s) {
    withTab(d.tabId, function (st) {
      var ex = st.items.find(function (i) { return i.key === key; });
      if (ex) { if (size > ex.size) { ex.size = size; return; } return false; }
      if (partialTail) return false;
      if (size > 0 && size < s.minSize * 1024) return false;
      var item = { id: hashKey(key), key: key, url: url, kind: 'file', mime: ct, ext: ext || (ct.split('/')[1] || ''),
        size: size, audio: ct.indexOf('audio/') === 0 || AUDIO_EXT.indexOf(ext) >= 0, variants: [], referer: ref, ts: Date.now() };
      // un morceau de flux (seg_N.mp4...) n'est jamais une video : meme sans manifeste vu (lecture pas encore lancee),
      // il ferait croire a une video de quelques secondes ; l'aide « lancez la lecture » s'affiche alors.
      if (FRAG_RE.test(item.url) || (hasManifest(st) && looksLikeFragment(item, st))) return false;
      st.items.push(item);
    });
  });
}

function attr(line, name) {
  var m = new RegExp('(?:^|[:,])' + name + '=("([^"]*)"|[^,]*)').exec(line);
  return m ? (m[2] !== undefined ? m[2] : m[1]) : '';
}

function parseM3u8(text, base) {
  var lines = text.split(/\r?\n/).map(function (l) { return l.trim(); });
  var res = { master: false, variants: [], audioUris: [], duration: 0, live: true };
  for (var i = 0; i < lines.length; i++) {
    var l = lines[i];
    if (l.indexOf('#EXT-X-STREAM-INF') === 0) {
      res.master = true;
      var uri = '';
      for (var j = i + 1; j < lines.length; j++) { if (lines[j] && lines[j][0] !== '#') { uri = lines[j]; break; } }
      if (!uri) continue;
      var bw = +attr(l, 'AVERAGE-BANDWIDTH') || +attr(l, 'BANDWIDTH') || 0;
      var resol = attr(l, 'RESOLUTION');
      var h = 0, w = 0;
      if (resol) { var p = resol.split('x'); w = +p[0] || 0; h = +p[1] || 0; }
      var abs;
      try { abs = new URL(uri, base).toString(); } catch (e) { continue; }
      res.variants.push({ url: abs, bandwidth: bw, width: w, height: h, codecs: attr(l, 'CODECS'),
        label: h ? h + 'p' : (bw ? Math.round(bw / 1000) + ' kbps' : '?') });
    } else if (l.indexOf('#EXT-X-MEDIA') === 0) {
      var u = attr(l, 'URI');
      if (u) { try { res.audioUris.push(new URL(u, base).toString()); } catch (e) { } }
    } else if (l.indexOf('#EXTINF:') === 0) {
      res.duration += parseFloat(l.slice(8)) || 0;
    } else if (l.indexOf('#EXT-X-ENDLIST') === 0) {
      res.live = false;
    }
  }
  // Doublons (meme URL pour plusieurs codecs) puis tri du meilleur au moins bon.
  var seen = {};
  res.variants = res.variants.filter(function (v) { if (seen[v.url]) return false; seen[v.url] = 1; return true; })
    .sort(function (a, b) { return (b.height - a.height) || (b.bandwidth - a.bandwidth); });
  if (res.master) res.live = false;
  return res;
}

// Lecture d'une liste HLS : par le service worker, sinon par le cadre qui l'a demandee (meme origine,
// meme Referer : les hebergeurs qui refusent une requete sans Referer, comme Bunny, repondent alors).
function fetchPlaylist(tabId, frameId, url) {
  return fetch(url, { credentials: 'include' }).then(function (r) { return r.ok ? r.text() : ''; }).catch(function () { return ''; })
    .then(function (text) {
      if (text && text.indexOf('#EXTM3U') >= 0) return text;
      if (frameId == null || frameId < 0) return '';
      return chrome.tabs.sendMessage(tabId, { type: 'fetchText', url: url }, { frameId: frameId })
        .then(function (r) { return (r && r.text) || ''; }, function () { return ''; });
    });
}

function processHls(tabId, url, key, frameId, ref) {
  var flightKey = tabId + '|' + key;
  if (hlsInFlight[flightKey]) return;
  hlsInFlight[flightKey] = 1;
  readTab(tabId).then(function (st) {
    if (st.children.indexOf(key) >= 0 || st.items.some(function (i) { return i.key === key; })) return;
    return fetchPlaylist(tabId, frameId, url)
      .then(function (text) {
        var info = text && text.indexOf('#EXTM3U') >= 0 ? parseM3u8(text.slice(0, 2000000), url) : null;
        return withTab(tabId, function (st2) {
          if (st2.children.indexOf(key) >= 0 || st2.items.some(function (i) { return i.key === key; })) return false;
          var item = { id: hashKey(key), key: key, url: url, kind: 'hls', mime: 'application/vnd.apple.mpegurl', ext: 'm3u8',
            size: 0, audio: false, variants: [], duration: 0, live: false, referer: ref || '', frame: frameId == null ? null : frameId, ts: Date.now() };
          if (info && info.master) {
            item.variants = info.variants;
            // Flux audio seul : aucune variante n'a d'image ni de codec video declare.
            item.audio = info.variants.length > 0 && info.variants.every(function (v) {
              return !v.height && !!v.codecs && !/avc|hvc|hev|vp0?9|av01/i.test(v.codecs);
            });
            var kids = info.variants.map(function (v) { return normUrl(v.url); }).concat(info.audioUris.map(normUrl));
            kids.forEach(function (k) { if (st2.children.indexOf(k) < 0) st2.children.push(k); });
            st2.items = st2.items.filter(function (i) { return kids.indexOf(i.key) < 0; });
          } else if (info) {
            item.duration = info.duration;
            item.live = info.live;
          }
          st2.items.push(item);
          dropFragments(st2);
        });
      });
  }).finally(function () { delete hlsInFlight[flightKey]; });
}

// ---------- relais vers le programme ----------
function nativeSend(msg) {
  return new Promise(function (resolve, reject) {
    try {
      chrome.runtime.sendNativeMessage(HOST, msg, function (r) {
        var e = chrome.runtime.lastError;
        if (e) reject(new Error(e.message || 'native'));
        else resolve(r || { ok: false, error: 'empty' });
      });
    } catch (e) { reject(e); }
  });
}

// ---------- programme present ET a la bonne version ? ----------
// Une extension a jour face a un programme ancien echouait au clic sans rien dire (MATEBOOK 20/10 : extension 0.7.10,
// programme 0.7.3, « ytprobe » inconnu). Maintenant on le sait AVANT : bouton « a reparer » sur la video, « ! » sur
// l'icone, et tout telechargement ouvre la page de reparation (welcome.html?fix=1) au lieu de partir dans le mur.
// Cache court : 15 s quand tout va bien, 2 s sinon (la page de reparation attend l'installation).
var appInfo = { state: 'unknown', version: '', at: 0 }, appP = null;
function appCheck(force) {
  var ttl = appInfo.state === 'ok' ? 15000 : 2000;
  if (!force && appInfo.at && Date.now() - appInfo.at < ttl) return Promise.resolve(appInfo);
  if (appP) return appP;
  appP = nativeSend({ cmd: 'ping' }).then(function (r) { return r; }, function () { return null; }).then(function (r) {
    var before = appInfo.state;
    var beforeUpd = appInfo.update;
    appInfo = { state: showlefAppState(r), update: showlefAppUpdate(r), version: (r && r.ok && r.version) || '', at: Date.now() };
    if (beforeUpd !== appInfo.update) refreshAllBadges();
    if (appInfo.state === 'ok') { pilotSync(); wakeLoop(); }
    appP = null;
    if (before !== appInfo.state) refreshAllBadges();
    return appInfo;
  });
  return appP;
}
function refreshAllBadges() {
  chrome.tabs.query({}).then(function (tabs) {
    tabs.forEach(function (t) { readTab(t.id).then(function (st) { updateBadge(t.id, st); }, function () { }); });
  }).catch(function () { });
}
// Une seule page de reparation : on remet au premier plan celle qui est deja ouverte.
function openFix(update, auto) {
  var url = chrome.runtime.getURL('welcome.html');
  // auto : l'utilisateur a deja clique « Mettre a jour » (menu, popup) => la page lance la mise a jour tout de suite
  var full = url + '?fix=1' + (update ? '&update=1' : '') + (auto ? '&auto=1' : '');
  var create = function () { return chrome.tabs.create({ url: full }); };
  return chrome.tabs.query({ url: url + '*' }).then(function (tabs) {
    if (!tabs || !tabs[0]) return create();
    if (auto) return chrome.tabs.update(tabs[0].id, { url: full, active: true });
    return chrome.tabs.update(tabs[0].id, { active: true }).then(function () {
      return chrome.windows.update(tabs[0].windowId, { focused: true }).catch(function () { });
    });
  }).catch(create);
}
// Garde de tous les telechargements : programme pas pret = page de reparation, et le bouton dit « a reparer ».
function needFix() {
  return appCheck().then(function (a) {
    if (a.state === 'ok') return null;
    openFix();
    return { ok: false, error: a.state === 'old' ? 'needUpdate' : 'needApp', fix: true };
  });
}

// ---------- morceaux YouTube : connexion PERSISTANTE au programme (v0.7.13) ----------
// sendNativeMessage lance un hote par message : avec des morceaux audio de ~150 Ko, ce lancement plafonnait le MP3
// direct a ~1 Mo/s. Programme >= 0.7.11 : l'hote boucle, une seule connexion pour toute la capture, reponses dans
// l'ordre. Programme plus ancien (hote a un seul message) : on reste sur un lancement par message.
var segPort = null, segWait = [];
function segSend(msg) {
  if (!appInfo.version || showlefVerCmp(appInfo.version, '0.7.11') < 0) return nativeSend(msg);
  return new Promise(function (resolve, reject) {
    if (!segPort) {
      try { segPort = chrome.runtime.connectNative(HOST); } catch (e) { nativeSend(msg).then(resolve, reject); return; }
      var port = segPort;
      port.onMessage.addListener(function (r) { var w = segWait.shift(); if (w) w.resolve(r || { ok: false, error: 'empty' }); });
      port.onDisconnect.addListener(function () {
        void chrome.runtime.lastError;
        if (segPort === port) segPort = null;
        // ce qui attendait repart par l'ancien chemin (le programme ignore un morceau deja recu)
        var ws = segWait; segWait = [];
        ws.forEach(function (w) { nativeSend(w.msg).then(w.resolve, w.reject); });
      });
    }
    segWait.push({ msg: msg, resolve: resolve, reject: reject });
    segPort.postMessage(msg);
  });
}
function segClose() {
  // la fin de capture passe par sendNativeMessage : on attend que tous les morceaux soient confirmes avant de fermer
  if (segPort && !segWait.length) { try { segPort.disconnect(); } catch (e) { } segPort = null; }
}

function cookiesFor(url) {
  return chrome.cookies.getAll({ url: url }).then(function (list) {
    return list.map(function (c) {
      return { name: c.name, value: c.value, domain: c.domain, path: c.path, secure: c.secure,
        expirationDate: c.expirationDate || 0, hostOnly: c.hostOnly };
    });
  }).catch(function () { return []; });
}

// ---------- vignette envoyee au programme (champ optionnel `thumb` de add) ----------
// Le cadre qui a lance donne l'affiche (poster) de la <video>, puis la page principale son og:image / twitter:image.
// Adresse http(s) seulement, jamais data: ni blob:. (Les medias pilotes par le programme ont la vignette qu'il donne.)
function cleanThumb(t) { return typeof t === 'string' && /^https?:\/\//i.test(t) && t.length < 2048 ? t : ''; }
function askThumb(tabId, frameId, mediaUrl) {
  var q = chrome.tabs.sendMessage(tabId, { type: 'getThumb', mediaUrl: mediaUrl || '' }, { frameId: frameId || 0 })
    .then(function (r) { return cleanThumb(r && r.thumb); }, function () { return ''; });
  return Promise.race([q, new Promise(function (ok) { setTimeout(function () { ok(''); }, 600); })]);
}
function thumbFor(tabId, frameId, mediaUrl) {
  if (tabId == null || tabId < 0) return Promise.resolve('');
  return askThumb(tabId, frameId, mediaUrl).then(function (t) { return t || (frameId ? askThumb(tabId, 0, mediaUrl) : ''); });
}

// opts : { url, kind, audioOnly, variantUrl, quality, filename, referer, title, ask, frameId }
// Point de passage de TOUS les envois au programme (bouton, menu, clic droit, raccourci, interception, popup) :
// la garde de version se pose ICI, pas dans chaque appelant.
function sendToApp(tabId, opts) {
  return needFix().then(function (bad) { return bad || sendToAppRaw(tabId, opts); });
}
function sendToAppRaw(tabId, opts) {
  return Promise.all([
    getSettings(),
    tabId >= 0 ? chrome.tabs.get(tabId).catch(function () { return null; }) : Promise.resolve(null),
    cookiesFor(opts.url)
  ]).then(function (r) {
    return thumbFor(tabId, opts.frameId || 0, opts.url).then(function (th) { r.push(th); return r; });
  }).then(function (r) {
    var s = r[0], tab = r[1], cookies = r[2], thumb = r[3];
    var pageUrl = (tab && tab.url) || opts.referer || '';
    if (showlefIsBlocked(opts.url) || (opts.kind === 'page' && showlefIsBlocked(pageUrl))) return { ok: false, error: 'blocked' };
    var site = showlefSiteKey(pageUrl || opts.url);
    // « laisser Chrome faire sur ce site » : un fichier direct part par Chrome, sans passer par le programme
    if ((opts.kind || 'file') === 'file' && !opts.audioOnly && !opts.noChrome && showlefSiteIn(s.siteChrome, pageUrl || opts.url)) {
      return chrome.downloads.download({ url: opts.url }).then(function () { return { ok: true, via: 'chrome', bySite: true }; },
        function (e) { return { ok: false, error: String(e && e.message || e) }; });
    }
    var msg = {
      cmd: 'add', url: opts.url, kind: opts.kind || 'file',
      title: opts.title || (tab && tab.title) || '',
      // nom explicite seulement : sinon le programme choisit (nom de l'adresse, ou titre de la page si
      // l'adresse ne dit rien, ex. « video.mp4 »), et le raccourcit
      filename: opts.filename || '',
      referer: opts.referer || pageUrl, pageUrl: pageUrl, userAgent: navigator.userAgent, cookies: cookies,
      quality: showlefQualityNum(opts.quality || s.quality),
      audioOnly: !!opts.audioOnly || showlefSiteIn(s.siteAudio, pageUrl),   // « toujours audio » sur ce site
      audioFormat: s.audioFormat,
      ask: !!opts.ask || !!s.ask
    };
    if (opts.variantUrl) msg.variantUrl = opts.variantUrl;
    if (thumb) msg.thumb = thumb;
    return nativeSend(msg).then(function (resp) {
      // req : de quoi refaire le telechargement par le navigateur si le filtre refuse celui du programme (chromeHls)
      if (resp && resp.ok) return { ok: true, via: 'app', id: resp.id, site: site, req: { url: opts.url, kind: msg.kind, quality: msg.quality, audioOnly: msg.audioOnly, title: msg.title, pageUrl: pageUrl } };
      if (resp && resp.error === 'duplicate') return { ok: false, error: 'duplicate', id: resp.id };
      return { ok: false, error: (resp && resp.error) || 'app' };
    }, function () {
      // Programme injoignable : un fichier direct part quand meme par Chrome.
      if (msg.kind === 'file' && !msg.audioOnly) {
        return chrome.downloads.download({ url: msg.url }).then(function () { return { ok: true, via: 'chrome' }; },
          function (e) { return { ok: false, error: String(e && e.message || e) }; });
      }
      return { ok: false, error: 'needApp' };
    });
  });
}

function resultText(res) {
  if (res.ok && res.via === 'capture') return SHOWLEF_I18N.t('capStarted');
  if (res.ok) return SHOWLEF_I18N.t(res.via === 'chrome' ? (res.bySite ? 'sentChromeSite' : 'sentChrome') : 'sent');
  if (res.error === 'needApp' || res.error === 'needUpdate' || res.error === 'blocked' || res.error === 'duplicate') return SHOWLEF_I18N.t(res.error);
  return SHOWLEF_I18N.t('error', { e: SHOWLEF_I18N.humanErr(res.error) });
}
function toast(tabId, res) {
  if (tabId < 0) return;
  chrome.tabs.sendMessage(tabId, { type: 'toast', text: resultText(res), ok: !!res.ok }, { frameId: 0 }).catch(function () { });
}

// ---------- messages de la popup et du script de contenu ----------
chrome.runtime.onMessage.addListener(function (m, sender, reply) {
  handle(m, sender).then(reply, function (e) { reply({ ok: false, error: String(e && e.message || e) }); });
  return true;
});

function handle(m, sender) {
  var tabId = m.tabId != null ? m.tabId : (sender.tab ? sender.tab.id : -1);
  // cadre qui a lance (bouton sur une video dans un iframe) ; depuis la popup : la page principale
  var frameId = sender.tab ? (sender.frameId || 0) : 0;
  switch (m.type) {
    case 'getMedia':
      return Promise.all([readTab(tabId), getSettings(), appCheck()]).then(function (r) {
        return { ok: true, items: r[0].items, settings: r[1], store: STORE_BUILD, app: r[2].state, appUpdate: !!r[2].update };
      });
    case 'appState':
      return appCheck(!!m.force);
    case 'openFix':
      return openFix(!!m.update, !!m.auto).then(function () { return { ok: true }; });
    case 'download':
    case 'downloadUrl':
    case 'downloadPage':
      return handleDownload(m, tabId, frameId);
    case 'native':
      // revue Codex 20/10 : un script de contenu vit dans une page web et ses messages sont moins surs. Depuis un
      // onglet, seules les deux commandes du bouton passent (ouvrir un fichier fini, montrer le programme) ; le
      // reste (mise a jour, reglages, suppression...) n'est accepte que des pages de l'extension (popup, accueil).
      if (sender.tab) {
        var c = m.msg || {};
        if (!(c.cmd === 'show' || (c.cmd === 'control' && c.action === 'open' && typeof c.id === 'string'))) return Promise.resolve({ ok: false, error: 'forbidden' });
        m.msg = c.cmd === 'show' ? { cmd: 'show' } : { cmd: 'control', id: c.id, action: 'open' };
      }
      return nativeSend(m.msg).catch(function (e) { return { ok: false, error: 'noApp', detail: e.message }; });
  }
  return handleMisc(m, tabId, sender);
}
function handleDownload(m, tabId, frameId) {
  switch (m.type) {
    case 'download':
      return readTab(tabId).then(function (st) {
        var it = st.items.find(function (i) { return i.id === m.itemId; });
        if (!it) return { ok: false, error: 'gone' };
        if (it.kind === 'site') return siteDownload(tabId, m.variantUrl, m.audioOnly, frameId);
        return sendToApp(tabId, { url: it.url, kind: it.kind, referer: it.referer || undefined, variantUrl: m.variantUrl, audioOnly: m.audioOnly,
          quality: m.quality, frameId: frameId, filename: m.filename, title: m.title });
      }).then(function (res) { if (m.toast) toast(tabId, res); track(tabId, frameId, res); return res; });
    case 'downloadUrl':
      return sendToApp(tabId, { url: m.url, kind: m.kind || guessKind(m.url), audioOnly: m.audioOnly, frameId: frameId })
        .then(function (res) { if (m.toast) toast(tabId, res); track(tabId, frameId, res); return res; });
    case 'downloadPage':
      return chrome.tabs.get(tabId).then(function (tab) {
        return sendToApp(tabId, { url: tab.url, kind: 'page', audioOnly: m.audioOnly });
      }).then(function (res) { if (m.toast) toast(tabId, res); track(tabId, frameId, res); return res; });
  }
}
function handleMisc(m, tabId, sender) {
  switch (m.type) {
    case 'reportSrc':
      return addReported(tabId, m.url, m.audio).then(function () { return { ok: true }; });
    // ---------- sites pilotes par le programme (v0.7.22) : bridge.js relaie, le programme decide ----------
    case 'pilotData':
      pilotStats.data++;
      if (tabId < 0 || !sender.tab) return Promise.resolve({ ok: false });
      return getSettings().then(function (s) {
        return nativeSend({ cmd: 'pilot_data', page: sender.tab.url, rule: m.rule, url: m.url, body: m.body, compat: !!s.compat, hint: m.hint || '' });
      }).then(function (r) { return setMedia(tabId, r && r.media).then(function () { return { ok: !!(r && r.ok) }; }); },
        function () { return { ok: false }; });
    case 'pilotPage':
      if (tabId < 0 || !sender.tab) return Promise.resolve({ ok: false });
      return getSettings().then(function (s) {
        return nativeSend({ cmd: 'pilot_page', page: sender.tab.url, compat: !!s.compat, allowFetch: !!m.allowFetch });
      }).then(function (r) { return setMedia(tabId, r && r.media).then(function () { return r || { ok: false }; }); },
        function () { return { ok: false }; });
    case 'pilotStart':
      return pilotStart(tabId, sender, m);
    case 'playlistStart':
      // seulement depuis la popup (page de l'extension) : un script de contenu ne peut pas lancer une playlist
      if (sender.tab) return Promise.resolve({ ok: false, error: 'forbidden' });
      return playlistStart(m.tabId);
    case 'pilotStep':
      // erreur de transport : rendue au relais, qui continue ses battements (le programme redemande les actions perdues)
      return segSend({ cmd: 'pilot_step', id: m.id, results: m.results || [] })
        .catch(function (e) { return { ok: false, error: 'app', transport: true, detail: e.message }; })
        .then(function (r) { pilotReply(tabId, r); return r; });
    case 'pilotFail':
      abortCapture(m.id);
      pilotReply(tabId, { ok: false, error: m.error || 'app' });
      return Promise.resolve({ ok: true });
    case 'pilotAbort':
      segClose();
      return nativeSend({ cmd: 'pilot_abort', id: m.id }).catch(function () { return { ok: false }; });
    case 'clear':
      return clearTab(tabId).then(function () { return { ok: true }; });
    case 'asset':
      return asset(m.path, m.text);
    case 'siteChrome':
      return setSiteChrome(m.site, !!m.on).then(function () {
        if (m.on && tabId >= 0) chrome.tabs.sendMessage(tabId, { type: 'toast', text: SHOWLEF_I18N.t('siteChromeDone'), ok: true }, { frameId: 0 }).catch(function () { });
        return { ok: true };
      });
    case 'rescan':
      // « Chercher a nouveau » : chaque cadre de la page re-signale ses <video>/<audio>, puis on relit la liste
      return clearTab(tabId).then(function () {
        return chrome.tabs.sendMessage(tabId, { type: 'rescan' }).catch(function () { });
      }).then(function () {
        return new Promise(function (ok) { setTimeout(ok, 1500); });
      }).then(function () { return readTab(tabId); }).then(function (st) { return { ok: true, n: st.items.length }; });
  }
  return Promise.resolve({ ok: false, error: 'unknown' });
}

// ---------- polices et feuille du bouton, lues ici pour le script de contenu (jamais chargees par la page) ----------
var ASSETS = ['content.css', 'fonts/rubik-hebrew.woff2', 'fonts/rubik-latin.woff2', 'fonts/fredoka-hebrew.woff2', 'fonts/fredoka-latin.woff2'];
var assetCache = {};
function asset(path, text) {
  if (ASSETS.indexOf(path) < 0) return Promise.resolve({ ok: false, error: 'asset' });
  if (assetCache[path]) return Promise.resolve({ ok: true, data: assetCache[path] });
  return fetch(chrome.runtime.getURL(path)).then(function (r) { return text ? r.text() : r.arrayBuffer(); }).then(function (d) {
    if (!text) {
      var b = new Uint8Array(d), s = '';
      for (var i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));
      d = btoa(s);
    }
    assetCache[path] = d;
    return { ok: true, data: d };
  });
}

// ---------- regles des sites, donnees par le PROGRAMME (v0.7.22, « comme IDM ») ----------
// L'extension ne connait aucun site. Le programme lui donne : les hotes ou poser hook.js (capture generique, monde
// de la page) et bridge.js (relais, monde isole), ce qu'il faut y capturer, et les hotes vers lesquels il pourra
// demander des requetes. Sans programme (ou programme < 0.7.16) : rien n'est enregistre.
var pilotAt = 0;
function validHost(h) { return typeof h === 'string' && /^[a-z0-9-]+(\.[a-z0-9-]+)+$/.test(h); }
function pilotSites(r) {
  return (r && r.ok && Array.isArray(r.sites) ? r.sites : []).filter(function (x) {
    return x && Array.isArray(x.hosts) && x.hosts.length && x.hosts.every(validHost) && (x.fetchHosts || []).every(validHost)
      && !x.hosts.some(function (h) { return showlefIsBlocked('https://' + h + '/'); });   // version Store de secours
  });
}
function pilotSync(force) {
  if (!force && pilotAt && Date.now() - pilotAt < 600000) return Promise.resolve();
  pilotAt = Date.now();
  return nativeSend({ cmd: 'pilot_rules' }).then(function (r) {
    if (!r) return;
    var sites = r.ok ? pilotSites(r) : [];   // programme trop ancien (« unknown cmd ») : aucun site, anciennes regles retirees
    return chrome.storage.local.set({ pilotRules: { sites: sites, at: Date.now() } }).then(function () { return registerPilot(sites); });
  }, function () { pilotAt = 0; });
}
function hostMatches(hosts) {
  var m = [];
  hosts.forEach(function (h) { m.push('*://' + h + '/*', '*://*.' + h + '/*'); });
  return m;
}
// enregistrement dynamique : seulement si les hotes ont change (persistant entre deux lancements de Chrome)
function registerScripts(id, matches, def) {
  return chrome.scripting.getRegisteredContentScripts({ ids: [id] }).catch(function () { return []; }).then(function (cur) {
    var same = cur.length && JSON.stringify((cur[0].matches || []).slice().sort()) === JSON.stringify(matches.slice().sort())
      && JSON.stringify(cur[0].js || []) === JSON.stringify(def.js) && (cur[0].world || 'ISOLATED') === (def.world || 'ISOLATED')
      && (cur[0].runAt || '') === (def.runAt || '') && !!cur[0].allFrames === !!def.allFrames;
    if (same || (!cur.length && !matches.length)) return;
    return (cur.length ? chrome.scripting.unregisterContentScripts({ ids: [id] }) : Promise.resolve()).then(function () {
      if (!matches.length) return;
      return chrome.scripting.registerContentScripts([Object.assign({ id: id, matches: matches, persistAcrossSessions: true }, def)]);
    });
  }).catch(function (e) { console.warn('ShowLef scripts', id, e); });
}
function registerPilot(sites) {
  var hosts = [];
  sites.forEach(function (x) { x.hosts.forEach(function (h) { if (hosts.indexOf(h) < 0) hosts.push(h); }); });
  var m = hostMatches(hosts);
  return Promise.all([
    registerScripts('showlef-hook', m, { js: ['hook.js'], runAt: 'document_start', world: 'MAIN' }),
    registerScripts('showlef-bridge', m, { js: ['config.js', 'bridge.js'], runAt: 'document_start' })
  ]).then(function () { return getSettings().then(syncCompat); });
}
// « format compatible » (H.264 + AAC) : script de page enregistre seulement si le reglage est actif, sur les sites
// que le programme marque « compat »
function syncCompat(s) {
  return chrome.storage.local.get('pilotRules').then(function (r) {
    var hosts = [];
    if (s && s.compat) ((r.pilotRules && r.pilotRules.sites) || []).forEach(function (x) { if (x.compat) hosts = hosts.concat(x.hosts); });
    return registerScripts('showlef-h264', hostMatches(hosts), { js: ['compat.js'], runAt: 'document_start', world: 'MAIN', allFrames: true });
  });
}
chrome.storage.onChanged.addListener(function (ch, area) {
  if (area === 'local' && ch.settings) syncCompat(ch.settings.newValue || {});
});

// Media annonce par le programme pour cet onglet (popup, bouton sur la video)
function setMedia(tabId, md) {
  if (!md || typeof md.key !== 'string' || tabId < 0) return Promise.resolve();
  return withTab(tabId, function (st) {
    st.items = st.items.filter(function (i) { return i.kind !== 'site'; });
    st.items.unshift({ id: 'site-' + md.key, key: md.key, url: '', kind: 'site', site: String(md.site || ''), ext: 'mp4',
      title: String(md.title || ''), duration: +md.duration || 0, live: !!md.live, size: +md.size || 0, thumb: md.thumb || '',
      variants: (md.variants || []).map(function (v) { return { label: String(v.label), url: String(v.url), height: +v.height || 0, size: +v.size || 0 }; }),
      resume: md.resume && (md.resume.video || md.resume.audio) ? { video: +md.resume.video || 0, audio: +md.resume.audio || 0 } : undefined });
  });
}

// ---------- telechargement d'un media pilote par le programme ----------
// Le bouton demande au relais de l'onglet de commencer ; le relais demande au programme (pilotStart) puis execute ses
// requetes (pilotStep). Le programme essaie d'abord yt-dlp si le reseau le permet, sinon il pilote le telechargement.
function siteDownload(tabId, quality, audioOnly, frameId) {
  return needFix().then(function (bad) {
    if (bad) return bad;
    return getSettings().then(function (s) {
      return chrome.tabs.sendMessage(tabId, { type: 'pilotGo', quality: quality || s.quality, audioOnly: !!audioOnly }, { frameId: 0 });
    }).then(function (resp) {
      return resp && resp.ok ? { ok: true, via: 'capture' } : { ok: false, error: SHOWLEF_I18N.t('capErr_' + ((resp && resp.error) || 'noPlayer'), { p: 0 }) };
    }, function () { return { ok: false, error: SHOWLEF_I18N.t('capErr_noPlayer') }; });
  });
}
// banc de mesure seulement (jamais pose par l'extension) : pilotTune = { par } force le nombre de requetes en vol
var pilotTune = null, pilotStats = { data: 0 };   // pilotStats : compteur lu par les bancs (tests/attack.mjs)
function pilotStart(tabId, sender, m) {
  if (tabId < 0 || !sender.tab) return Promise.resolve({ ok: false, error: 'notMedia' });
  var tab = sender.tab;
  return Promise.all([getSettings(), cookiesFor(tab.url)]).then(function (r) {
    var s = r[0];
    return nativeSend({ cmd: 'pilot_start', page: tab.url, pageUrl: tab.url, referer: tab.url, title: tab.title || '',
      quality: showlefQualityNum(m.quality || s.quality), audioOnly: !!m.audioOnly || showlefSiteIn(s.siteAudio, tab.url),
      audioFormat: s.audioFormat, compat: !!s.compat, cookies: r[1], userAgent: navigator.userAgent, par: pilotTune && pilotTune.par });
  }).catch(function (e) { return { ok: false, error: 'app', detail: e.message }; }).then(function (r) {
    if (r && r.ok && r.via === 'app') {   // yt-dlp direct : suivi comme un telechargement ordinaire
      return loadJobs().then(function () {
        var cp = capPending[tabId] || { frameId: 0 };
        delete capPending[tabId]; saveJobs();
        track(tabId, cp.frameId, { ok: true, id: r.id });
        return r;
      });
    }
    pilotReply(tabId, r);
    return r;
  });
}
// Reponse du programme pendant un telechargement pilote : suivi, progression sur le bouton, fin, echec.
function pilotReply(tabId, r) {
  if (r && (r.stale || r.transport)) return;   // pas parti avant la fin / erreur de transport (le relais decide)
  loadJobs().then(function () {
    var cp = capPending[tabId] || { frameId: 0 };
    if (!r || !r.ok) {
      segClose();
      var key = r && r.error ? r.error : 'app';
      if (key === 'unknown id') key = 'cancelled';
      var text = SHOWLEF_I18N.t('capErr_' + key, { p: (r && r.pct) || 0 });
      if (!text || text === 'capErr_' + key) text = SHOWLEF_I18N.t('capErr_app');
      chrome.tabs.sendMessage(tabId, { type: 'toast', text: text, ok: false }, { frameId: 0 }).catch(function () { });
      delete capPending[tabId];
      Object.keys(jobs).forEach(function (id) { if (jobs[id].tabId === tabId && jobs[id].capture) delete jobs[id]; });
      saveJobs();
      pushJob(tabId, cp.frameId, { status: 'error', error: text });
      return;
    }
    var id = r.id || '';
    var item = /^\d/.test(id);   // id provisoire « s... » tant que le programme prepare ; puis id de l'element ShowLef
    if (item && !jobs[id] && !r.done) {
      chrome.tabs.get(tabId).catch(function () { abortCapture(id); });   // onglet ferme pendant le demarrage
      jobs[id] = { tabId: tabId, frameId: cp.frameId, capture: true };
      // un nouveau telechargement de ce media : plus de « Reprendre » sur le bouton
      withTab(tabId, function (st) {
        var ch = false;
        st.items.forEach(function (i) { if (i.kind === 'site' && i.resume) { delete i.resume; ch = true; } });
        if (!ch) return false;
      });
      saveJobs(); startPoll();
    }
    if (r.done) {
      segClose();
      delete capPending[tabId];
      if (jobs[id]) jobs[id].capture = false;
      saveJobs(); startPoll();
      pushJob(tabId, cp.frameId, { status: 'merging', id: id });
    } else if (r.pct != null) pushJob(tabId, cp.frameId, { status: 'capture', pct: r.pct, direct: true });
  });
}

// ---------- telechargement HLS par le NAVIGATEUR (v0.7.30, programme >= 0.7.52) ----------
// Un filtre (NetFree) peut refuser la requete du programme (yt-dlp : « HTTP Error 418 ») alors que le lecteur de la page
// passe. Le programme le dit par le code `blocked` ; on refait alors le travail avec la pile reseau de Chrome : listes et
// morceaux sont lus par le cadre de la page (meme origine, meme Referer), puis confies au programme (`capture_seg`), qui
// assemble avec ffmpeg sans reencoder. Chiffre (AES), direct et morceaux en plage d'octets : pas pris en charge (message).
function b64Bytes(b64) { var s = atob(b64), b = new Uint8Array(s.length); for (var i = 0; i < s.length; i++) b[i] = s.charCodeAt(i); return b; }
function b64Text(b64) { return new TextDecoder().decode(b64Bytes(b64)); }
function bufB64(buf) {
  var b = new Uint8Array(buf), s = '';
  for (var i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));
  return btoa(s);
}
// base64 de l'adresse : par le cadre de la page d'abord, sinon par le service worker
function chromeGet(tabId, frameId, url) {
  var viaFrame = frameId == null ? Promise.reject(new Error('frame'))
    : chrome.tabs.sendMessage(tabId, { type: 'fetchBin', url: url }, { frameId: frameId }).then(function (r) { if (r && r.ok) return r.b64; throw new Error((r && r.error) || 'frame'); });
  return viaFrame.catch(function () {
    return fetch(url, { credentials: 'include' }).then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); }).then(bufB64);
  });
}
function chromeGetRetry(tabId, frameId, url, n) {
  return chromeGet(tabId, frameId, url).catch(function (e) {
    if (n <= 1 || /HTTP 4(03|18)/.test(String(e && e.message))) throw e;
    return new Promise(function (ok) { setTimeout(ok, 800); }).then(function () { return chromeGetRetry(tabId, frameId, url, n - 1); });
  });
}
function hlsAbs(u, base) { return new URL(u, base).toString(); }
// master -> { video, audio } (adresses des listes) ; liste simple -> { video: elle-meme }
function hlsChoose(text, base, quality, audioOnly) {
  var lines = text.split(/\r?\n/).map(function (l) { return l.trim(); });
  var vars = [], audios = {};
  for (var i = 0; i < lines.length; i++) {
    var l = lines[i];
    if (l.indexOf('#EXT-X-MEDIA') === 0 && attr(l, 'TYPE') === 'AUDIO' && attr(l, 'URI')) {
      var g = attr(l, 'GROUP-ID');
      if (!audios[g] || attr(l, 'DEFAULT') === 'YES') audios[g] = hlsAbs(attr(l, 'URI'), base);
    } else if (l.indexOf('#EXT-X-STREAM-INF') === 0) {
      var j = i + 1; while (j < lines.length && (!lines[j] || lines[j][0] === '#')) j++;
      if (j >= lines.length) continue;
      var p = (attr(l, 'RESOLUTION') || '').split('x');
      vars.push({ url: hlsAbs(lines[j], base), bw: +attr(l, 'AVERAGE-BANDWIDTH') || +attr(l, 'BANDWIDTH') || 0, h: +p[1] || 0, ag: attr(l, 'AUDIO') });
    }
  }
  if (!vars.length) return { video: base, audio: '' };
  var cap = +quality || 0, pick;
  var hs = vars.filter(function (v) { return v.h > 0; }).sort(function (a, b) { return (b.h - a.h) || (b.bw - a.bw); });
  if (audioOnly) pick = vars.slice().sort(function (a, b) { return a.bw - b.bw; })[0];
  else if (!hs.length) pick = vars.slice().sort(function (a, b) { return b.bw - a.bw; })[0];
  else if (!cap) pick = hs[0];
  else pick = hs.filter(function (v) { return v.h <= cap; })[0] || hs[hs.length - 1];
  var au = pick.ag && audios[pick.ag] ? audios[pick.ag] : '';
  return audioOnly && au ? { video: '', audio: au } : { video: pick.url, audio: au };
}
function hlsParse(text, base) {
  var r = { init: '', segs: [], ended: false, bad: '' };
  text.split(/\r?\n/).forEach(function (raw) {
    var l = raw.trim();
    if (!l) return;
    if (l[0] !== '#') { r.segs.push(hlsAbs(l, base)); return; }
    if (l.indexOf('#EXT-X-MAP') === 0) { r.init = hlsAbs(attr(l, 'URI'), base); if (attr(l, 'BYTERANGE')) r.bad = 'range'; }
    else if (l.indexOf('#EXT-X-KEY') === 0) { var m = attr(l, 'METHOD'); if (m && m !== 'NONE') r.bad = 'crypt'; }
    else if (l.indexOf('#EXT-X-BYTERANGE') === 0) r.bad = 'range';
    else if (l.indexOf('#EXT-X-ENDLIST') === 0) r.ended = true;
  });
  if (!r.bad && !r.ended) r.bad = 'live';
  if (!r.bad && !r.segs.length) r.bad = 'empty';
  return r;
}
// manifeste a refaire par le navigateur : la liste HLS du telechargement, ou (DASH Cloudflare Stream) sa jumelle .m3u8
function hlsTwin(url) { var t = url.replace(/\/manifest\/video\.mpd(\?.*)?$/, '/manifest/video.m3u8$1'); return t !== url ? t : ''; }
function hlsManifestFor(j) {
  var req = j.req;
  return readTab(j.tabId).then(function (st) {
    var frameOf = function (url) {
      var h = ''; try { h = new URL(url).host; } catch (e) { }
      var it = st.items.find(function (i) { return i.frame != null && (i.key === normUrl(url) || (h && i.url.indexOf('//' + h + '/') > 0)); });
      return it ? it.frame : null;
    };
    var url = req.kind === 'hls' ? req.url : (req.kind === 'dash' ? hlsTwin(req.url) : '');
    if (!url) {
      var h = st.items.find(function (i) { return i.kind === 'hls' && !i.audio; });
      if (h) url = h.url;
      else { var d = st.items.find(function (i) { return i.kind === 'dash' && hlsTwin(i.url); }); if (d) url = hlsTwin(d.url); }
    }
    return url ? { url: url, frame: frameOf(url) } : null;
  });
}
function chromeHls(oldId, j) {
  var req = j.req, tabId = j.tabId, frameId = j.frameId, appId = '', stopped = false;
  var alive = function () { if (appId && !jobs[appId]) stopped = true; return !stopped; };
  var quit = function (e) {
    var msg = String(e && e.message || e);
    if (appId) { nativeSend({ cmd: 'capture_abort', id: appId }).catch(function () { }); delete jobs[appId]; saveJobs(); }
    segClose();
    if (stopped) return;   // onglet ferme
    console.warn('ShowLef chromeHls', msg);
    pushJob(tabId, frameId, { status: 'error', error: SHOWLEF_I18N.t(/HTTP 4(03|18)/.test(msg) ? 'errBlocked' : 'errGeneric') });
  };
  pushJob(tabId, frameId, { status: 'capture', pct: 0, direct: true });
  return Promise.all([hlsManifestFor(j), getSettings(), chrome.tabs.get(tabId).catch(function () { return null; })]).then(function (r) {
    var man = r[0], s = r[1], tab = r[2];
    if (!man) throw new Error('no manifest');
    return chromeGetRetry(tabId, man.frame, man.url, 3).then(function (b64) {
      var ch = hlsChoose(b64Text(b64), man.url, req.quality, req.audioOnly);
      return Promise.all([ch.video ? chromeGetRetry(tabId, man.frame, ch.video, 3) : '', ch.audio ? chromeGetRetry(tabId, man.frame, ch.audio, 3) : ''])
        .then(function (pl) {
          var tracks = [];
          if (pl[0]) tracks.push({ itag: 1, p: hlsParse(b64Text(pl[0]), ch.video), ext: 'mp4' });
          if (pl[1]) tracks.push({ itag: 2, p: hlsParse(b64Text(pl[1]), ch.audio), ext: 'm4a' });
          tracks.forEach(function (t) { if (t.p.bad) throw new Error('unsupported ' + t.p.bad); });
          var tasks = [];
          tracks.forEach(function (t) {
            tasks.push({ itag: t.itag, init: true, seq: 0, url: t.p.init });   // sans EXT-X-MAP (TS) : init vide
            t.p.segs.forEach(function (u, i) { tasks.push({ itag: t.itag, init: false, seq: i + 1, url: u }); });
          });
          // la ligne en echec du premier essai part d'abord : son nom est alors libre pour le fichier
          return nativeSend({ cmd: 'control', id: oldId, action: 'remove' }).catch(function () { }).then(function () {
            return nativeSend({ cmd: 'capture_begin', pageUrl: (tab && tab.url) || req.pageUrl, title: req.title || (tab && tab.title) || '',
              quality: req.quality, audioOnly: !!req.audioOnly, audioFormat: s.audioFormat, key: 'hls:' + normUrl(man.url) + (req.audioOnly ? ':a' : ''), total: 0 });
          })
            .then(function (b) {
              if (!b || !b.ok) throw new Error((b && b.error) || 'begin');
              appId = b.id;
              jobs[appId] = { tabId: tabId, frameId: frameId, capture: true, site: j.site || '' };
              saveJobs();
              var have = b.have || {};
              tasks = tasks.filter(function (t) {
                var h = have[String(t.itag)];
                return !(h && (t.init ? h.init > 0 : (h.segs || {})[String(t.seq)] > 0));
              });
              var total = tasks.length, done = 0, k = 0, lastPush = 0, t0 = Date.now(), msGet = 0, msSend = 0, bytes = 0, WORKERS = 8;
              var worker = function () {
                if (!alive() || k >= tasks.length) return Promise.resolve();
                var t = tasks[k++];
                var a0 = Date.now(), a1 = a0;
                var get = t.url ? chromeGetRetry(tabId, man.frame, t.url, 3) : Promise.resolve('');
                return get.then(function (data) {
                  a1 = Date.now(); msGet += a1 - a0; bytes += data.length * 3 / 4;
                  return segSend({ cmd: 'capture_seg', id: appId, itag: t.itag, init: t.init, seq: t.seq, data: data });
                }).then(function (x) {
                  msSend += Date.now() - a1;
                  if (!x || !x.ok) throw new Error((x && x.error) || 'seg');
                  done++;
                  if (Date.now() - lastPush > 500) { lastPush = Date.now(); pushJob(tabId, frameId, { status: 'capture', pct: Math.min(99, Math.floor(done * 100 / Math.max(1, total))), direct: true }); }
                  return worker();
                });
              };
              var pool = []; for (var w = 0; w < WORKERS; w++) pool.push(worker());
              return Promise.all(pool).then(function () {
                if (!alive()) throw new Error('stopped');
                segClose();
                var end = { cmd: 'capture_end', id: appId };
                // mesure (0.7.54) : ou passe le temps - lecture par le navigateur (filtre compris) ou remise au programme
                end.stats = { n: done, workers: WORKERS, wallMs: Date.now() - t0, getMs: msGet, sendMs: msSend, bytes: Math.round(bytes) };
                tracks.forEach(function (t) { end[t.itag === 1 ? 'video' : 'audio'] = { itag: t.itag, ext: t.ext }; });
                return nativeSend(end);
              }).then(function (x) {
                if (!x || !x.ok) throw new Error((x && x.error) || 'end');
                jobs[appId].capture = false;
                saveJobs(); startPoll();
                pushJob(tabId, frameId, { status: 'merging', id: appId });
              });
            });
        });
    });
  }).catch(quit);
}

// ---------- reprise sans geste et playlists (v0.7.35, programme >= 0.7.35) ----------
// Le programme ne peut pas parler le premier : on lui pose une attente longue (`pilot_wait`, 20 s). Quand il a quelque chose a
// demander (« Reprendre » sur une ligne, video suivante d'une playlist), il repond tout de suite ; on ouvre alors la page EN
// ARRIERE-PLAN (jamais au premier plan), on lance le telechargement, et on referme l'onglet quand il est fini.
// L'onglet n'est ouvert que pour une video YouTube dont l'adresse vient du programme (validee ici), jamais pour une autre page.
var wakeOn = false, ownTabs = {};
function ownLoad() { return chrome.storage.session.get('own').then(function (r) { ownTabs = r.own || {}; }, function () { }); }
function ownSave() { return chrome.storage.session.set({ own: ownTabs }).catch(function () { }); }
function closeOwn(tabId) {
  ownLoad().then(function () {
    if (!ownTabs[tabId]) return;
    delete ownTabs[tabId]; ownSave();
    chrome.tabs.remove(tabId).catch(function () { });
  });
}
function wakeLoop() {
  if (wakeOn) return;
  wakeOn = true;
  (function tick() {
    nativeSend({ cmd: 'pilot_wait', sec: 20 }).then(function (r) {
      if (!r || !r.ok) { wakeOn = false; return; }   // programme absent ou trop ancien : repris au prochain appCheck
      (r.wakes || []).forEach(function (w) { if (!answerConnectionProbe(w)) wakeResume(w); });
      setTimeout(tick, 200);
    }, function () { wakeOn = false; });
  })();
}
function answerConnectionProbe(w) {
  if (!w || w.type !== 'extension_probe') return false;
  if (/^[a-f0-9]{32}$/.test(w.token || '')) nativeSend({ cmd: 'extension_probe_reply', token: w.token }).catch(function () { });
  return true; // jamais d'onglet ni de video pour une verification de connexion
}
function wakeFail(w, tabId, own) {
  if (own && tabId != null) chrome.tabs.remove(tabId).catch(function () { });
  nativeSend({ cmd: 'pilot_wake_failed', tag: w.tag }).catch(function () { });
}
// attend que le relais de la page ait annonce la video (le programme sait de quoi il s'agit)
function waitSite(tabId, ms) {
  var t0 = Date.now();
  return new Promise(function (ok) {
    (function look() {
      readTab(tabId).then(function (st) {
        if (st.items.some(function (i) { return i.kind === 'site'; })) return ok(true);
        if (Date.now() - t0 > ms) return ok(false);
        setTimeout(look, 500);
      }, function () { ok(false); });
    })();
  });
}
function wakeResume(w) {
  var m = /^https:\/\/www\.youtube\.com\/watch\?v=([\w-]{6,20})$/.exec(String(w.url || ''));
  if (!m) { wakeFail(w, null, false); return; }
  var vid = m[1];
  chrome.tabs.query({ url: '*://*.youtube.com/watch*' }).then(function (tabs) {
    var t = (tabs || []).find(function (x) { return (x.url || '').indexOf('v=' + vid) >= 0; });
    if (t) return { id: t.id, own: false };
    return chrome.tabs.create({ url: w.url, active: false }).then(function (nt) {
      return ownLoad().then(function () { ownTabs[nt.id] = 1; return ownSave(); }).then(function () { return { id: nt.id, own: true }; });
    });
  }).then(function (o) {
    return waitSite(o.id, 30000).then(function (ok) {
      if (!ok) { wakeFail(w, o.id, o.own); return; }
      var q = w.quality && w.quality !== 'best' ? w.quality : undefined;
      return siteDownload(o.id, q, !!w.audioOnly, 0).then(function (res) {
        if (!res || !res.ok) { wakeFail(w, o.id, o.own); return; }
        track(o.id, 0, res);
      });
    });
  }).catch(function () { wakeFail(w, null, false); });
}

// ---------- playlist : liste lue dans la page, envoyee au programme (fenetre a cocher) ----------
// Lecture seule de la page : titres et identifiants des videos listees, rien d'autre. Defilement jusqu'a la fin (borne).
function scanPlaylist() {
  var out = { name: '', items: [], list: '' };
  var m = /[?&]list=([\w-]+)/.exec(location.search);
  out.list = m ? m[1] : '';
  // page de lecture : le titre de la liste est dans son panneau (le h1 de la page est celui de la video)
  var h = document.querySelector('ytd-playlist-panel-renderer h3 a, ytd-playlist-panel-renderer .title, ytd-playlist-header-renderer h1, yt-dynamic-text-view-model h1');
  out.name = (h && h.textContent || document.title || '').replace(/\s*-\s*YouTube\s*$/i, '').trim();
  var SEL = 'ytd-playlist-video-renderer, ytd-playlist-panel-video-renderer, ytd-rich-item-renderer, ytd-grid-video-renderer';
  function read() {
    var seen = {}, list = [];
    document.querySelectorAll(SEL).forEach(function (el) {
      var a = el.querySelector('a#video-title, a#wc-endpoint, a[href*="watch?v="]');
      if (!a) return;
      var mm = /[?&]v=([\w-]{6,20})/.exec(a.getAttribute('href') || '');
      if (!mm || seen[mm[1]]) return;
      seen[mm[1]] = 1;
      var title = (el.querySelector('#video-title') || a).textContent.replace(/\s+/g, ' ').trim();
      // privees / supprimees : le titre est un texte generique entre crochets, sans video a lire
      if (/^\[[^\]]{3,40}\]$/.test(title) || el.hasAttribute('is-unavailable')) return;
      list.push({ id: mm[1], title: title });
    });
    return list;
  }
  return new Promise(function (resolve) {
    var last = -1, still = 0, t0 = Date.now();
    (function step() {
      var l = read();
      if (l.length === last) still++; else { still = 0; last = l.length; }
      if (still >= 3 || Date.now() - t0 > 25000 || l.length >= 2000) {
        window.scrollTo(0, 0);
        l.forEach(function (x, i) { x.idx = i + 1; });
        out.items = l; resolve(out); return;
      }
      window.scrollTo(0, document.documentElement.scrollHeight);
      // lecteur de playlist (page de lecture) : la liste a son propre defilement
      document.querySelectorAll('ytd-playlist-panel-renderer #items').forEach(function (p) { p.scrollTop = p.scrollHeight; });
      setTimeout(step, 700);
    })();
  });
}
function playlistStart(tabId) {
  return needFix().then(function (bad) {
    if (bad) return bad;
    return chrome.tabs.get(tabId).then(function (tab) {
      if (!/^https:\/\/www\.youtube\.com\/(playlist|watch|@|channel|c\/|user\/)/.test(tab.url || '')) return { ok: false, error: 'notPlaylist' };
      return chrome.scripting.executeScript({ target: { tabId: tabId }, func: scanPlaylist }).then(function (r) {
        var pl = r && r[0] && r[0].result;
        if (!pl || !pl.items.length) return { ok: false, error: 'emptyList' };
        return getSettings().then(function (s) {
          return nativeSend({ cmd: 'playlist_add', name: pl.name, list: pl.list, items: pl.items, quality: showlefQualityNum(s.quality),
            audioOnly: showlefSiteIn(s.siteAudio, tab.url), audioFormat: s.audioFormat });
        });
      });
    });
  }).catch(function (e) { return { ok: false, error: String(e && e.message || e) }; });
}

// ---------- raccourci clavier : le meilleur media de l'onglet (sinon la page entiere) ----------
function bestItem(st) {
  return st.items.slice().sort(function (a, b) {
    return ((a.kind === 'file') - (b.kind === 'file')) || ((b.size || 0) - (a.size || 0));
  })[0];
}
chrome.commands.onCommand.addListener(function (cmd, tab) {
  if (cmd !== 'download-best' || !tab) return;
  readTab(tab.id).then(function (st) {
    var it = bestItem(st);
    if (it && it.kind === 'site') return siteDownload(tab.id);
    if (it) return sendToApp(tab.id, { url: it.url, kind: it.kind });
    if (/^https?:/i.test(tab.url || '')) return sendToApp(tab.id, { url: tab.url, kind: 'page' });
    return { ok: false, error: SHOWLEF_I18N.t('nothingHere') };
  }).then(function (res) { track(tab.id, 0, res); });
});

// ---------- menus contextuels ----------
function buildMenus() {
  chrome.contextMenus.removeAll(function () {
    chrome.contextMenus.create({ id: 'showlef-link', title: SHOWLEF_I18N.t('ctxLink'), contexts: ['link', 'video', 'audio'] });
    chrome.contextMenus.create({ id: 'showlef-page', title: SHOWLEF_I18N.t('ctxPage'), contexts: ['page', 'video'] });
  });
}
// v0.7.5 : l'ancien defaut « la meilleure » (4K = 213 Mo pour un chant de 2 min 32) devient « qualite normale », une fois
function migrateQuality() {
  return chrome.storage.local.get('settings').then(function (r) {
    var st = r.settings;
    if (!st || st.qv2) return;
    if (!st.quality || st.quality === 'best') st.quality = 'normal';
    st.qv2 = true;
    return chrome.storage.local.set({ settings: st });
  }).catch(function () { });
}
chrome.runtime.onInstalled.addListener(function () { migrateQuality(); });
// 1re installation : page « encore une etape » si le programme n'est pas deja la (une extension ne peut pas
// installer un programme elle-meme : la page telecharge l'installeur et voit le programme arriver tout seul).
// Mise a jour de l'extension (ou rechargement en developpement) : si le programme est reste en arriere, la page
// de reparation s'ouvre tout de suite, sans attendre un clic qui echouerait.
chrome.runtime.onInstalled.addListener(function (d) {
  if (d.reason !== 'install' && d.reason !== 'update') return;
  appCheck(true).then(function (a) {
    // programme qui marche mais en retard sur la derniere version publiee : on PROPOSE la mise a jour (rien ne bloque)
    if (a.state === 'ok') { if (a.update) openFix(true); return; }
    if (d.reason === 'install' && a.state === 'missing') chrome.tabs.create({ url: chrome.runtime.getURL('welcome.html') });
    else openFix();
  });
});
chrome.runtime.onStartup.addListener(function () { appCheck(true); pilotSync(true); });
chrome.runtime.onInstalled.addListener(function () { pilotSync(true); });
chrome.runtime.onInstalled.addListener(function () { SHOWLEF_I18N.init().then(buildMenus); });
chrome.runtime.onStartup.addListener(function () { SHOWLEF_I18N.init().then(buildMenus); });
SHOWLEF_I18N.init();

chrome.contextMenus.onClicked.addListener(function (info, tab) {
  var tabId = tab ? tab.id : -1;
  var p;
  if (info.menuItemId === 'showlef-link') {
    var url = info.srcUrl || info.linkUrl || '';
    // Une video en blob: n'a pas d'adresse telechargeable : on passe par la page.
    if (!/^https?:/i.test(url)) p = sendToApp(tabId, { url: (tab && tab.url) || info.pageUrl, kind: 'page' });
    else p = sendToApp(tabId, { url: url, kind: guessKind(url), referer: info.pageUrl });
  } else if (info.menuItemId === 'showlef-page') {
    p = sendToApp(tabId, { url: (tab && tab.url) || info.pageUrl, kind: 'page' });
  }
  if (p) p.then(function (res) { track(tabId, info.frameId || 0, res); });
});

// ---------- interception des telechargements Chrome (reglage, defaut : non) ----------
chrome.downloads.onCreated.addListener(function (it) {
  getSettings().then(function (s) {
    if (!s.intercept || it.byExtensionId === chrome.runtime.id) return;
    var url = it.finalUrl || it.url;
    if (!/^https?:/i.test(url) || showlefIsBlocked(url)) return;
    var name = it.filename || fileNameFromUrl(url);
    var m = /\.([a-z0-9]{1,5})$/i.exec(name);
    var ext = m ? m[1].toLowerCase() : urlExt(url);
    var mime = (it.mime || '').toLowerCase();
    if (INTERCEPT_EXT.indexOf(ext) < 0 && !/^(video|audio)\//.test(mime)) return;
    if (showlefSiteIn(s.siteChrome, it.referrer || url)) return;   // « laisser Chrome faire sur ce site »
    return nativeSend({ cmd: 'ping' }).then(function (p) {
      if (showlefAppState(p) !== 'ok') return;   // programme absent ou trop vieux : Chrome garde son telechargement
      // Avant (revue Codex n° 11) : Chrome annule et efface AVANT de savoir si ShowLef accepte ; un ajout refuse ou
      // une fenetre de confirmation annulee faisait perdre le telechargement. Maintenant : pause, puis annulation
      // seulement si ShowLef l'a pris ; sinon Chrome reprend.
      return chrome.downloads.pause(it.id).catch(function () { })
        .then(function () { return chrome.tabs.query({ active: true, lastFocusedWindow: true }); })
        .then(function (tabs) {
          var tabId = tabs && tabs[0] ? tabs[0].id : -1;
          return sendToApp(tabId, { url: url, kind: 'file', filename: name.split(/[\\\/]/).pop(), referer: it.referrer, noChrome: true })
            .then(function (res) {
              if (res && res.ok && res.via === 'app') {
                return chrome.downloads.cancel(it.id).catch(function () { })
                  .then(function () { return chrome.downloads.erase({ id: it.id }).catch(function () { }); })
                  .then(function () { track(tabId, 0, res); });
              }
              if (res && res.error === 'duplicate') { track(tabId, 0, res); return chrome.downloads.cancel(it.id).catch(function () { }); }
              return chrome.downloads.resume(it.id).catch(function () { });
            });
        });
    }, function () { });
  });
});
