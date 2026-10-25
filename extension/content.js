// ShowLef - bouton flottant sur les videos (facon IDM) + menu des qualites + notifications.
// Tout vit dans un shadow DOM ferme : la page ne peut ni le styler ni le casser.
(function () {
  if (window.__showlefLoaded) return;
  window.__showlefLoaded = true;

  var I = SHOWLEF_I18N;
  var MIN_W = 200, MIN_H = 120, AUDIO_MIN_W = 150;
  var enabled = true, ready = false, pos = 'tr', settingsNow = {};
  var veil = null;   // voile sur le lecteur pendant la capture YouTube (lecture rapide et muette = normal, on le dit)
  // Dans une iframe, le site « de l'onglet » est celui de la page principale.
  var topUrl = (location.ancestorOrigins && location.ancestorOrigins.length) ? location.ancestorOrigins[location.ancestorOrigins.length - 1] : location.href;
  function applySettings(s) {
    settingsNow = s || {};
    enabled = settingsNow.overlay !== false && !showlefSiteIn(settingsNow.siteOff, topUrl);
    pos = settingsNow.overlayPos || 'tr';
  }
  var media = new Set();
  var host = null, root = null, btn = null, menu = null, toastEl = null;
  var current = null, hideTimer = 0, menuOpen = false, moveQueued = false, lastX = -1, lastY = -1;

  chrome.storage.local.get('settings').then(function (r) {
    applySettings(r.settings);
    return I.init();
  }).then(function () { ready = true; });
  chrome.storage.onChanged.addListener(function (ch, area) {
    if (area !== 'local' || !ch.settings) return;
    var s = ch.settings.newValue || {};
    applySettings(s);
    I.setLang(s.lang);
    applyThemeHost();
    mediaCache = null;
    if (job) { renderJob(); return; }   // un telechargement suivi garde son texte
    if (!enabled) hide(true);
    if (btn) resetIdle();
  });

  // ---------- suivi des elements media (ajouts dynamiques compris) ----------
  var reported = {};
  function report(el) {
    var s = directSrc(el);
    if (!s || reported[s]) return;
    reported[s] = 1;
    chrome.runtime.sendMessage({ type: 'reportSrc', url: s, audio: el.tagName === 'AUDIO' }).catch(function () { });
  }
  function track(el) {
    if (media.has(el)) return;
    media.add(el);
    report(el);
    el.addEventListener('loadedmetadata', function () { report(el); });
  }
  function scan(node) {
    if (!node || node.nodeType !== 1) return;
    if (node.tagName === 'VIDEO' || node.tagName === 'AUDIO') track(node);
    if (node.querySelectorAll) node.querySelectorAll('video,audio').forEach(track);
  }
  scan(document.documentElement);
  new MutationObserver(function (muts) {
    for (var i = 0; i < muts.length; i++) {
      var a = muts[i].addedNodes;
      for (var j = 0; j < a.length; j++) scan(a[j]);
    }
  }).observe(document.documentElement, { childList: true, subtree: true });

  function eligible(el) {
    if (!el.isConnected) { media.delete(el); return null; }
    var r = el.getBoundingClientRect();
    if (el.tagName === 'AUDIO') {
      if (r.width < AUDIO_MIN_W || r.height <= 0) return null;
    } else if (r.width < MIN_W || r.height < MIN_H) return null;
    var cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity === 0) return null;
    return r;
  }

  function mediaAt(x, y) {
    var best = null, area = 0;
    media.forEach(function (el) {
      var r = eligible(el);
      if (!r || x < r.left || x > r.right || y < r.top || y > r.bottom) return;
      var a = r.width * r.height;
      if (!best || a < area) { best = el; area = a; }   // la plus petite : video imbriquee dans un lecteur
    });
    return best;
  }

  // ---------- interface (shadow DOM) ----------
  // Icones au trait de la maquette v1 (meme dessin que la popup).
  var IC = {
    dl: '<path d="M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19.5h14"/>',
    play: '<path d="M8 5.5v13l10.5-6.5z" fill="currentColor" stroke="none"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
    alert: '<path d="M12 8v5M12 16.5v.5"/><circle cx="12" cy="12" r="9"/>',
    caret: '<path d="m7 10 5 5 5-5"/>',
    x: '<path d="M6 6l12 12M18 6 6 18"/>',
    film: '<rect x="3.5" y="5" width="17" height="14" rx="2.5"/><path d="M3.5 9.5h17M8 5v4.5M16 5v4.5"/>',
    note: '<path d="M9 17.5V6l10-2v11.5"/><circle cx="6.5" cy="17.5" r="2.5"/><circle cx="16.5" cy="15.5" r="2.5"/>',
    globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.5 2.6 2.5 14.4 0 17M12 3.5c-2.5 2.6-2.5 14.4 0 17"/>',
    more: '<circle cx="6" cy="12" r="1.2" fill="currentColor"/><circle cx="12" cy="12" r="1.2" fill="currentColor"/><circle cx="18" cy="12" r="1.2" fill="currentColor"/>',
    eyeoff: '<path d="M4 4l16 16M10.6 6.1A9.6 9.6 0 0 1 12 6c5 0 8.5 4.5 9 6-.3.8-1.2 2.3-2.7 3.6M6.4 7.6C4.6 8.9 3.4 10.8 3 12c.5 1.5 4 6 9 6 1.6 0 3-.5 4.2-1.1M9.9 9.9a3 3 0 0 0 4.2 4.2"/>'
  };
  function svg(name, cls) { return '<svg class="i' + (cls ? ' ' + cls : '') + '" viewBox="0 0 24 24" aria-hidden="true">' + IC[name] + '</svg>'; }
  var ARROW = svg('dl');

  // Polices de l'extension, declarees UNE fois dans le document sous des noms prefixes : un @font-face dans
  // la feuille du shadow DOM ne s'applique pas, et les polices « Rubik » du site ne sont jamais touchees.
  // FontFace (et non une <style> injectee) : passe aussi sur les sites a politique style-src stricte.
  // Les octets viennent du service worker (message « asset ») puis sont donnes a FontFace : sur YouTube, TOUT
  // chargement d'une adresse chrome-extension:// depuis la page (police, feuille, et meme fetch du script de contenu)
  // reussit mais inscrit « Unsafe attempt to load URL » dans les erreurs de l'extension (vu par ShowF le
  // 20/10 : 5 erreurs par video ; reproduit : fetch -> 200 + erreur sur YouTube, 200 sans erreur sur w3schools).
  function extAsset(path, text) {
    return chrome.runtime.sendMessage({ type: 'asset', path: path, text: !!text }).then(function (r) {
      if (!r || !r.ok) throw new Error('asset ' + path);
      if (text) return r.data;
      var bin = atob(r.data), u = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
      return u.buffer;
    });
  }
  function extBytes(path) { return extAsset(path, false); }
  function loadFonts() {
    if (window.__showlefFonts || !document.fonts || typeof FontFace !== 'function') return;
    window.__showlefFonts = true;
    var HE = 'U+0307-0308, U+0590-05FF, U+200C-2010, U+20AA, U+25CC, U+FB1D-FB4F';
    var LA = 'U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD';
    [['ShowLef Rubik', 'rubik-hebrew', HE, '300 900'], ['ShowLef Rubik', 'rubik-latin', LA, '300 900'],
      ['ShowLef Fredoka', 'fredoka-hebrew', HE, '300 700'], ['ShowLef Fredoka', 'fredoka-latin', LA, '300 700']].forEach(function (f) {
      try {
        extBytes('fonts/' + f[1] + '.woff2').then(function (buf) {
          var ff = new FontFace(f[0], buf, { weight: f[3], unicodeRange: f[2], display: 'swap' });
          document.fonts.add(ff);
          return ff.load();
        }).catch(function () { });
      } catch (e) { }
    });
  }

  var edge = null, tip = null;
  function ensureUi() {
    if (host) { attachHost(); return; }
    loadFonts();
    host = document.createElement('showlef-ui');
    // invisible tant que la feuille n'est pas posee : jamais de bouton sans style
    host.style.cssText = 'all:initial;position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;visibility:hidden;';
    root = host.attachShadow({ mode: 'closed' });
    // feuille lue par ce script puis adoptee par le shadow DOM (meme raison que les polices : jamais une adresse
    // chrome-extension:// chargee par la page)
    extAsset('content.css', true).then(function (css) {
      var sh = new CSSStyleSheet();
      sh.replaceSync(css);
      root.adoptedStyleSheets = [sh];
      host.style.visibility = 'visible';
      place();   // tant que la feuille n'est pas posee, la largeur mesuree est fausse
    }).catch(function () { die(); });
    btn = document.createElement('button');
    btn.className = 'kbtn';
    btn.innerHTML = '<span class="kic">' + ARROW + '</span><span class="lbl"></span>' +
      '<span class="kq" role="button"><span class="ksub num"></span>' + svg('caret') + '</span>' +
      '<span class="ktail"></span><span class="kx" role="button">' + svg('x') + '</span><i class="kfill"></i>';
    btn.addEventListener('click', function (e) {
      e.preventDefault(); e.stopPropagation();
      var onX = e.target.closest && e.target.closest('.kx');
      if (job) { if (onX) endJob(); else jobClick(); return; }
      if (onX) hideSite();
      else onButton(!!(e.target.closest && e.target.closest('.kq')));
    }, true);
    btn.addEventListener('mouseenter', function () { clearTimeout(hideTimer); });
    btn.addEventListener('mouseleave', scheduleHide);
    root.appendChild(btn);
    // capture YouTube : le lecteur lit la video tres vite et sans son ; sans explication l'utilisateur croit a une panne
    veil = document.createElement('div');
    veil.className = 'kveil';
    veil.innerHTML = '<span class="vp num"></span><span class="vt"></span><span class="vs"></span><span class="vb"><i></i></span>';
    veil.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); }, true);
    root.insertBefore(veil, btn);
    edge = document.createElement('div');
    edge.className = 'kedge';
    edge.appendChild(document.createElement('i'));
    root.appendChild(edge);
    tip = document.createElement('div');
    tip.className = 'ktip';
    root.appendChild(tip);
    menu = document.createElement('div');
    menu.className = 'kmenu';
    menu.addEventListener('mouseenter', function () { clearTimeout(hideTimer); });
    menu.addEventListener('mouseleave', scheduleHide);
    root.appendChild(menu);
    toastEl = document.createElement('div');
    toastEl.className = 'ktoast';
    root.appendChild(toastEl);
    resetIdle();
    attachHost();
  }
  // Pastille au repos : « Telecharger | 1080p ⌄ »
  function resetIdle() {
    btn.className = 'kbtn' + (btn.classList.contains('on') ? ' on' : '');
    btn.title = '';
    btn.querySelector('.kic').innerHTML = ARROW;
    btn.querySelector('.lbl').textContent = I.t('download');
    btn.querySelector('.ktail').innerHTML = '';
    btn.querySelector('.kx').title = I.t('hideHere');
    btn.querySelector('.kq').title = I.t('pickQuality');
    btn.querySelector('.kq').style.display = '';
    btn.querySelector('.kfill').style.width = '0';
    if (edge) edge.className = 'kedge';
    if (tip) tip.className = 'ktip';
    refreshSub();
  }
  function applyThemeHost() {
    if (!host) return;
    var th = settingsNow.theme || 'auto';
    var dark = th === 'dark' || (th === 'auto' && matchMedia('(prefers-color-scheme: dark)').matches);
    host.classList.toggle('dark', dark);
  }

  // En plein ecran seul le sous-arbre plein ecran est affiche : le bouton doit y vivre.
  function attachHost() {
    if (dead) return;
    var fs = document.fullscreenElement;
    var parent = fs && fs.tagName !== 'VIDEO' && fs.tagName !== 'AUDIO' ? fs : document.documentElement;
    if (host.parentNode !== parent) parent.appendChild(host);
    host.setAttribute('dir', I.dir());
    applyThemeHost();
  }
  document.addEventListener('fullscreenchange', function () { if (host) { attachHost(); place(); } });

  function place() {
    if (!btn) return;
    var target = job ? job.el : current;
    var r = target ? eligible(target) : null;
    if (!r) {
      // telechargement suivi dont la video n'est plus visible (ou lance depuis la popup) : coin de la fenetre
      if (job) { btn.classList.add('corner'); edge.classList.remove('on'); if (veil) veil.style.display = 'none'; placeCorner(); placeTip(); return; }
      if (current) hide(true);
      return;
    }
    btn.classList.remove('corner');
    // lecteurs qui deborderaient leur cadre (YouTube) : on s'aligne sur le cadre du lecteur
    var frame = target.closest && target.closest('.html5-video-player');
    if (frame) r = frame.getBoundingClientRect();
    if (veil) {
      veil.style.display = '';
      veil.style.left = r.left + 'px'; veil.style.top = r.top + 'px';
      veil.style.width = r.width + 'px'; veil.style.height = r.height + 'px';
    }
    // signature v1 : fine barre de progression le long du bas de la video
    if (job && job.pct >= 0) {
      edge.style.left = Math.max(0, r.left) + 'px';
      edge.style.width = Math.max(0, Math.min(r.right, innerWidth) - Math.max(0, r.left)) + 'px';
      edge.style.top = (Math.min(r.bottom, innerHeight) - 3) + 'px';
      edge.classList.add('on');
    } else edge.classList.remove('on');
    var bw = btn.offsetWidth || 110, bh = btn.offsetHeight || 30;
    var bottom = pos === 'br' || pos === 'bl', leftSide = pos === 'tl' || pos === 'bl';
    // en bas : au-dessus de la barre de lecture du lecteur (environ 56 px)
    var top = bottom ? Math.min(r.bottom - bh - 58, innerHeight - bh - 4) : r.top + 10;
    top = Math.max(4, Math.max(r.top + 4, top));
    var left = leftSide ? r.left + 10 : r.right - bw - 10;
    btn.style.top = top + 'px';
    btn.style.left = Math.max(4, left) + 'px';
    placeTip();
    if (menuOpen) {
      var mw = menu.offsetWidth || 240, mh = menu.offsetHeight || 200;
      var mleft = leftSide ? r.left + 10 : r.right - mw - 10;
      var mtop = bottom ? top - mh - 6 : top + bh + 6;
      menu.style.top = Math.max(4, Math.min(mtop, innerHeight - mh - 4)) + 'px';
      menu.style.left = Math.max(4, Math.min(mleft, innerWidth - mw - 4)) + 'px';
    }
  }

  // bulle d'erreur : sous la pastille (au-dessus si la pastille est en bas de l'ecran)
  function placeTip() {
    if (!tip || !tip.classList.contains('on')) return;
    var b = btn.getBoundingClientRect(), tw = tip.offsetWidth || 240, th = tip.offsetHeight || 60;
    var x = I.dir() === 'rtl' ? b.left : b.right - tw;
    var y = b.bottom + 8 + th > innerHeight ? b.top - th - 8 : b.bottom + 8;
    tip.style.left = Math.max(4, Math.min(x, innerWidth - tw - 4)) + 'px';
    tip.style.top = Math.max(4, y) + 'px';
  }

  function placeCorner() {
    var bw = btn.offsetWidth || 180, bh = btn.offsetHeight || 32;
    btn.style.top = Math.max(4, innerHeight - bh - 20) + 'px';
    btn.style.left = Math.max(4, I.dir() === 'rtl' ? 20 : innerWidth - bw - 20) + 'px';
  }

  // Script orphelin : l'extension a ete rechargee / mise a jour pendant que la page restait ouverte (YouTube ne
  // recharge jamais la page entre deux videos). Il ne peut plus joindre le service worker : sa feuille de style ne
  // vient jamais et le bouton s'affichait SANS STYLE, gros et gris en bas a gauche (ShowF, 20/10). Il se retire.
  var dead = false;
  function alive() { try { return !!(chrome.runtime && chrome.runtime.id); } catch (e) { return false; } }
  function die() {
    dead = true;
    if (host && host.parentNode) host.parentNode.removeChild(host);
  }
  function show(el) {
    if (dead) return;
    if (!alive()) { die(); return; }
    if (!enabled || !ready) return;
    if (job) {
      // le bouton est pris par un telechargement suivi ; une fois fini, survoler une autre video le libere
      if (finalJob() && el !== job.el) endJob(); else { place(); return; }
    }
    if (document.fullscreenElement === el) return;   // la video elle-meme en plein ecran : rien a recouvrir
    ensureUi();
    clearTimeout(hideTimer);
    if (current !== el) { closeMenu(); current = el; refreshSub(); }
    btn.classList.add('on');
    place();
    requestAnimationFrame(place);
  }
  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(function () { hide(false); }, menuOpen ? 2500 : 1200);
  }
  function hide(now) {
    clearTimeout(hideTimer);
    if (!btn) return;
    if (job) { closeMenu(); return; }   // la progression reste affichee
    btn.classList.remove('on');
    closeMenu();
    current = null;
  }
  function closeMenu() {
    menuOpen = false;
    if (menu) { menu.classList.remove('on'); menu.textContent = ''; }
  }

  document.addEventListener('mousemove', function (e) {
    lastX = e.clientX; lastY = e.clientY;
    if (moveQueued) return;
    moveQueued = true;
    setTimeout(function () {
      moveQueued = false;
      if (!enabled) return;
      var el = mediaAt(lastX, lastY);
      if (el) show(el);
      else if (current) scheduleHide();
    }, 80);
  }, { passive: true, capture: true });
  addEventListener('scroll', function () { if (current) place(); }, { passive: true, capture: true });
  addEventListener('resize', function () { if (current) place(); }, { passive: true });

  // ---------- actions ----------
  // Lancement depuis le bouton : le bouton reste sur la video et montre la preparation, la progression,
  // puis « Termine » (clic = lire) ou l'erreur. Le service worker pousse les etats suivants (message « job »).
  function send(msg) {
    msg.toast = false;
    var el = current;
    closeMenu();
    setJob({ status: 'prep' }, el);
    return chrome.runtime.sendMessage(msg).then(function (res) {
      res = res || { ok: false, error: '?' };
      // meme traduction que le service worker (qui pousse aussi l'etat) : pas d'ecart si son message se perd
      if (res.ok && res.via === 'capture') setJob({ status: 'capture', pct: 0 });
      else if (res.ok && res.id) { if (job && job.st.status === 'prep') setJob({ status: 'queued', id: res.id }); }
      else if (res.ok) setJob({ status: res.via === 'chrome' ? 'chrome' : 'sent', bySite: !!res.bySite });
      else if (res.error === 'duplicate') setJob({ status: 'dup', id: res.id });
      else setJob({ status: 'error', error: res.error });
    }, function (e) { setJob({ status: 'error', error: e.message }); });
  }

  // ---------- telechargement suivi : etat affiche sur le bouton ----------
  var job = null;   // { el, st, timer }
  var SPIN = '<span class="kspin" aria-hidden="true"></span>';
  var PLAY = svg('play'), CHECK = svg('check'), ALERT = svg('alert');
  // anneau de progression (v1) : cercle r=9, circonference 56.55
  function ring(pct) {
    var c = 56.55, off = c * (1 - Math.max(0, Math.min(100, pct)) / 100);
    return '<svg class="kring" viewBox="0 0 24 24" aria-hidden="true"><circle class="bg" cx="12" cy="12" r="9"/>' +
      '<circle class="fg" cx="12" cy="12" r="9" stroke-dasharray="56.55" stroke-dashoffset="' + off.toFixed(2) + '" transform="rotate(-90 12 12)"/></svg>';
  }
  var FINAL = ['done', 'dup', 'error', 'chrome', 'sent'];
  function finalJob() { return !!job && FINAL.indexOf(job.st.status) >= 0; }

  function biggestMedia() {
    var best = null, area = 0;
    media.forEach(function (el) {
      var r = eligible(el);
      if (r && r.width * r.height > area) { best = el; area = r.width * r.height; }
    });
    return best;
  }

  function setJob(st, el) {
    if (!st) return;
    if (st.status === 'gone') { endJob(); return; }   // retire de la liste dans le programme
    if (!job) job = { el: null, st: st };
    if (el) job.el = el;
    if (!job.el || !job.el.isConnected) job.el = biggestMedia();
    // un etat tardif (ex. « queued ») ne doit pas effacer un etat plus avance deja recu
    if (job.st && FINAL.indexOf(job.st.status) >= 0 && FINAL.indexOf(st.status) < 0 && job.st.id && job.st.id === st.id) return;
    if (st.id == null && job.st && job.st.id != null && FINAL.indexOf(st.status) < 0) st.id = job.st.id;
    job.st = st;
    clearTimeout(job.timer);
    if (finalJob()) job.timer = setTimeout(endJob, st.status === 'error' ? 15000 : 12000);
    renderJob();
  }

  function endJob() {
    if (!job) return;
    clearTimeout(job.timer);
    job = null;
    if (!btn) return;
    if (veil) veil.className = 'kveil';
    btn.classList.remove('on');
    resetIdle();
    hide(true);
  }

  var humanErr = I.humanErr;

  // « reste 3:10 · fin 14:32 » : temps restant et heure de fin estimee (demande de ShowF, 20/10)
  function etaHtml(left) {
    if (!(left > 0) || !isFinite(left)) return '';
    var end = new Date(Date.now() + left * 1000);
    var hm = (end.getHours() < 10 ? '0' : '') + end.getHours() + ':' + (end.getMinutes() < 10 ? '0' : '') + end.getMinutes();
    return '<span class="dim"> · ' + esc(I.t('leftShort', { t: I.fmtLeft(left) })) + ' · <span class="num">' + esc(I.t('endAt', { h: hm })) + '</span></span>';
  }
  // capture : pas d'estimation venue du programme ; on la tire du rythme du pourcentage depuis le debut
  function capLeft(pct) {
    var now = Date.now();
    if (!job.pk || pct < job.pk.p) job.pk = { p: pct, t: now };
    var dp = pct - job.pk.p, dt = (now - job.pk.t) / 1000;
    if (dp < 1 || dt < 3) return 0;
    return (100 - pct) * dt / dp;
  }
  function renderJob() {
    if (!job) return;
    ensureUi();
    var st = job.st, cls = 'kbtn on job go', icon = SPIN, html = null, text = '', pct = -1, tip0 = '', tail = '', errText = '';
    switch (st.status) {
      case 'prep': text = I.t('jobPrep'); break;
      case 'queued': text = I.t('jobQueued'); break;
      case 'capture':
        pct = st.pct || 0; icon = ring(pct);
        // YouTube direct (v0.7.12) : comme un telechargement, pourcentage + temps restant + heure de fin
        if (st.direct) html = '<span class="num">' + esc(I.t('pct', { p: Math.floor(pct) })) + '</span>' + etaHtml(capLeft(pct));
        else html = esc(I.t('jobCapture', { p: Math.floor(pct) })) + etaHtml(capLeft(pct));
        break;
      case 'downloading': {
        if (st.total > 0) {
          pct = Math.min(100, st.received / st.total * 100);
          icon = ring(pct);
          html = '<span class="num">' + esc(I.t('pct', { p: Math.floor(pct) })) + '</span>' + etaHtml(st.eta);
        } else {
          text = st.received > 0 ? I.fmtSize(st.received) : I.t('jobPrep');
        }
        tip0 = [st.name || '', st.speed > 0 ? I.t('perSec', { s: I.fmtSize(st.speed) }) : ''].filter(Boolean).join(' · ');
        break;
      }
      case 'merging': text = I.t('jobMerging'); break;
      case 'paused': icon = ARROW; text = I.t('st_paused'); if (st.total > 0) pct = Math.min(100, st.received / st.total * 100); break;
      case 'done': case 'dup': icon = CHECK; tail = PLAY; cls = 'kbtn on job jdone'; text = I.t(st.status === 'dup' ? 'jobDup' : 'jobDone'); tip0 = st.name || ''; break;
      case 'chrome': case 'sent': icon = CHECK; cls = 'kbtn on job jdone'; text = I.t(st.status === 'chrome' ? (st.bySite ? 'sentChromeSite' : 'sentChrome') : 'sent'); break;
      case 'error': icon = ALERT; cls = 'kbtn on job jerr'; text = I.t('jobErrShort'); errText = humanErr(st.error, st.code); break;
      default: text = I.t('jobPrep');
    }
    job.pct = pct;
    if (st.status === 'capture' && !st.direct) {
      veil.querySelector('.vp').textContent = I.t('pct', { p: Math.floor(pct) });
      veil.querySelector('.vt').textContent = I.t('veilTitle');
      veil.querySelector('.vs').textContent = I.t('veilSub');
      veil.querySelector('.vb i').style.width = Math.max(0, pct).toFixed(1) + '%';
      veil.setAttribute('dir', I.dir());
      veil.className = 'kveil on';
    } else veil.className = 'kveil';
    var corner = btn.classList.contains('corner');
    btn.className = cls + (corner ? ' corner' : '');
    btn.querySelector('.kic').innerHTML = icon;
    var lbl = btn.querySelector('.lbl');
    if (html != null) lbl.innerHTML = html; else lbl.textContent = text;
    btn.querySelector('.ktail').innerHTML = tail;
    btn.querySelector('.kx').title = I.t('jobHide');
    btn.querySelector('.kfill').style.width = pct >= 0 ? pct.toFixed(1) + '%' : '0';
    edge.firstChild.style.width = pct >= 0 ? pct.toFixed(1) + '%' : '0';
    btn.title = tip0 || (FINAL.indexOf(st.status) < 0 ? I.t('jobShowApp') : '');
    // erreur : bulle explicative en clair sous la pastille
    tip.textContent = '';
    if (errText) {
      var b = document.createElement('b'); b.textContent = I.t('jobErrShort');
      var sp = document.createElement('span'); sp.textContent = errText;
      tip.appendChild(b); tip.appendChild(sp);
      tip.setAttribute('dir', I.dir());
      tip.className = 'ktip on';
    } else tip.className = 'ktip';
    attachHost();
    place();
    requestAnimationFrame(place);
  }
  function esc(t) { return String(t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  // Clic sur le bouton pendant le suivi : termine -> lire le fichier ; en cours -> ouvrir le programme.
  function jobClick() {
    var st = job.st;
    if ((st.status === 'done' || st.status === 'dup') && st.id) {
      chrome.runtime.sendMessage({ type: 'native', msg: { cmd: 'control', id: st.id, action: 'open' } }).then(function (r) {
        if (r && r.ok) endJob(); else showToast(I.t('error', { e: humanErr(r && (r.error || r.detail)) }), false);
      }, function () { });
    } else if (st.status === 'error' && (st.error === 'needApp' || st.error === 'needUpdate')) {
      chrome.runtime.sendMessage({ type: 'openFix' }).catch(function () { });
      endJob();
    } else if (FINAL.indexOf(st.status) >= 0) endJob();
    else chrome.runtime.sendMessage({ type: 'native', msg: { cmd: 'show' } }).catch(function () { });
  }

  // Etats pousses par le service worker (vers CE cadre : celui qui a lance, ou la page principale)
  chrome.runtime.onMessage.addListener(function (m) {
    if (!m || m.type !== 'job') return;
    I.init().then(function () { setJob(m.state); });
  });

  function kindLabel(it) {
    if (it.kind === 'site') return it.site || I.t('kindVideo');   // nom du site donne par le programme
    if (it.kind === 'hls') return I.t('kindHls');
    if (it.kind === 'dash') return I.t('kindDash');
    return I.t(it.audio ? 'kindAudio' : 'kindVideo');
  }

  function directSrc(el) {
    var s = el.currentSrc || el.src || '';
    return /^https?:/i.test(s) ? s : '';
  }

  // Medias de l'onglet (cache court : lu au survol pour afficher « 1080p » avant le clic)
  var mediaCache = null, mediaAt0 = 0, mediaP = null;
  function getMediaCached() {
    if (mediaCache && Date.now() - mediaAt0 < 2000) return Promise.resolve(mediaCache);
    if (mediaP) return mediaP;
    mediaP = chrome.runtime.sendMessage({ type: 'getMedia' }).then(function (r) {
      mediaP = null; mediaCache = r || {}; mediaAt0 = Date.now(); return mediaCache;
    }, function (e) { mediaP = null; throw e; });
    return mediaP;
  }
  // Deux presentations (v0.7.5) : SIMPLE (defaut) = des mots, « qualite normale » / « fichier leger », la taille a
  // cote ; AVANCEE (reglage « mode avance », ou « Plus d'options » dans le menu) = toutes les qualites en chiffres.
  function buildEntries(r, el, full) {
    var items = (r && r.items) || [];
    var st = (r && r.settings) || settingsNow;
    var q = st.quality || 'normal';
    var adv = full || !!st.advanced;
    var entries = [], def = null;
    // le media dont l'adresse est celle de la video survolee passe en tete : c'est lui que l'utilisateur veut
    var mine = el ? sameMedia(directSrc(el)) : null;
    items.slice().sort(function (a, b) {
      var oa = mine && mine(a.url) ? 0 : 1, ob = mine && mine(b.url) ? 0 : 1;
      var ma = a.kind === 'file' ? 1 : 0, mb = b.kind === 'file' ? 1 : 0;
      return (oa - ob) || (ma - mb) || ((b.size || 0) - (a.size || 0));
    }).forEach(function (it, idx) {
      if ((it.kind === 'hls' || it.kind === 'site') && it.variants && it.variants.length) {
        var vEntry = function (v, label, sub, short) {
          return { label: label, sub: sub, size: v.size ? I.fmtSize(v.size) : '', short: short, audio: it.audio,
            msg: { type: 'download', itemId: it.id, variantUrl: v.url } };
        };
        if (adv) {
          var dv = showlefPickVariant(it.variants, q);
          it.variants.forEach(function (v) {
            var e = vEntry(v, v.label, kindLabel(it), v.label);
            entries.push(e);
            if (!def && v === dv) def = e;
          });
        } else if (idx === 0) {
          // media principal : deux choix au plus, en mots ; deux fois la meme variante => un seul choix
          var vn = showlefPickVariant(it.variants, 'normal'), vs = showlefPickVariant(it.variants, 'small');
          var en = vEntry(vn, I.t('qNormal'), I.t('qNormalSub'), I.t('qNormalShort'));
          entries.push(en);
          var es = vs !== vn ? vEntry(vs, I.t('qSmall'), I.t('qSmallSub'), I.t('qSmallShort')) : null;
          if (es) entries.push(es);
          // defaut : le reglage (une hauteur choisie en mode avance reste respectee)
          var vq = showlefPickVariant(it.variants, q);
          def = vq === vn ? en : es && vq === vs ? es : vEntry(vq, vq.label, '', vq.label);
        }
      } else if (adv || !entries.length) {
        var sub = [kindLabel(it), (it.ext || '').toUpperCase(), it.duration ? I.fmtDur(it.duration) : ''].filter(Boolean).join(' · ');
        entries.push(adv
          ? { label: shortName(it.url), sub: sub, size: I.fmtSize(it.size), short: (it.ext || '').toUpperCase(), audio: it.audio, msg: { type: 'download', itemId: it.id } }
          : { label: kindLabel(it), sub: [shortName(it.url), it.duration ? I.fmtDur(it.duration) : ''].filter(Boolean).join(' · '), size: I.fmtSize(it.size), short: '', audio: it.audio, msg: { type: 'download', itemId: it.id } });
      }
    });
    var src = el ? directSrc(el) : '';
    if (src && !items.some(function (i) { return i.url === src; }) && (adv || !entries.length)) {
      entries.push({ label: adv ? shortName(src) : I.t(el.tagName === 'AUDIO' ? 'kindAudio' : 'kindVideo'), sub: adv ? I.t(el.tagName === 'AUDIO' ? 'kindAudio' : 'kindVideo') : shortName(src),
        short: '', audio: el.tagName === 'AUDIO', msg: { type: 'downloadUrl', url: src } });
    }
    return { items: items, entries: entries, def: def || entries[0] || null, src: src, adv: adv };
  }
  // Meme media, a des parametres de plage pres (range=, bytes=... que le lecteur ajoute en lisant).
  function normSrc(u) {
    try { var x = new URL(u); ['range', 'bytes', 'rn', 'rbuf'].forEach(function (k) { x.searchParams.delete(k); }); x.hash = ''; return x.toString(); }
    catch (e) { return u || ''; }
  }
  function sameMedia(src) {
    if (!src) return null;
    var n = normSrc(src);
    return function (u) { return !!u && normSrc(u) === n; };
  }
  // « 1080p » a cote de « Telecharger » (qualite qui partira au clic)
  function refreshSub() {
    if (!btn || job) return;
    var sub = btn.querySelector('.ksub');
    var el = current;
    getMediaCached().then(function (r) {
      if (job || el !== current) return;
      if (appBad(r)) { badButton(r.app); requestAnimationFrame(place); return; }
      var b = buildEntries(r, el);
      sub.textContent = b.def ? (b.def.short || '') : '';
      btn.title = r && r.appUpdate ? I.t('updTitle', { v: SHOWLEF_APP_LATEST }) : '';
      // telechargement coupe de cette video : « Reprendre (63 %) » (programme 0.7.12)
      var rs = b.items.find(function (i) { return i.kind === 'site' && i.resume && i.resume.video; });
      btn.querySelector('.lbl').textContent = rs ? I.t('resumeDl', { p: rs.resume.video }) : I.t('download');
      requestAnimationFrame(place);
    }, function () { });
  }
  // Programme absent ou trop vieux (v0.7.11) : pas de « Telecharger » qui echouerait au clic, mais « ShowLef a
  // reparer » ; le clic ouvre la page de reparation (installeur + verification automatique).
  function appBad(r) { return !!r && (r.app === 'old' || r.app === 'missing'); }
  function badButton(state) {
    btn.className = 'kbtn jerr' + (btn.classList.contains('on') ? ' on' : '') + (btn.classList.contains('corner') ? ' corner' : '');
    btn.querySelector('.kic').innerHTML = ALERT;
    btn.querySelector('.lbl').textContent = I.t('fixButton');
    btn.querySelector('.ksub').textContent = '';
    btn.querySelector('.kq').style.display = 'none';   // pas de choix de qualite tant que rien ne peut partir
    btn.title = I.t(state === 'old' ? 'fixOldTitle' : 'fixMissingTitle');
  }

  // Demarre la video de la page (muette, le temps de la preparation) et telecharge des que son flux est connu.
  var starting = false;
  function startAndWait(el) {
    if (starting) return;
    starting = true;
    setJob({ status: 'prep' }, el);
    var wasPaused = el.paused, wasMuted = el.muted, t0 = Date.now();
    try { el.muted = true; var pr = el.play(); if (pr && pr.catch) pr.catch(function () { }); } catch (e) { }
    function restore() { starting = false; try { el.muted = wasMuted; if (wasPaused) el.pause(); } catch (e) { } }
    (function poll() {
      mediaCache = null;
      getMediaCached().then(function (r) {
        var b = buildEntries(r, el);
        if (b.def) { restore(); send(b.def.msg); return; }
        if (Date.now() - t0 > 12000) { restore(); send({ type: 'downloadPage' }); return; }
        setTimeout(poll, 700);
      }, function () {
        if (Date.now() - t0 > 12000) { restore(); send({ type: 'downloadPage' }); return; }
        setTimeout(poll, 700);
      });
    })();
  }

  // Clic principal : la qualite par defaut part tout de suite ; la partie « 1080p ⌄ » ouvre le menu.
  function onButton(wantMenu, full) {
    if (!current) return;
    var el = current;
    mediaCache = null;
    getMediaCached().then(function (r) {
      if (appBad(r)) { closeMenu(); chrome.runtime.sendMessage({ type: 'openFix' }).catch(function () { }); return; }
      var b = buildEntries(r, el, full), items = b.items, entries = b.entries, src = b.src;
      var fmt = ((r && r.settings && r.settings.audioFormat) || 'mp3').toUpperCase();
      if (!wantMenu && b.def) { send(b.def.msg); return; }
      // Rien de detecte (lecture pas encore lancee, flux pas encore demande) : au lieu d'une erreur, on LANCE la video
      // (le clic est un geste de l'utilisateur), on attend le flux, puis on telecharge ; sans flux => la page entiere.
      if (!wantMenu && !b.def && el && el.tagName === 'VIDEO') { startAndWait(el); return; }

      var extra = [];
      // programme en retard sur la derniere version : la mise a jour est proposee en tete du menu (rien n'est bloque)
      if (r && r.appUpdate) extra.push({ label: I.t('updMenu', { v: SHOWLEF_APP_LATEST }), icon: 'dl',
        local: function () { closeMenu(); chrome.runtime.sendMessage({ type: 'openFix', update: true, auto: true }).catch(function () { }); } });
      var isOwn = sameMedia(src), own = isOwn ? items.find(function (i) { return isOwn(i.url); }) : null;
      var best = own || items.find(function (i) { return i.kind !== 'file'; }) || items[0];
      if (best) extra.push({ label: I.t('audioOnly', { f: fmt }) + (best.resume && best.resume.audio ? ' · ' + I.t('resumeDl', { p: best.resume.audio }) : ''),
        icon: 'note', msg: { type: 'download', itemId: best.id, audioOnly: true } });
      else if (src) extra.push({ label: I.t('audioOnly', { f: fmt }), icon: 'note', msg: { type: 'downloadUrl', url: src, audioOnly: true } });
      if (!b.adv) {
        // le reste (toutes les qualites, page entiere, masquer) derriere un seul lien
        extra.push({ label: I.t('moreOptions'), icon: 'more', muted: true, keep: true, local: function () { onButton(true, true); } });
      } else {
        if (!(r && r.store && /youtu/.test(location.hostname))) extra.push({ label: I.t('wholePage'), icon: 'globe', msg: { type: 'downloadPage' } });
        extra.push({ label: I.t('hideHere'), icon: 'eyeoff', local: hideSite, muted: true });
      }
      openMenu(entries, extra, !entries.length);
    }, function (e) { showToast(I.t('error', { e: humanErr(e.message) }), false); });
  }

  function shortName(url) {
    try {
      var p = decodeURIComponent(new URL(url).pathname.split('/').pop() || '');
      if (p.length > 38) p = p.slice(0, 18) + '…' + p.slice(-16);
      return p || new URL(url).hostname;
    } catch (e) { return url.slice(0, 38); }
  }

  function openMenu(entries, extra, empty) {
    menu.textContent = '';
    menu.setAttribute('dir', I.dir());
    var h = document.createElement('div');
    h.className = empty ? 'knote' : 'khead';
    h.textContent = empty ? I.t('noMedia') : I.t('pickQuality');
    menu.appendChild(h);
    entries.forEach(function (e) { menu.appendChild(row(e, false)); });
    if (extra.length && entries.length) {
      var sep = document.createElement('div');
      sep.className = 'ksep';
      menu.appendChild(sep);
    }
    extra.forEach(function (e) { menu.appendChild(row(e, true)); });
    menuOpen = true;
    menu.classList.add('on');
    place();
  }

  function row(e, secondary) {
    var b = document.createElement('button');
    b.className = 'krow' + (secondary ? ' sec' : '') + (e.muted ? ' muted' : '');
    b.innerHTML = '<span class="kri">' + svg(e.icon || (e.audio ? 'note' : 'film')) + '</span>';
    var tx = document.createElement('span');
    tx.className = 'ktx';
    var l = document.createElement('span');
    l.className = 'kl';
    l.textContent = e.label;
    tx.appendChild(l);
    if (e.sub) {
      var s = document.createElement('span');
      s.className = 'ks';
      s.textContent = e.sub;
      tx.appendChild(s);
    }
    b.appendChild(tx);
    if (e.size) {
      var z = document.createElement('span');
      z.className = 'ksz';
      z.textContent = e.size;
      b.appendChild(z);
    }
    b.addEventListener('click', function (ev) { ev.preventDefault(); ev.stopPropagation(); if (e.local) e.local(); else send(e.msg); }, true);
    return b;
  }

  // « Pas de bouton sur ce site » : memorise pour le site de l'onglet, reversible depuis la popup.
  function hideSite() {
    var key = showlefSiteKey(topUrl);
    if (!key) return;
    chrome.storage.local.get('settings').then(function (r) {
      var st = r.settings || {};
      st.siteOff = (st.siteOff || []).filter(function (h) { return h !== key; }).concat([key]);
      return chrome.storage.local.set({ settings: st });
    }).then(function () {
      hide(true);
      // « Annuler » : on remet le bouton tout de suite, sans passer par la popup
      showToast(I.t('hiddenHere'), true, { label: I.t('undo'), run: function () {
        chrome.storage.local.get('settings').then(function (r) {
          var st = r.settings || {};
          st.siteOff = (st.siteOff || []).filter(function (h) { return h !== key; });
          return chrome.storage.local.set({ settings: st });
        });
      } });
    });
  }

  var toastTimer = 0;
  function showToast(text, ok, action) {
    ensureUi();
    toastEl.textContent = '';
    var t = document.createElement('span');
    t.textContent = text;
    toastEl.appendChild(t);
    if (action) {
      var b = document.createElement('button');
      b.className = 'kact';
      b.textContent = action.label;
      b.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); action.run(); toastEl.className = 'ktoast'; }, true);
      toastEl.appendChild(b);
    }
    toastEl.className = 'ktoast on ' + (ok ? 'ok' : 'ko') + (action ? ' act' : '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.className = 'ktoast'; }, action ? 10000 : 2800);   // une question laisse le temps de lire
  }

  // Vignette pour le programme (champ `thumb` de add) : affiche de la <video>, sinon og:image / twitter:image.
  chrome.runtime.onMessage.addListener(function (m, sender, reply) {
    if (!m || m.type !== 'getThumb') return;
    var ok = function (u) { return typeof u === 'string' && /^https?:\/\//i.test(u) ? u : ''; };
    var vids = Array.prototype.slice.call(document.querySelectorAll('video'));
    var v = vids.find(function (x) { return m.mediaUrl && (x.currentSrc === m.mediaUrl || x.src === m.mediaUrl); }) ||
      vids.filter(function (x) { return ok(x.poster); }).sort(function (a, b) {
        return b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight;
      })[0];
    var t = v ? ok(v.poster) : '';   // propriete .poster : deja une adresse absolue
    if (!t && window === window.top) {
      var meta = document.querySelector('meta[property="og:image:secure_url"], meta[property="og:image"], meta[name="og:image"], meta[name="twitter:image"], meta[property="twitter:image"], meta[name="twitter:image:src"]');
      if (meta) { try { t = ok(new URL(meta.getAttribute('content') || '', location.href).href); } catch (e) { } }
    }
    reply({ thumb: t });
  });

  chrome.runtime.onMessage.addListener(function (m) {
    if (!m || m.type !== 'rescan') return;
    reported = {};
    scan(document.documentElement);
    media.forEach(function (el) { report(el); });
  });

  // Liste HLS demandee par le service worker : lue depuis CE cadre (son origine et son Referer).
  chrome.runtime.onMessage.addListener(function (m, sender, reply) {
    if (!m || m.type !== 'fetchText') return;
    // sans cookies d'abord : un CDN qui repond « Access-Control-Allow-Origin: * » refuse une requete avec cookies
    var get = function (cred) { return fetch(m.url, { credentials: cred }).then(function (r) { return r.ok ? r.text() : ''; }); };
    get('same-origin').catch(function () { return ''; })
      .then(function (t) { return t || get('include').catch(function () { return ''; }); })
      .then(function (t) { reply({ text: (t || '').slice(0, 2000000) }); });
    return true;
  });

  // Morceau demande par le service worker pour un telechargement qui passe par le navigateur (v0.7.30) : lu depuis CE
  // cadre, comme le fait le lecteur de la page (meme origine, meme Referer), rendu en base64.
  chrome.runtime.onMessage.addListener(function (m, sender, reply) {
    if (!m || m.type !== 'fetchBin') return;
    var get = function (cred) {
      return fetch(m.url, { credentials: cred }).then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.blob();
      }).then(function (b) {
        return new Promise(function (ok, ko) {
          var fr = new FileReader();
          fr.onload = function () { ok(String(fr.result).split(',')[1] || ''); };
          fr.onerror = function () { ko(new Error('read')); };
          fr.readAsDataURL(b);
        });
      });
    };
    get('same-origin').catch(function () { return get('include'); })
      .then(function (b64) { reply({ ok: true, b64: b64 }); }, function (e) { reply({ ok: false, error: String(e && e.message || e) }); });
    return true;
  });

  // Notifications du service worker (menu contextuel, interception) : cadre principal seulement.
  if (window === window.top) {
    chrome.runtime.onMessage.addListener(function (m) {
      if (m && m.type === 'toast') {
        I.init().then(function () {
          showToast(m.text, m.ok, m.action ? { label: m.action.label, run: function () { chrome.runtime.sendMessage(m.action.msg).catch(function () { }); } } : null);
        });
      }
    });
  }
})();
