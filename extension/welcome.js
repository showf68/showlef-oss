// Page d'accueil (ouverte a l'installation) ET page de reparation (welcome.html?fix=1, v0.7.11) : ouverte par le
// service worker des qu'un telechargement est demande alors que le programme est absent ou trop vieux pour cette
// extension (SHOWLEF_APP_MIN, config.js).
// Une extension n'a pas le droit de lancer un programme : on telecharge l'installeur, l'utilisateur l'ouvre,
// et la page voit toute seule le programme arriver A LA BONNE VERSION (essai de connexion toutes les 2 s).
(function () {
  var I = SHOWLEF_I18N, HOST = 'com.showlef.app';
  var SETUP_URL = 'https://ext.i-call.me/downloads/ShowLef-Setup.exe';
  var $ = function (id) { return document.getElementById(id); };
  var fix = /[?&]fix=1/.test(location.search);
  // update=1 : le programme marche mais une version plus recente est publiee => on vise SHOWLEF_APP_LATEST
  var upd = fix && /[?&]update=1/.test(location.search), want = upd ? SHOWLEF_APP_LATEST : SHOWLEF_APP_MIN;
  var auto = upd && /[?&]auto=1/.test(location.search);
  var state = 'unknown', version = '', foundVer = '';
  // programme >= 0.7.13 : il se met a jour lui-meme (telecharge l'installeur, verifie l'empreinte, le lance)
  var self = false, updating = 0, autoDone = false;

  function ping() {
    return new Promise(function (resolve) {
      try {
        chrome.runtime.sendNativeMessage(HOST, { cmd: 'ping' }, function (r) {
          resolve(chrome.runtime.lastError ? null : r);
        });
      } catch (e) { resolve(null); }
    });
  }
  function texts() {
    document.documentElement.lang = I.getLang();
    document.documentElement.dir = I.dir();
    var old = state === 'old' || (state === 'ok' && foundVer);
    $('title').textContent = I.t(upd ? 'updTitle' : fix ? 'fxTitle' : 'wTitle', { v: SHOWLEF_APP_LATEST });
    $('lead').textContent = !fix ? I.t('wLead')
      : upd && old ? I.t('updHelp', { v: foundVer || version || '?', m: want })
      : old ? I.t('fxLeadOld', { v: foundVer || version || '?', m: want }) : I.t('fxLeadMissing');
    $('s1').textContent = !fix ? I.t('wStep1') : I.t(upd && old ? 'updStep1' : old ? 'fxStep1Old' : 'fxStep1Missing', { v: foundVer || version, m: want });
    $('s2').textContent = I.t(upd && self ? 'updStep2Self' : 'wStep2');
    $('s3').textContent = I.t(upd ? 'updStep3' : fix ? 'fxStep3' : 'wStep3');
    $('getTx').textContent = I.t(upd && self ? 'updNow' : old ? 'getUpdate' : 'getApp');
  }
  function render() {
    var ok = state === 'ok';
    texts();
    $('li2').className = ok ? 'done' : 'now';
    $('li3').className = ok ? (fix ? 'done' : 'now') : '';
    $('get').hidden = ok;
    if (ok) $('hint').hidden = true;
    $('status').className = 'status' + (ok ? ' ok' : '');
    $('statusTx').textContent = ok ? (upd ? I.t('updReady', { v: version }) : fix ? I.t('fxReady', { v: version }) : I.t('wReady')) : I.t('wWaiting');
  }
  function manual() {
    chrome.downloads.download({ url: SETUP_URL, saveAs: false }).catch(function () { location.href = SETUP_URL; });
    hint(I.t('gotApp'));
  }
  function hint(t) { $('hint').textContent = t; $('hint').hidden = false; }
  function selfRun() {
    updating = Date.now();
    $('get').disabled = true;
    hint(I.t('updRunning'));
    chrome.runtime.sendNativeMessage(HOST, { cmd: 'selfupdate', sha256: SHOWLEF_APP_LATEST_SHA256, version: SHOWLEF_APP_LATEST }, function (r) {
      void chrome.runtime.lastError;
      if (r && r.ok) return;   // l'installeur tourne : le programme revient tout seul, la boucle le voit
      updating = 0;
      $('get').disabled = false;
      if (r && r.error === 'busy') { hint(I.t('updBusy')); return; }
      // telechargement ou empreinte en echec : on repasse par l'installeur a ouvrir soi-meme
      manual();
      hint(I.t('updFail'));
    });
  }
  $('get').addEventListener('click', function () { if (upd && self) selfRun(); else manual(); });
  function loop() {
    ping().then(function (r) {
      var s = showlefAppState(r);
      if (updating && s === 'missing' && Date.now() - updating < 90000) { setTimeout(loop, 1000); return; }   // installeur au travail
      if (updating && Date.now() - updating >= 90000 && s !== 'ok') { updating = 0; $('get').disabled = false; hint(I.t('updFail')); }
      self = !!(r && r.selfupdate);
      version = (r && r.ok && r.version) || '';
      if (s === 'ok' && upd && showlefVerCmp(version, want) < 0) s = 'old';   // pas encore la derniere version
      if (s === 'old') foundVer = version;   // garde la version trouvee pour expliquer ce qui a ete repare
      if (s === 'ok') updating = 0;
      if (s !== state) {
        state = s; render();
        if (auto && !autoDone && s === 'old' && self) { autoDone = true; selfRun(); }
        // le service worker garde l'etat en cache : on le force a relire, le bouton revient tout de suite
        if (s === 'ok') chrome.runtime.sendMessage({ type: 'appState', force: true }).catch(function () { });
      }
      if (state !== 'ok') setTimeout(loop, updating ? 1000 : 2000);
    });
  }
  I.init().then(function () { render(); loop(); });
})();
