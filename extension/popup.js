// ShowLef - popup (maquette « v1 Studio clair ») : etat du programme, media trouve dans la page,
// telechargements recents, reglages. Ouvrable aussi dans un onglet pour les tests : popup.html?tab=<id>.
(function () {
  var I = SHOWLEF_I18N;
  var HOST = 'com.showlef.app';
  var DEFAULTS = { lang: '', overlay: true, intercept: false, minSize: 300, quality: 'normal', qv2: true, advanced: false, audioFormat: 'mp3',
    overlayPos: 'tr', ask: false, theme: 'auto', siteOff: [], siteAudio: [], siteChrome: [], compat: false };
  var tabUrl = '', tabTitle = '';
  var $ = function (id) { return document.getElementById(id); };
  var tabId = -1, settings = Object.assign({}, DEFAULTS), appOk = false, pollTimer = 0, store = false;
  var mediaItems = null, sel = 0, mode = 'video', pick = {};   // pick : itemId -> variante choisie
  var allQ = false;   // « Plus d'options » ouvert dans la carte (mode simple)
  var unpicked = {};   // medias decoches pour « Tout telecharger » (par defaut : tous coches)

  function native(msg) {
    return new Promise(function (resolve) {
      try {
        chrome.runtime.sendNativeMessage(HOST, msg, function (r) {
          if (chrome.runtime.lastError) resolve({ ok: false, error: 'noApp', detail: chrome.runtime.lastError.message });
          else resolve(r || { ok: false, error: 'empty' });
        });
      } catch (e) { resolve({ ok: false, error: 'noApp', detail: e.message }); }
    });
  }
  function bg(msg) {
    msg.tabId = tabId;
    return chrome.runtime.sendMessage(msg).catch(function (e) { return { ok: false, error: e.message }; });
  }
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function icon(name) {
    var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('class', 'i');
    var u = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    u.setAttribute('href', '#i-' + name);
    s.appendChild(u);
    return s;
  }
  // action : { label, run } -> bouton dans l'avis (ex. « Annuler » apres une suppression)
  function flash(text, ok, action) {
    var n = $('notice');
    n.hidden = false;
    n.textContent = '';
    n.appendChild(el('span', null, text));
    n.className = 'notice ' + (ok ? 'ok' : 'ko');
    if (action) {
      var b = el('button', 'lnk', action.label);
      b.addEventListener('click', function () { clearTimeout(flash.t); n.hidden = true; action.run(); });
      n.appendChild(b);
    }
    clearTimeout(flash.t);
    flash.t = setTimeout(function () { n.hidden = true; }, action ? 8000 : 2600);
  }
  function resultText(res) {
    if (res.ok) return I.t(res.via === 'capture' ? 'capStarted' : res.via === 'chrome' ? (res.bySite ? 'sentChromeSite' : 'sentChrome') : 'sent');
    if (res.error === 'needApp' || res.error === 'needUpdate' || res.error === 'blocked' || res.error === 'duplicate') return I.t(res.error);
    return I.t('error', { e: I.humanErr(res.error) });
  }

  // ---------- textes ----------
  function applyTexts() {
    document.documentElement.lang = I.getLang();
    document.documentElement.dir = I.dir();
    $('appName').textContent = I.t('appName');
    $('openApp').title = $('openApp').ariaLabel = I.t('openApp');
    $('gear').title = $('gear').ariaLabel = I.t('settings');
    $('setBack').title = $('setBack').ariaLabel = I.t('back');
    $('pageVideo').title = I.t('pageHelp');
    $('ver').textContent = chrome.runtime.getManifest().version + (store ? ' store' : '');
    document.querySelectorAll('[data-t]').forEach(function (n) { n.textContent = I.t(n.getAttribute('data-t')); });
    fillSelect($('sLang'), [['', I.t('langAuto')], ['he', 'עברית'], ['fr', 'Français'], ['en', 'English']], settings.lang || '');
    // en mots ; les hauteurs en chiffres seulement en mode avance (une hauteur deja choisie reste affichee)
    var qOpts = [['normal', I.t('qNormal')], ['small', I.t('qSmall')], ['best', I.t('qBest')]];
    if (settings.advanced || /^\d+$/.test(settings.quality || '')) qOpts = qOpts.concat([['2160', '2160p'], ['1080', '1080p'], ['720', '720p'], ['480', '480p'], ['360', '360p']]);
    fillSelect($('sQuality'), qOpts, settings.quality || 'normal');
    $('sAdvanced').checked = !!settings.advanced;
    fillSelect($('sPos'), [['tr', I.t('posTR')], ['tl', I.t('posTL')], ['br', I.t('posBR')], ['bl', I.t('posBL')]], settings.overlayPos);
    fillSelect($('sTheme'), [['auto', I.t('themeAuto')], ['light', I.t('themeLight')], ['dark', I.t('themeDark')]], settings.theme);
    $('sAsk').checked = !!settings.ask;
    $('sCompat').checked = !!settings.compat;
    $('sCompat').title = I.t('compatHelp');
    $('sCompat').disabled = !!store;
    $('sOverlay').checked = settings.overlay;
    $('sIntercept').checked = settings.intercept;
    $('sMin').value = settings.minSize;
    $('sAudio').value = settings.audioFormat;
    $('siteFolderClear').title = I.t('folderReset');
    applyTheme();
    renderSite();
  }
  function fillSelect(sel, opts, val) {
    sel.textContent = '';
    opts.forEach(function (o) { var op = el('option', null, o[1]); op.value = o[0]; sel.appendChild(op); });
    sel.value = val;
  }
  function applyTheme() {
    if (settings.theme === 'light' || settings.theme === 'dark') document.documentElement.setAttribute('data-theme', settings.theme);
    else document.documentElement.removeAttribute('data-theme');
  }

  // ---------- sur ce site ----------
  function webTab() { return /^https?:/i.test(tabUrl); }
  function renderSite() {
    var key = showlefSiteKey(tabUrl), web = webTab();
    $('siteHost').textContent = $('siteHost2').textContent = web ? key : I.t('siteNone');
    $('siteLbl').textContent = I.t(mediaItems && !mediaItems.length ? 'site' : 'foundIn');
    $('siteButtons').disabled = $('siteAudio').disabled = $('siteAudioFoot').disabled = $('siteChrome').disabled = !web;
    $('siteChrome').checked = web && showlefSiteIn(settings.siteChrome, tabUrl);
    $('siteButtons').checked = web && settings.overlay && !showlefSiteIn(settings.siteOff, tabUrl);
    var aud = web && showlefSiteIn(settings.siteAudio, tabUrl);
    $('siteAudio').checked = aud;
    $('siteAudioFoot').classList.toggle('on', aud);
    $('siteAudioFoot').setAttribute('aria-pressed', aud ? 'true' : 'false');
    if (web) { $('siteFav').src = chrome.runtime.getURL('/_favicon/?pageUrl=' + encodeURIComponent(tabUrl) + '&size=32'); $('siteFav').hidden = false; $('siteFavNone').hidden = true; }
    else { $('siteFav').hidden = true; $('siteFavNone').hidden = false; }
  }
  function toggleSite(listName, on) {
    var key = showlefSiteKey(tabUrl);
    if (!key) return;
    var list = (settings[listName] || []).filter(function (h) { return h !== key && !key.endsWith('.' + h); });
    if (on) list.push(key);
    settings[listName] = list;
    if (listName === 'siteOff' && !on && !settings.overlay) settings.overlay = true;   // reactiver ici reactive le bouton
    if (listName === 'siteAudio') mode = on ? 'audio' : 'video';
    chrome.storage.local.set({ settings: settings }).then(function () { applyTexts(); renderMedia(); });
  }
  // Dossier de ce site : la regle vit dans le programme (seul lui connait les chemins du PC).
  function renderSiteFolder(folder) {
    var p = $('siteFolderPath');
    p.textContent = folder || I.t('folderDefault');
    p.className = 'fold-path' + (folder ? '' : ' def');
    p.title = folder || '';
    $('siteFolderClear').hidden = !folder;
  }
  function siteFolder(action) {
    var key = showlefSiteKey(tabUrl);
    if (!key || !appOk || !webTab()) { $('siteFolderRow').hidden = true; return; }
    $('siteFolderRow').hidden = false;
    native({ cmd: 'site_folder', site: key, action: action }).then(function (r) { if (r && r.ok) renderSiteFolder(r.folder); });
  }
  // la fenetre de choix s'ouvre dans le programme ; la popup peut se fermer, la regle est gardee
  $('siteFolderPick').addEventListener('click', function () { siteFolder('pick'); });
  $('siteFolderClear').addEventListener('click', function () { siteFolder('clear'); });
  $('siteButtons').addEventListener('change', function () { toggleSite('siteOff', !this.checked); });
  $('siteAudio').addEventListener('change', function () { toggleSite('siteAudio', this.checked); });
  $('siteChrome').addEventListener('change', function () { toggleSite('siteChrome', this.checked); });
  $('siteAudioFoot').addEventListener('click', function () { toggleSite('siteAudio', !showlefSiteIn(settings.siteAudio, tabUrl)); });

  function renderShortcut() {
    chrome.commands.getAll().then(function (cmds) {
      var c = cmds.find(function (x) { return x.name === 'download-best'; });
      $('kbd').textContent = (c && c.shortcut) || I.t('shortcutNone');
    });
  }
  // Diagnostic a coller dans un message d'aide : versions, etat du programme, reglages, dernieres erreurs.
  // Ni adresse de page, ni cookie, ni liste de sites (le PC est souvent familial).
  $('diag').addEventListener('click', function () {
    Promise.all([native({ cmd: 'ping' }), native({ cmd: 'list' })]).then(function (r) {
      var s = Object.assign({}, settings);
      s.siteOff = (s.siteOff || []).length; s.siteAudio = (s.siteAudio || []).length; s.siteChrome = (s.siteChrome || []).length;
      var items = (r[1] && r[1].items) || [];
      var lines = [
        'ShowLef ' + chrome.runtime.getManifest().version + (store ? ' store' : ''),
        'Navigateur : ' + navigator.userAgent,
        'Programme : ' + (r[0] && r[0].ok ? 'v' + r[0].version + ' ffmpeg=' + !!(r[0].tools || {}).ffmpeg + ' yt-dlp=' + !!(r[0].tools || {}).ytdlp : 'injoignable (' + ((r[0] && (r[0].detail || r[0].error)) || '?') + ')'),
        'Reglages : ' + JSON.stringify(s),
        'Telechargements : ' + items.length + ' (' + items.filter(function (i) { return i.status === 'error'; }).length + ' en erreur)'
      ];
      items.filter(function (i) { return i.status === 'error'; }).slice(0, 8).forEach(function (i) {
        lines.push(' - ' + i.kind + ' : ' + (i.code || '') + ' ' + scrub(i.error || '?'));
      });
      return navigator.clipboard.writeText(lines.join('\n'));
    }).then(function () { flash(I.t('diagCopied'), true); }, function (e) { flash(I.t('error', { e: I.humanErr(e.message) }), false); });
  });
  // une erreur d'outil peut contenir une adresse signee (jeton) ou un chemin du PC : on les retire
  function scrub(e) {
    return String(e).replace(/[a-z][a-z0-9+.-]*:\/\/\S+/gi, '<adresse>').replace(/[A-Za-z]:\\[^\s"']+/g, '<chemin>')
      .replace(/\\\\[^\s"']+/g, '<chemin>').slice(0, 300);
  }
  $('kbdChange').addEventListener('click', function () { chrome.tabs.create({ url: 'chrome://extensions/shortcuts' }); });

  // ---------- etat du programme ----------
  var pingRes = null;
  function renderState() {
    var st = $('state');
    if (!pingRes) { st.className = 'state'; $('stateText').textContent = I.t('checking'); st.title = ''; }
    else if (appOld) {
      // programme present mais trop vieux pour cette extension : meme carte, textes « mise a jour »
      st.className = 'state ko';
      $('stateText').textContent = I.t('appOldTitle');
      st.title = I.t('fxLeadOld', { v: pingRes.version || '?', m: SHOWLEF_APP_MIN });
    }
    else if (pingRes.ok) {
      var tools = pingRes.tools || {};
      var missing = !tools.ffmpeg || !tools.ytdlp;
      st.className = 'state ok';
      $('stateText').textContent = I.t('connected') + (missing ? ' · ' + I.t('toolsMissing') : '');
      st.title = I.t('appOk', { v: pingRes.version || '?' }) + ' · ' + I.t('toolConv') + ' ' + (tools.ffmpeg ? I.t('yes') : I.t('no')) +
        ' · ' + I.t('toolSites') + ' ' + (tools.ytdlp ? I.t('yes') : I.t('no'));
    } else {
      st.className = 'state ko';
      $('stateText').textContent = I.t('notConnected');
      st.title = I.t('appMissing');
    }
    $('openApp').disabled = $('allApp').disabled = !appOk;
    // programme qui marche mais en retard sur la derniere version publiee : meme carte, en proposition
    var upd = appOk && showlefAppUpdate(pingRes);
    $('missing').hidden = !(pingRes && (!appOk || upd));
    $('missing').classList.toggle('upd', !!upd);
    $('missTitle').textContent = I.t(upd ? 'updTitle' : appOld ? 'appOldTitle' : 'appMissingTitle', { v: SHOWLEF_APP_LATEST });
    $('missHelp').textContent = upd ? I.t('updHelp', { v: pingRes.version || '?', m: SHOWLEF_APP_LATEST })
      : appOld ? I.t('fxLeadOld', { v: pingRes.version || '?', m: SHOWLEF_APP_MIN }) : I.t('appMissingHelp');
    $('missDo').textContent = I.t(upd ? (pingRes.selfupdate ? 'updDoSelf' : 'updDo') : appOld ? 'appOldDo' : 'appMissingDo');
    $('getAppTx').textContent = I.t(upd && pingRes.selfupdate ? 'updNow' : appOld || upd ? 'getUpdate' : 'getApp');
  }
  var appOld = false;
  function ping() {
    return native({ cmd: 'ping' }).then(function (r) {
      pingRes = r; appOk = showlefAppState(r) === 'ok'; appOld = showlefAppState(r) === 'old';
      renderState();
      $('dlSection').hidden = !appOk;
      renderMedia();   // « Tout telecharger » attend le programme
      if (appOk) pollDownloads(); else clearTimeout(pollTimer);
      siteFolder('get');
    });
  }
  $('recheck').addEventListener('click', function () { pingRes = null; renderState(); ping(); });
  // Programme absent : on telecharge l'installeur (un fichier, sans droits admin), puis on reverifie toutes les
  // 3 secondes tant que la popup est ouverte : des que l'installation est faite, la carte disparait toute seule.
  var SETUP_URL = 'https://ext.i-call.me/downloads/ShowLef-Setup.exe';
  $('getApp').addEventListener('click', function () {
    // mise a jour proposee et programme capable de se mettre a jour lui-meme : la page s'en charge (avancement visible)
    if (pingRes && pingRes.selfupdate && appOk && showlefAppUpdate(pingRes)) {
      chrome.runtime.sendMessage({ type: 'openFix', update: true, auto: true }).catch(function () { });
      window.close();
      return;
    }
    chrome.downloads.download({ url: SETUP_URL, saveAs: false }).catch(function () { chrome.tabs.create({ url: SETUP_URL }); });
    $('gotApp').hidden = false;
  });
  setInterval(function () { if (pingRes && (!appOk || showlefAppUpdate(pingRes))) ping(); }, 3000);

  // ---------- media trouve dans la page ----------
  function kindLabel(it) {
    if (it.kind === 'site') return it.site || I.t('kindVideo');   // nom du site donne par le programme
    if (it.kind === 'hls' || it.kind === 'dash') return I.t(it.kind === 'hls' ? 'kindHls' : 'kindDash');
    return it.audio ? I.t('kindAudio') : I.t('kindVideo');
  }
  function fileName(url) {
    try { return decodeURIComponent(new URL(url).pathname.split('/').pop()) || new URL(url).hostname; } catch (e) { return url; }
  }
  // meme coupe que le programme (Fmt.ShortTitle) : ce qui precede le premier « | » s'il fait 8 caracteres, sans « - YouTube »
  function shortTitle(t) {
    t = (t || '').replace(/\s+/g, ' ').trim().replace(/\s*[-–—]\s*YouTube$/i, '');
    var m = /[|｜]/.exec(t);
    if (m) { var head = t.slice(0, m.index).trim().replace(/[-–—|,:\s]+$/, ''); if (head.length >= 8) t = head; }
    return t;
  }
  function titleOf(it) {
    if (it.title) return it.title;
    // un seul media dans la page : le titre de la page parle mieux que le nom du fichier
    if (mediaItems && mediaItems.length === 1 && tabTitle) return shortTitle(tabTitle);
    return fileName(it.url);
  }
  function loadMedia() {
    return bg({ type: 'getMedia' }).then(function (r) {
      store = !!(r && r.store);
      mediaItems = ((r && r.items) || []).slice().sort(function (a, b) {
        return ((a.kind === 'file') - (b.kind === 'file')) || ((b.size || 0) - (a.size || 0));
      });
      if (sel >= mediaItems.length) sel = 0;
      renderMedia();
    });
  }
  // variantes de qualite, avec taille quand on la connait (YouTube : annoncee ; HLS : debit x duree)
  function variantsOf(it) {
    return (it.variants || []).map(function (v) {
      var size = v.size || (v.bandwidth && it.duration ? v.bandwidth / 8 * it.duration : 0);
      return { value: v.url, label: v.label, size: size };
    });
  }
  function defaultVariant(it) {
    var vs = variantsOf(it);
    if (!vs.length) return undefined;
    if (pick[it.id] && vs.some(function (v) { return v.value === pick[it.id]; })) return pick[it.id];
    return showlefPickVariant(vs, settings.quality || 'normal').value;
  }
  function chip(label, small, on, fn) {
    var b = el('button', 'q' + (on ? ' on' : ''), label);
    if (small) b.appendChild(el('small', null, small));
    b.addEventListener('click', fn);
    return b;
  }
  function renderMedia() {
    if (!mediaItems) return;
    $('loading').hidden = true;
    var items = mediaItems, web = webTab();
    $('pageVideo').hidden = !web || (store && showlefIsBlocked(tabUrl));
    // playlist YouTube : liste a cocher (la liste est lue dans la page, le choix se fait dans le programme)
    $('plBtn').hidden = !(web && !store && /^https:\/\/www\.youtube\.com\/(playlist\?|watch\?)[^#]*[?&]?list=/.test(tabUrl));
    $('emptyBox').hidden = !!items.length;
    $('siteLbl').textContent = I.t(items.length ? 'foundIn' : 'site');
    $('mediaBox').hidden = !items.length;
    if (!items.length) return;
    var it = items[sel];
    $('mTitle').textContent = titleOf(it);
    $('mTitle').dir = 'auto';
    $('mTitle').title = it.url;

    // plusieurs medias : liste compacte pour choisir
    $('pickBox').hidden = items.length < 2;
    var list = $('mediaList');
    list.textContent = '';
    if (items.length > 1) {
      $('mediaCount').textContent = I.t('mediaN', { n: items.length });
      var batch = batchItems();
      items.forEach(function (m, i) {
        var r = el('button', 'mrow' + (i === sel ? ' on' : ''));
        var inBatch = m.kind !== 'site';
        var ck = el('span', 'ck' + (inBatch && !unpicked[m.id] ? ' on' : ''));
        ck.appendChild(icon('check'));
        ck.style.visibility = inBatch ? '' : 'hidden';
        ck.addEventListener('click', function (e) { e.stopPropagation(); unpicked[m.id] = !unpicked[m.id]; renderMedia(); });
        r.appendChild(ck);
        var ic = el('span', 'mi' + (m.audio ? ' a' : ''));
        ic.appendChild(icon(m.audio ? 'note' : 'film'));
        r.appendChild(ic);
        r.appendChild(el('span', 'mt', m.title || fileName(m.url)));
        var meta = [kindLabel(m)];
        if (m.variants && m.variants.length) meta.push(m.variants[0].label);
        else if (m.size) meta.push(I.fmtSize(m.size));
        if (m.duration) meta.push(I.fmtDur(m.duration));
        r.appendChild(el('span', 'mm', meta.join(' · ')));
        r.title = m.url;
        r.addEventListener('click', function () { sel = i; renderMedia(); });
        list.appendChild(r);
      });
      var nb = batch.filter(function (m) { return !unpicked[m.id]; }).length;
      $('dlAll').hidden = batch.length < 2;
      $('dlAll').disabled = !nb || !appOk;
      $('dlAllTxt').textContent = I.t(nb === batch.length ? 'dlAll' : 'dlSel', { n: nb });
      $('pickToggle').hidden = batch.length < 2;
      $('pickToggle').textContent = I.t(nb === batch.length ? 'pickNone' : 'pickAll');
    }

    // Video / Audio seul (un fichier deja audio : pas de choix)
    var audioFile = it.audio && it.kind === 'file';
    $('seg').hidden = audioFile;
    var m = audioFile ? 'video' : mode;
    $('segVideo').classList.toggle('on', m === 'video');
    $('segAudio').classList.toggle('on', m === 'audio');

    // puces : qualites (video) ou format (audio seul)
    var qs = $('qs');
    qs.textContent = '';
    if (m === 'audio') {
      ['mp3', 'm4a'].forEach(function (f) {
        qs.appendChild(chip(f.toUpperCase(), '', settings.audioFormat === f, function () {
          settings.audioFormat = f;
          chrome.storage.local.set({ settings: settings }).then(function () { applyTexts(); renderMedia(); });
        }));
      });
    } else {
      var vs = variantsOf(it), cur = defaultVariant(it);
      var choose = function (v) { return function () { pick[it.id] = v.value; renderMedia(); }; };
      if (vs.length && (settings.advanced || allQ)) vs.forEach(function (v) {
        qs.appendChild(chip(v.label, v.size ? I.fmtSize(v.size) : '', v.value === cur, choose(v)));
      });
      else if (vs.length) {
        // mode simple : deux choix en mots (un seul s'ils tombent sur la meme qualite), puis « Plus d'options »
        var vn = showlefPickVariant(vs, 'normal'), vsm = showlefPickVariant(vs, 'small');
        qs.appendChild(chip(I.t('qNormal'), vn.size ? I.fmtSize(vn.size) : '', cur === vn.value, choose(vn)));
        if (vsm !== vn) qs.appendChild(chip(I.t('qSmall'), vsm.size ? I.fmtSize(vsm.size) : '', cur === vsm.value, choose(vsm)));
        if (cur !== vn.value && cur !== vsm.value) {
          var vc = vs.find(function (v) { return v.value === cur; });
          if (vc) qs.appendChild(chip(vc.label, vc.size ? I.fmtSize(vc.size) : '', true, choose(vc)));
        }
        if (vs.length > 2) { var more = chip(I.t('moreOptions'), '', false, function () { allQ = true; renderMedia(); }); more.classList.add('more'); qs.appendChild(more); }
      }
      else {
        var lab = it.ext ? it.ext.toUpperCase() : kindLabel(it);
        qs.appendChild(chip(lab, it.size ? I.fmtSize(it.size) : (it.live ? I.t('live') : ''), true, function () { }));
      }
    }
  }
  // « Tout telecharger » (idee prise chez Chrono) : les medias coches, dans l'ordre de la liste, noms numerotes
  // « 01 — titre » pour qu'une serie de cours reste dans l'ordre sur une cle USB ou un petit lecteur.
  function batchItems() { return (mediaItems || []).filter(function (m) { return m.kind !== 'site'; }); }
  function pad2(n, total) { var w = String(total).length < 2 ? 2 : String(total).length; var s = String(n); while (s.length < w) s = '0' + s; return s; }
  function batchName(m, n, total, audio) {
    var t = titleOf(m).replace(/\.[A-Za-z0-9]{2,4}$/, '').replace(/[\\\/:*?"<>|]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 110) || 'media';
    var ext = audio ? '.' + (settings.audioFormat || 'mp3') : m.kind === 'file' ? '.' + ((m.ext || 'mp4').toLowerCase()) : '.mp4';
    return pad2(n, total) + ' — ' + t + ext;
  }
  $('pickToggle').addEventListener('click', function () {
    var b = batchItems(), all = b.every(function (m) { return !unpicked[m.id]; });
    unpicked = {};
    if (all) b.forEach(function (m) { unpicked[m.id] = true; });
    renderMedia();
  });
  $('dlAll').addEventListener('click', function () {
    var list = batchItems().filter(function (m) { return !unpicked[m.id]; });
    if (!list.length) return;
    $('dlAll').disabled = true;
    var audio = mode === 'audio', sent = 0, i = 0;
    (function next() {
      if (i >= list.length) {
        $('dlAll').disabled = false;
        flash(I.t('batchSent', { n: sent }), sent > 0);
        setTimeout(pollDownloads, 400);
        return;
      }
      var m = list[i++], isAudioFile = m.audio && m.kind === 'file';
      bg({ type: 'download', itemId: m.id, variantUrl: defaultVariant(m), audioOnly: audio && !isAudioFile,
        filename: batchName(m, i, list.length, audio && !isAudioFile), title: titleOf(m) })
        .then(function (r) { if (r && (r.ok || r.error === 'duplicate')) sent++; next(); });
    })();
  });

  // « Je ne vois pas ma video ? » : les gestes, puis « Chercher a nouveau » (chaque cadre re-signale ses medias)
  $('helpLnk').addEventListener('click', function () { $('helpBox').hidden = !$('helpBox').hidden; });
  $('rescan').addEventListener('click', function () {
    $('rescan').disabled = true;
    bg({ type: 'rescan' }).then(function (r) {
      $('rescan').disabled = false;
      var n = (r && r.n) || 0;
      flash(I.t(n ? 'rescanDone' : 'rescanNone', { n: n }), n > 0);
      sel = 0; return loadMedia();
    });
  });

  $('segVideo').addEventListener('click', function () { mode = 'video'; renderMedia(); });
  $('segAudio').addEventListener('click', function () { mode = 'audio'; renderMedia(); });
  $('dlBig').addEventListener('click', function () {
    var it = mediaItems && mediaItems[sel];
    if (!it) return;
    var audio = mode === 'audio' && !(it.audio && it.kind === 'file');
    $('dlBig').disabled = true;
    bg({ type: 'download', itemId: it.id, variantUrl: defaultVariant(it), audioOnly: audio }).then(after);
  });
  $('plBtn').addEventListener('click', function () {
    $('plBtn').disabled = true;
    flash(I.t('plReading'), true);
    bg({ type: 'playlistStart' }).then(function (r) {
      $('plBtn').disabled = false;
      if (r && r.ok) flash(I.t(r.count ? 'plSent' : 'plNone', { n: r.count || 0 }), true);
      else flash(I.t(r && r.error === 'emptyList' ? 'plEmpty' : r && r.error === 'busy' ? 'plBusy' : 'plErr'), false);
    });
  });
  $('pageVideo').addEventListener('click', function () { bg({ type: 'downloadPage', audioOnly: mode === 'audio' }).then(after); });
  function after(res) {
    $('dlBig').disabled = false;
    flash(resultText(res || { ok: false, error: '?' }), res && res.ok);
    if (res && res.ok && res.via === 'app') setTimeout(pollDownloads, 400);
  }

  // ---------- telechargements recents (programme) ----------
  function pollDownloads() {
    clearTimeout(pollTimer);
    if (!appOk) return;
    native({ cmd: 'list' }).then(function (r) {
      if (r && r.ok) renderDownloads(r.items || []);
      pollTimer = setTimeout(pollDownloads, 1000);
    });
  }
  function control(id, action) {
    native({ cmd: 'control', id: id, action: action }).then(function (r) {
      if (!r.ok) flash(action === 'undelete' ? I.t('undoFail') : I.t('error', { e: I.humanErr(r.error) }), false);
      else if (action === 'delete') flash(I.t('deleted'), true, r.undo ? { label: I.t('undo'), run: function () { control(id, 'undelete'); } } : null);
      pollDownloads();
    });
  }
  function miniBtn(name, title, cls, fn) {
    var b = el('button', cls || '');
    b.title = title; b.setAttribute('aria-label', title);
    b.appendChild(icon(name));
    b.addEventListener('click', fn);
    return b;
  }
  var AUDIO_EXT = /\.(mp3|m4a|aac|opus|ogg|oga|wav|flac|wma)$/i;
  // Rendu par cle : la liste est rafraichie chaque seconde, recreer les boutons ferait perdre
  // le survol et le focus clavier a chaque passage.
  var rows = {};
  function renderDownloads(items) {
    var list = $('dlList');
    items = items.slice(0, 30);
    $('dlCount').textContent = items.length || '';
    if (!items.length) { rows = {}; list.textContent = ''; list.appendChild(el('div', 'dl-empty', I.t('noDownloads'))); return; }
    var empty = list.querySelector('.dl-empty'); if (empty) empty.remove();
    var seen = {};
    items.forEach(function (d, i) {
      seen[d.id] = 1;
      var r = rows[d.id];
      if (!r) {
        r = rows[d.id] = { box: el('div', 'pl-row'), ico: el('span', 'ico'), fn: el('div', 't'), meta: el('div', 'm'), bar: el('div', 'minibar'), fill: el('i'), btns: el('div', 'mini'), sig: '' };
        var mid = el('div');
        mid.style.minWidth = '0';
        r.bar.appendChild(r.fill);
        mid.appendChild(r.fn); mid.appendChild(r.meta); mid.appendChild(r.bar);
        r.box.appendChild(r.ico); r.box.appendChild(mid); r.box.appendChild(r.btns);
      }
      r.box.className = 'pl-row ' + d.status;
      r.fn.textContent = (d.name || '').replace(/^~/, '').replace(/\.[A-Za-z0-9]{2,4}$/, '') || d.name || '…'; r.fn.title = d.name || ''; r.fn.dir = 'auto';  // titre sans extension, comme le programme (v1)
      var kind = d.status === 'error' ? 'e' : AUDIO_EXT.test(d.name || '') ? 'a' : 'v';
      if (r.kind !== kind) { r.kind = kind; r.ico.className = 'ico ' + kind; r.ico.textContent = ''; r.ico.appendChild(icon(kind === 'e' ? 'alert' : kind === 'a' ? 'note' : 'film')); }
      if (r.sig !== d.status) {       // boutons recrees seulement quand l'etat change
        r.sig = d.status;
        r.btns.textContent = '';
        var cancel = function () { control(d.id, 'cancel'); };
        if (d.status === 'downloading' || d.status === 'queued' || d.status === 'merging') {
          if (d.status !== 'merging') r.btns.appendChild(miniBtn('pause', I.t('pause'), '', function () { control(d.id, 'pause'); }));
          r.btns.appendChild(miniBtn('x', I.t('cancel'), 'del', cancel));
        } else if (d.status === 'paused') {
          r.btns.appendChild(miniBtn('play', I.t('resume'), '', function () { control(d.id, 'resume'); }));
          r.btns.appendChild(miniBtn('x', I.t('cancel'), 'del', cancel));
        } else if (d.status === 'error') {
          r.btns.appendChild(miniBtn('retry', I.t('retry'), '', function () { control(d.id, 'resume'); }));
          r.btns.appendChild(miniBtn('x', I.t('remove'), 'del', function () { control(d.id, 'remove'); }));
        } else if (d.status === 'done') {
          // termine : Lire, Ouvrir le dossier, Supprimer (corbeille, annulable) ; x discret = retirer de la liste
          r.btns.appendChild(miniBtn('play', I.t('play'), 'pp', function () { control(d.id, 'open'); }));
          r.btns.appendChild(miniBtn('folder', I.t('openFolder'), '', function () { control(d.id, 'folder'); }));
          r.btns.appendChild(miniBtn('trash', I.t('deleteFile'), 'del', function () { control(d.id, 'delete'); }));
          r.btns.appendChild(miniBtn('x', I.t('remove'), 'rm', function () { control(d.id, 'remove'); }));
        }
      }
      var pct = d.total > 0 ? Math.min(100, d.received / d.total * 100) : (d.status === 'done' ? 100 : 0);
      var active = d.status === 'downloading' || d.status === 'paused' || d.status === 'queued' || d.status === 'merging';
      r.bar.hidden = !active;
      r.fill.style.width = pct.toFixed(1) + '%';
      var meta = [];
      if (d.status === 'downloading') {
        if (d.total > 0) meta.push(Math.floor(pct) + '%');
        else if (d.received > 0) meta.push(I.fmtSize(d.received));
        if (d.speed > 0) meta.push(I.t('perSec', { s: I.fmtSize(d.speed) }));
        if (d.eta > 0) {
          meta.push(I.t('leftShort', { t: I.fmtLeft(d.eta) }));
          var end = new Date(Date.now() + d.eta * 1000);
          meta.push(I.t('endAt', { h: (end.getHours() < 10 ? '0' : '') + end.getHours() + ':' + (end.getMinutes() < 10 ? '0' : '') + end.getMinutes() }));
        }
      } else if (d.status === 'done') {
        meta.push(I.t('st_done'));
        if (d.total > 0) meta.push(I.fmtSize(d.total));
      } else if (d.status === 'error') {
        meta.push(I.humanErr(d.error, d.code));
      } else {
        meta.push(I.t('st_' + d.status));
        if (d.total > 0) meta.push(Math.floor(pct) + '%');
      }
      r.meta.textContent = meta.join(' · ');
      r.meta.title = d.status === 'error' ? (d.error || '') : '';
      if (list.children[i] !== r.box) list.insertBefore(r.box, list.children[i] || null);
    });
    Object.keys(rows).forEach(function (id) { if (!seen[id]) { rows[id].box.remove(); delete rows[id]; } });
  }

  // ---------- reglages ----------
  function saveSettings() {
    settings.lang = $('sLang').value;
    settings.overlay = $('sOverlay').checked;
    settings.intercept = $('sIntercept').checked;
    settings.minSize = Math.max(0, parseInt($('sMin').value, 10) || 0);
    settings.quality = $('sQuality').value;
    settings.audioFormat = $('sAudio').value;
    settings.overlayPos = $('sPos').value;
    settings.ask = $('sAsk').checked;
    settings.advanced = $('sAdvanced').checked;
    settings.qv2 = true;
    settings.theme = $('sTheme').value;
    var compatWas = !!settings.compat;
    settings.compat = $('sCompat').checked;
    if (settings.compat !== compatWas) setTimeout(function () { flash(I.t('compatReload'), true); }, 50);
    chrome.storage.local.set({ settings: settings }).then(function () {
      I.setLang(settings.lang);
      pick = {};
      applyTexts(); renderState(); loadMedia(); rows = {}; $('dlList').textContent = ''; pollDownloads();
    });
  }
  ['sLang', 'sOverlay', 'sIntercept', 'sMin', 'sQuality', 'sAudio', 'sPos', 'sAsk', 'sAdvanced', 'sTheme', 'sCompat'].forEach(function (id) {
    $(id).addEventListener('change', saveSettings);
  });
  function showSettings(open) {
    $('settings').hidden = !open;
    $('mainView').hidden = open;
    $('gear').setAttribute('aria-expanded', open ? 'true' : 'false');
  }
  $('gear').addEventListener('click', function () { showSettings($('settings').hidden); });
  $('setBack').addEventListener('click', function () { showSettings(false); });
  $('openApp').addEventListener('click', function () { native({ cmd: 'show' }); });
  $('allApp').addEventListener('click', function () { native({ cmd: 'show' }); });

  // ---------- demarrage ----------
  var q = new URLSearchParams(location.search);
  if (q.get('tab')) document.body.classList.add('tab');
  var tabP = q.get('tab')
    ? Promise.resolve(+q.get('tab'))
    : chrome.tabs.query({ active: true, currentWindow: true }).then(function (t) { return t[0] ? t[0].id : -1; });
  tabP = tabP.then(function (id) {
    return chrome.tabs.get(id).then(function (t) { tabUrl = t.url || ''; tabTitle = t.title || ''; return id; }, function () { return id; });
  });
  Promise.all([tabP, chrome.storage.local.get('settings'), I.init()]).then(function (r) {
    tabId = r[0];
    renderShortcut();
    settings = Object.assign({}, DEFAULTS, r[1].settings || {});
    if (showlefSiteIn(settings.siteAudio, tabUrl)) mode = 'audio';
    applyTexts();
    renderState();
    return loadMedia();
  }).then(function () {
    applyTexts();
    ping();
  });
})();
