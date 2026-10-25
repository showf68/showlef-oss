// ShowLef - « format compatible » (monde de la PAGE, document_start).
// Enregistre dynamiquement par le service worker seulement si le reglage est actif, et seulement sur les sites que
// le PROGRAMME marque « compat ». Un lecteur choisit ses formats d'apres ce que le navigateur dit savoir lire : en
// refusant VP9, AV1 et Opus, le site envoie du H.264 + AAC, que tout lecteur et tout appareil ouvrent.
// Effet aussi sur la lecture dans Chrome (meme qualite maximale), c'est le prix de la compatibilite.
(function () {
  if (window.__showlefH264) return;
  window.__showlefH264 = true;
  var refused = /vp0?8|vp0?9|av01|opus|vorbis/i;
  var ms = window.MediaSource;
  if (ms && ms.isTypeSupported) {
    var orig = ms.isTypeSupported.bind(ms);
    ms.isTypeSupported = function (t) { return refused.test(t || '') ? false : orig(t); };
  }
  if (window.ManagedMediaSource && window.ManagedMediaSource.isTypeSupported) {
    var origM = window.ManagedMediaSource.isTypeSupported.bind(window.ManagedMediaSource);
    window.ManagedMediaSource.isTypeSupported = function (t) { return refused.test(t || '') ? false : origM(t); };
  }
  var cpt = HTMLMediaElement.prototype.canPlayType;
  HTMLMediaElement.prototype.canPlayType = function (t) { return refused.test(t || '') ? '' : cpt.call(this, t); };
  if (navigator.mediaCapabilities && navigator.mediaCapabilities.decodingInfo) {
    var di = navigator.mediaCapabilities.decodingInfo.bind(navigator.mediaCapabilities);
    navigator.mediaCapabilities.decodingInfo = function (cfg) {
      var c = cfg && ((cfg.video && cfg.video.contentType) || (cfg.audio && cfg.audio.contentType)) || '';
      if (refused.test(c)) return Promise.resolve({ supported: false, smooth: false, powerEfficient: false });
      return di(cfg);
    };
  }
})();
