// Configuration de construction. build.ps1 -Store -NoYouTube passe STORE_BUILD a true et remplit la liste des hotes
// bloques (version de secours si Google refuse) : rien n'y est detecte ni envoye, meme si le programme le propose.
var STORE_BUILD = false;
var SHOWLEF_BLOCKED_HOSTS = [];

// Cle d'un site pour les reglages « sur ce site » : nom d'hote sans « www. ».
function showlefSiteKey(url) {
  try { return new URL(url).hostname.toLowerCase().replace(/^www\./, ''); } catch (e) { return ''; }
}
function showlefSiteIn(list, url) {
  var k = showlefSiteKey(url);
  return !!k && (list || []).some(function (h) { return k === h || k.endsWith('.' + h); });
}

function showlefIsBlocked(url) {
  if (!STORE_BUILD || !url) return false;
  var h = '';
  try { h = new URL(url).hostname.toLowerCase(); } catch (e) { return false; }
  return SHOWLEF_BLOCKED_HOSTS.some(function (b) { return h === b || h.endsWith('.' + b); });
}

// Qualite en mots (v0.7.5) : 'normal' = 720p au plus (defaut), 'small' = 480p au plus, 'best' = la plus haute ;
// un nombre (mode avance, anciens reglages) = cette hauteur au plus. Sans hauteur sous le plafond : la plus petite.
var SHOWLEF_QCAP = { normal: 720, small: 480 };
function showlefQualityCap(q) {
  if (!q || q === 'normal' || q === 'small') return SHOWLEF_QCAP[q || 'normal'];
  if (q === 'best') return 0;
  return +q || SHOWLEF_QCAP.normal;
}
// valeur envoyee au programme, qui ne connait que 'best' ou une hauteur
function showlefQualityNum(q) { var c = showlefQualityCap(q); return c ? String(c) : 'best'; }
function showlefHeightOf(v) { var m = /(\d{3,4})p/.exec((v && v.label) || ''); return (v && v.height) || (m ? +m[1] : 0); }
function showlefPickVariant(vs, q) {
  if (!vs || !vs.length) return null;
  var hs = vs.filter(function (v) { return showlefHeightOf(v) > 0; })
    .sort(function (a, b) { return showlefHeightOf(b) - showlefHeightOf(a); });
  if (!hs.length) return vs[0];
  var cap = showlefQualityCap(q);
  if (!cap) return hs[0];
  return hs.find(function (v) { return showlefHeightOf(v) <= cap; }) || hs[hs.length - 1];
}

// Version minimale du PROGRAMME dont cette extension a besoin (v0.7.11). A monter des que l'extension envoie une
// commande ou un champ que les anciens programmes ne connaissent pas (0.7.10 : « ytprobe », « capture_abort »).
// Sans ca, une extension a jour face a un vieux programme echoue au clic sans dire pourquoi (MATEBOOK, 20/10).
var SHOWLEF_APP_MIN = '0.7.16';   // 0.7.22 : sites pilotes par le programme (pilot_*)
function showlefVerCmp(a, b) {
  var x = String(a || '0').split('.'), y = String(b || '0').split('.');
  for (var i = 0; i < Math.max(x.length, y.length); i++) {
    var d = (parseInt(x[i], 10) || 0) - (parseInt(y[i], 10) || 0);
    if (d) return d < 0 ? -1 : 1;
  }
  return 0;
}
// Etat du programme d'apres la reponse a « ping » : 'ok', 'old' (a mettre a jour) ou 'missing' (absent / muet).
// Derniere version du programme PUBLIEE (installeur ext.i-call.me). En dessous (mais au-dessus du minimum) rien n'est
// bloque : l'extension PROPOSE la mise a jour (bouton, popup, page ouverte apres une mise a jour de l'extension).
// Regle : quand l'extension se sert d'une nouvelle commande du programme, cette valeur passe a cette version dans le
// meme commit, et SHOWLEF_APP_MIN aussi si, sans elle, ca casse.
var SHOWLEF_APP_LATEST = '0.7.17';
// Empreinte de CET installeur (ext.i-call.me/downloads/ShowLef-Setup.exe) : un programme >= 0.7.13 se met a jour
// lui-meme (commande « selfupdate ») et ne lance l'installeur telecharge QUE s'il a exactement cette empreinte.
// Publier un nouvel installeur => changer les deux valeurs ensemble.
var SHOWLEF_APP_LATEST_SHA256 = '285019e4bad2103b4c39a048f8e43b069c26595e086e62167ed1816e563e32a2';
function showlefAppUpdate(p) {
  return !!(p && p.ok) && showlefVerCmp(p.version, SHOWLEF_APP_MIN) >= 0 && showlefVerCmp(p.version, SHOWLEF_APP_LATEST) < 0;
}
function showlefAppState(p) {
  if (!p || !p.ok) return 'missing';
  return showlefVerCmp(p.version, SHOWLEF_APP_MIN) < 0 ? 'old' : 'ok';
}
