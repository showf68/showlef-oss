// ShowLef - « comme IDM » (0.7.16) : TOUTE la connaissance des sites est ICI, dans le programme. L'extension n'est
// qu'un transport generique : elle recoit des regles (hotes, motifs d'adresse SANS regex), capture passivement les
// echanges qui correspondent, et execute des requetes bornees que le programme lui demande (hotes autorises par
// le programme, jamais une commande venue d'une page). Le programme decide de tout : quelle video, quels formats,
// quelles requetes, dans quel ordre, quand c'est fini.
//
// Protocole (commandes natives, une requete = une reponse JSON) :
//   pilot_rules                         -> { sites: [...] }  regles a poser dans l'extension
//   pilot_data  { page, rule, url, body } donnee capturee (corps en base64)      -> { media? }
//   pilot_page  { page, compat }         que sait-on de cette page ?             -> { media? , fetch? }
//   pilot_start { page, quality, audioOnly, compat, ... }                        -> { id, actions } ou { via:'app' }
//   pilot_step  { id, results:[{ rid, ok, status, body }] }                     -> { actions, pct, done?, error? }
//   pilot_abort { id }
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ---------- protobuf minimal (lecture + reecriture fidele) ----------
public class PbF {
    public int F; public int W; public ulong V; public byte[] B; public List<PbF> Sub;
    public static PbF Int(int f, ulong v) { return new PbF { F = f, W = 0, V = v }; }
    public static PbF Bytes(int f, byte[] b) { return new PbF { F = f, W = 2, B = b }; }
    public static PbF Msg(int f, List<PbF> s) { return new PbF { F = f, W = 2, Sub = s }; }
}

public static class Pb {
    public static List<PbF> Parse(byte[] b) { return Parse(b, 0, b.Length); }
    public static List<PbF> Parse(byte[] b, int off, int len) {
        var r = new List<PbF>();
        int p = off, end = off + len;
        while (p < end) {
            ulong key = Var(b, ref p, end);
            var x = new PbF { F = (int)(key >> 3), W = (int)(key & 7) };
            if (x.W == 0) x.V = Var(b, ref p, end);
            else if (x.W == 2) {
                ulong l = Var(b, ref p, end);
                if (l > (ulong)(end - p)) throw new FormatException("pb: longueur");
                x.B = new byte[l]; Buffer.BlockCopy(b, p, x.B, 0, (int)l); p += (int)l;
            } else if (x.W == 1 || x.W == 5) {
                int n = x.W == 1 ? 8 : 4;
                if (p + n > end) throw new FormatException("pb: fin");
                x.B = new byte[n]; Buffer.BlockCopy(b, p, x.B, 0, n); p += n;
            } else throw new FormatException("pb: type " + x.W);
            r.Add(x);
        }
        return r;
    }
    static ulong Var(byte[] b, ref int p, int end) {
        ulong v = 0; int s = 0;
        while (true) {
            if (p >= end || s > 63) throw new FormatException("pb: varint");
            byte x = b[p++];
            v |= (ulong)(x & 0x7f) << s; s += 7;
            if ((x & 0x80) == 0) return v;
        }
    }
    static void WVar(MemoryStream o, ulong v) { while (v >= 0x80) { o.WriteByte((byte)(v | 0x80)); v >>= 7; } o.WriteByte((byte)v); }
    public static byte[] Enc(List<PbF> list) {
        var o = new MemoryStream();
        foreach (var x in list) {
            WVar(o, ((ulong)x.F << 3) | (uint)x.W);
            if (x.W == 0) WVar(o, x.V);
            else if (x.W == 2) { var d = x.Sub != null ? Enc(x.Sub) : x.B; WVar(o, (ulong)d.Length); o.Write(d, 0, d.Length); }
            else o.Write(x.B, 0, x.B.Length);
        }
        return o.ToArray();
    }
    public static ulong U(List<PbF> l, int f) { foreach (var x in l) if (x.F == f && x.W == 0) return x.V; return 0; }
    public static byte[] Bt(List<PbF> l, int f) { foreach (var x in l) if (x.F == f && x.W == 2) return x.B; return null; }
}

// ---------- UMP : suite de parties (type, longueur, contenu), lue par morceaux de taille quelconque ----------
public class UmpReader {
    byte[] buf = new byte[0]; int len;
    // Entier UMP : sa longueur est donnee par les bits de tete du premier octet (1 a 5 octets).
    static int Need(byte c) { return c < 128 ? 1 : c < 192 ? 2 : c < 224 ? 3 : c < 240 ? 4 : 5; }
    static long Val(byte[] b, int p) {
        byte c = b[p];
        if (c < 128) return c;
        if (c < 192) return (c & 0x3f) + 64L * b[p + 1];
        if (c < 224) return (c & 0x1f) + 32L * (b[p + 1] + 256L * b[p + 2]);
        if (c < 240) return (c & 0x0f) + 16L * (b[p + 1] + 256L * (b[p + 2] + 256L * b[p + 3]));
        return b[p + 1] + 256L * (b[p + 2] + 256L * (b[p + 3] + 256L * b[p + 4]));
    }
    public List<KeyValuePair<int, byte[]>> Feed(byte[] data, int off, int n) {
        if (len + n > buf.Length) { var nb = new byte[Math.Max(buf.Length * 2, len + n)]; Buffer.BlockCopy(buf, 0, nb, 0, len); buf = nb; }
        Buffer.BlockCopy(data, off, buf, len, n); len += n;
        var outp = new List<KeyValuePair<int, byte[]>>();
        int p = 0;
        while (p < len) {
            int a = Need(buf[p]);
            if (p + a >= len) break;
            int b = Need(buf[p + a]);
            if (p + a + b > len) break;
            long type = Val(buf, p), size = Val(buf, p + a);
            if (size < 0 || size > 256L * 1024 * 1024) throw new FormatException("ump: taille " + size);
            if (p + a + b + size > len) break;
            var pl = new byte[size]; Buffer.BlockCopy(buf, p + a + b, pl, 0, (int)size);
            outp.Add(new KeyValuePair<int, byte[]>((int)type, pl));
            p += a + b + (int)size;
        }
        if (p > 0) { Buffer.BlockCopy(buf, p, buf, 0, len - p); len -= p; }
        return outp;
    }
    public int Pending { get { return len; } }

    // Reponse complete deja en memoire : decoupage SANS copie (chaque partie designe un morceau du tableau d'origine).
    // Mesure 21/10 : les copies successives (tampon, partie, MemoryStream, tableau) coutaient ~1/3 du temps du programme.
    public static List<UPart> Split(byte[] b, out int rest) {
        var outp = new List<UPart>();
        int p = 0, len = b.Length;
        while (p < len) {
            int a = Need(b[p]);
            if (p + a >= len) break;
            int c = Need(b[p + a]);
            if (p + a + c > len) break;
            long type = Val(b, p), size = Val(b, p + a);
            if (size < 0 || size > 256L * 1024 * 1024) throw new FormatException("ump: taille " + size);
            if (p + a + c + size > len) break;
            outp.Add(new UPart { T = (int)type, B = b, O = p + a + c, N = (int)size });
            p += a + c + (int)size;
        }
        rest = len - p;
        return outp;
    }
    public static List<UPart> Of(List<KeyValuePair<int, byte[]>> l) {
        return l.Select(x => new UPart { T = x.Key, B = x.Value, O = 0, N = x.Value.Length }).ToList();
    }
}
public struct UPart { public int T; public byte[] B; public int O; public int N; }

// ---------- formats et informations d'une video (connaissance du site : classe YtSite) ----------
public class MFormat {
    public int Itag; public string Mime = ""; public bool Audio; public int Height; public long Len; public long Br; public ulong Lmt; public string Label = "";
    public string Ext(bool asAudio) { return Mime.IndexOf("webm") >= 0 ? "webm" : asAudio ? "m4a" : "mp4"; }
}
public class MInfo {
    public string Vid = "", Title = "", Thumb = ""; public double Duration; public bool Live, Drm; public List<MFormat> Formats = new List<MFormat>();
    public DateTime At = DateTime.UtcNow;
}

// Tout ce qui est propre a YouTube : regles a poser dans l'extension, lecture des infos de la video, protocole SABR.
static class YtSite {
    public const string Name = "YouTube";
    public static readonly string[] PageHosts = { "youtube.com" };
    public static readonly string[] FetchHosts = { "googlevideo.com" };

    public static object Rules() {
        return new Dictionary<string, object> {
            { "id", "yt" }, { "name", Name }, { "hosts", PageHosts }, { "fetchHosts", FetchHosts }, { "compat", true },
            // captures passives : requete du lecteur (modele des requetes directes) et reponse « player » (navigation interne)
            { "capture", new object[] {
                new Dictionary<string, object> { { "id", "sabr" }, { "host", "googlevideo.com" }, { "path", "/videoplayback" }, { "query", new[] { "sabr=1" } },
                    { "method", "POST" }, { "part", "request" }, { "maxBytes", 256 * 1024 } },
                new Dictionary<string, object> { { "id", "player" }, { "host", "youtube.com" }, { "path", "/youtubei/v1/player" },
                    { "method", "POST" }, { "part", "response" }, { "maxBytes", 8 * 1024 * 1024 } } } },
            // script de la page qui porte les infos de la video au premier chargement
            { "scan", new object[] { new Dictionary<string, object> { { "id", "initial" }, { "marker", "ytInitialPlayerResponse" }, { "maxBytes", 8 * 1024 * 1024 } } } },
            // une page de video : chemin + parametre (pas de regex)
            { "media", new object[] {
                new Dictionary<string, object> { { "path", "/watch" }, { "query", new[] { "v=" } } },
                new Dictionary<string, object> { { "path", "/shorts/" } }, new Dictionary<string, object> { { "path", "/live/" } } } }
        };
    }

    static readonly Regex VidRe = new Regex(@"^[\w-]{6,20}$");
    public static string VidOf(string url) {
        try {
            var u = new Uri(url);
            string h = u.Host.ToLowerInvariant();
            if (!(h == "youtube.com" || h.EndsWith(".youtube.com"))) return null;
            string path = u.AbsolutePath;
            foreach (var pre in new[] { "/shorts/", "/live/", "/embed/" })
                if (path.StartsWith(pre)) { string id = path.Substring(pre.Length).Split('/')[0]; return VidRe.IsMatch(id) ? id : null; }
            if (path == "/watch") {
                foreach (var kv in u.Query.TrimStart('?').Split('&')) {
                    if (kv.StartsWith("v=")) { string id = Uri.UnescapeDataString(kv.Substring(2)); return VidRe.IsMatch(id) ? id : null; }
                }
            }
        } catch (System.Exception) { }
        return null;
    }

    // Reponse du lecteur (JSON) -> informations utiles. Rend null si ce n'est pas une reponse de lecteur exploitable.
    public static MInfo FromPlayer(Dictionary<string, object> r) {
        var vd = r != null && r.ContainsKey("videoDetails") ? r["videoDetails"] as Dictionary<string, object> : null;
        var sd = r != null && r.ContainsKey("streamingData") ? r["streamingData"] as Dictionary<string, object> : null;
        if (vd == null || sd == null) return null;
        var inf = new MInfo { Vid = Json.Str(vd, "videoId") ?? "", Title = Json.Str(vd, "title") ?? "" };
        if (!VidRe.IsMatch(inf.Vid)) return null;
        double d; double.TryParse(Json.Str(vd, "lengthSeconds") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        inf.Duration = d;
        inf.Live = Json.Bool(vd, "isLiveContent") && d <= 0;
        inf.Drm = sd.ContainsKey("drmFamilies");
        inf.Thumb = "https://i.ytimg.com/vi/" + inf.Vid + "/hqdefault.jpg";
        foreach (var f in Json.List(sd, "adaptiveFormats")) {
            var m = new MFormat();
            int.TryParse(Json.Str(f, "itag") ?? "0", out m.Itag);
            m.Mime = Json.Str(f, "mimeType") ?? "";
            string bare = m.Mime.Split(';')[0];
            m.Audio = bare.StartsWith("audio/");
            int.TryParse(Json.Str(f, "height") ?? "0", out m.Height);
            long.TryParse(Json.Str(f, "contentLength") ?? "0", out m.Len);
            long.TryParse(Json.Str(f, "bitrate") ?? "0", out m.Br);
            ulong.TryParse(Json.Str(f, "lastModified") ?? "0", out m.Lmt);
            m.Label = Json.Str(f, "qualityLabel") ?? "";
            if (m.Itag > 0) inf.Formats.Add(m);
        }
        return inf;
    }

    // Page HTML (ou script) contenant « ytInitialPlayerResponse = {...} » : on extrait l'objet JSON par appariement des accolades.
    public static MInfo FromHtml(string html) {
        if (string.IsNullOrEmpty(html)) return null;
        int k = html.IndexOf("ytInitialPlayerResponse", StringComparison.Ordinal);
        while (k >= 0) {
            int eq = html.IndexOf('=', k), br = eq >= 0 ? html.IndexOf('{', eq) : -1;
            if (br > 0 && br - k < 64) {
                string obj = Balanced(html, br);
                if (obj != null) {
                    try { var inf = FromPlayer(Json.Obj(obj)); if (inf != null) return inf; } catch (System.Exception) { }
                }
            }
            k = html.IndexOf("ytInitialPlayerResponse", k + 10, StringComparison.Ordinal);
        }
        return null;
    }
    static string Balanced(string s, int start) {
        int depth = 0; bool str = false; char q = '"';
        for (int i = start; i < s.Length; i++) {
            char c = s[i];
            if (str) { if (c == '\\') i++; else if (c == q) str = false; continue; }
            if (c == '"' || c == '\'') { str = true; q = c; }
            else if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return s.Substring(start, i - start + 1); }
        }
        return null;
    }

    // Formats retenus, dans le meme ordre de preference que l'extension 0.7.21 : meilleur audio (debit), video la plus
    // haute sous la qualite voulue puis la plus lourde. compat : H.264 + AAC seulement (« format compatible »).
    public static List<MFormat> Usable(MInfo inf, bool compat) {
        return inf.Formats.Where(f => !compat || Regex.IsMatch(f.Mime, "avc1|mp4a")).ToList();
    }
    public static int WantHeight(MInfo inf, string quality, bool compat) {
        int maxH = Usable(inf, compat).Where(f => !f.Audio && f.Len > 0 && f.Height > 0).Select(f => f.Height).DefaultIfEmpty(0).Max();
        int q; return quality != null && quality != "best" && int.TryParse(quality, out q) && q > 0 ? Math.Min(q, maxH) : maxH;
    }
    // un format sans taille annoncee n'est jamais retenu : la fin d'une piste se prouve par sa taille (revue Codex, I7)
    public static MFormat BestAudio(MInfo inf, bool compat) { return Usable(inf, compat).Where(f => f.Audio && f.Len > 0).OrderByDescending(f => f.Br).FirstOrDefault(); }
    public static MFormat BestVideo(MInfo inf, bool compat, int wantH) {
        return Usable(inf, compat).Where(f => !f.Audio && f.Len > 0 && f.Height > 0 && f.Height <= wantH).OrderByDescending(f => f.Height).ThenByDescending(f => f.Br).FirstOrDefault();
    }

    // Ce que l'extension affiche (popup, bouton) : qualites disponibles et tailles annoncees.
    public static Dictionary<string, object> Media(MInfo inf, bool compat) {
        var fs = Usable(inf, compat).Where(f => f.Len > 0).ToList();   // qualites annoncees = qualites telechargeables
        long aud = fs.Where(f => f.Audio).Select(f => f.Len).DefaultIfEmpty(0).Max();
        var hs = fs.Where(f => !f.Audio && f.Height > 0).Select(f => f.Height).Distinct().OrderByDescending(h => h).ToList();
        var variants = new List<object>();
        long size = 0;
        foreach (int h in hs) {
            long vl = fs.Where(f => !f.Audio && f.Height == h).Select(f => f.Len).DefaultIfEmpty(0).Max();
            if (h == hs[0]) size = vl;
            variants.Add(new Dictionary<string, object> { { "label", h + "p" }, { "url", h.ToString() }, { "height", h }, { "size", vl > 0 ? vl + aud : 0 } });
        }
        return new Dictionary<string, object> {
            { "key", "yt:" + inf.Vid }, { "vid", inf.Vid }, { "site", Name }, { "title", inf.Title }, { "duration", inf.Duration },
            { "live", inf.Live }, { "drm", inf.Drm }, { "thumb", inf.Thumb }, { "size", size + aud }, { "variants", variants }
        };
    }
}

// ---------- un telechargement SABR pilote par le programme ----------
// Le lecteur parle SABR : une requete POST (protobuf VideoPlaybackAbrRequest) qui dit « je suis a T ms » ; le serveur
// renvoie ~25 s de chaque piste a partir de T. On reprend la DERNIERE requete du lecteur (jeton, cookie, contexte,
// configuration : tout ce qu'on ne sait pas fabriquer) et on n'y change que la position, les formats voulus et la
// resolution. Numeros de champs lus dans googlevideo (LuanRT, licence MIT) ; rien de ce paquet n'est embarque.
// Port fidele du mode direct de l'extension 0.7.21 (yt_main.js), memes reglages eprouves.
public class SabrJob {
    public const int Win = 20000, Rounds = 4;
    public Item It; public MInfo Inf; public MFormat A, V; public bool AudioOnly, Resumed;
    public string Url; List<PbF> tpl; byte[] cookie; readonly Dictionary<ulong, byte[]> ctx = new Dictionary<ulong, byte[]>();
    public DateTime NotBefore = DateTime.MinValue; public readonly Dictionary<int, long> End = new Dictionary<int, long>();
    public long Span = Win; long rn = 1000; public int Par;
    readonly List<long> todo = new List<long>(); long cursor; int round; readonly Dictionary<long, int> tries = new Dictionary<long, int>();
    int fails; public int Served; public bool Stop, Err; public string ErrWhy;
    public readonly Dictionary<int, long> Out = new Dictionary<int, long>();   // requete en vol -> position
    // echeance de chaque requete en vol (revue Codex 21/10, B2) : si la reponse du programme portant l'action s'est perdue
    // en route (hote ou service worker interrompu), personne ne l'execute ; passee l'echeance, elle compte comme un echec
    // et la position est redemandee
    readonly Dictionary<int, DateTime> due = new Dictionary<int, DateTime>();
    public void ExpireForTest() { foreach (var k in due.Keys.ToList()) due[k] = DateTime.MinValue; }   // tests : reponse perdue
    int nextRid; public bool Finished; public string Failed; public int FailPct;
    // Protection du flux (partie 58, statut 3) : le serveur exige que le lecteur ait prouve qu'il est un vrai navigateur.
    // Mesure 21/10, profil Chrome vierge : premiere video refusee a 31 %, la meme reprise 30 s plus tard passe en entier.
    // On ne s'arrete donc pas : on fait relire la video au lecteur (lecture muette), on prend sa requete neuve, on continue.
    public bool Attest; public int Rearms; public DateTime AttestAt; int warmRid; public bool WarmDone;
    public void Swap(string url, byte[] template) {
        tpl = Pb.Parse(template); Url = url; cookie = null; ctx.Clear();
    }
    public void Resume() { Attest = false; todo.AddRange(Missing()); }
    public long NetBytes; public int Requests; public DateTime T0 = DateTime.UtcNow;
    public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
    public int ForeignVid;   // morceaux d'une AUTRE video (modele perime) : comptes, jamais ecrits
    public long DurMs { get { return Math.Max(1000, (long)(Inf.Duration * 1000)); } }

    public class Track { public bool HasInit; public long Bytes; public readonly HashSet<long> Segs = new HashSet<long>(); }

    public SabrJob(Item it, MInfo inf, MFormat a, MFormat v, bool audioOnly, string url, byte[] template, int par) {
        It = it; Inf = inf; A = a; V = v; AudioOnly = audioOnly; Url = url; tpl = Pb.Parse(template); Par = par;
        foreach (var f in new[] { a, v }) if (f != null) Tracks[f.Itag] = new Track();
        LoadDisk();
        if (Resumed) { todo.Add(0); cursor = DurMs; }
    }

    // reprise : ce qui est deja sur le disque compte comme recu ; seul le reste est demande
    void LoadDisk() {
        var have = new Dictionary<string, object>();
        Capture.Have(It, have);
        foreach (var kv in have) {
            int itag; if (!int.TryParse(kv.Key, out itag) || !Tracks.ContainsKey(itag)) continue;
            var h = kv.Value as Dictionary<string, object>; var t = Tracks[itag];
            long init; long.TryParse(Json.Str(h, "init") ?? "0", out init);
            if (init > 0) { t.HasInit = true; t.Bytes += init; Resumed = true; }
            var segs = h["segs"] as Dictionary<string, object>;
            foreach (var s in segs) { long q, n; long.TryParse(s.Key, out q); long.TryParse(Convert.ToString(s.Value, CultureInfo.InvariantCulture), out n); if (t.Segs.Add(q)) { t.Bytes += n; Resumed = true; } }
        }
    }

    public bool Wanted(int itag) { return (A != null && itag == A.Itag) || (V != null && itag == V.Itag); }

    static List<PbF> FmtId(MFormat f) { return new List<PbF> { PbF.Int(1, (ulong)f.Itag), PbF.Int(2, f.Lmt) }; }

    public byte[] Body(long ms) {
        int height = V != null ? V.Height : 144;
        var outp = new List<PbF>();
        foreach (var x in tpl) {
            if (x.F == 1 && x.W == 2) {
                var abr = new List<PbF>();
                foreach (var y in Pb.Parse(x.B)) {
                    if (AudioOnly && y.F == 40) continue;
                    if (y.F == 28) abr.Add(PbF.Int(28, (ulong)ms));                      // playerTimeMs
                    else if (y.F == 16 || y.F == 21) abr.Add(PbF.Int(y.F, (ulong)height)); // resolution choisie / collante
                    else abr.Add(y);
                }
                // audio seul : pistes activees = 1 (audio). Sans ca le serveur envoie AUSSI la video du lecteur (mesure 0.7.13 :
                // 73 % de video jetee par requete).
                if (AudioOnly) abr.Add(PbF.Int(40, 1));
                outp.Add(PbF.Msg(1, abr));
            } else if (x.F == 2 || x.F == 3 || x.F == 16 || x.F == 17) {
                // formats et plages du lecteur : remplaces par les notres
            } else if (x.F == 19 && x.W == 2) {
                outp.Add(PbF.Msg(16, FmtId(A)));
                if (V != null) outp.Add(PbF.Msg(17, FmtId(V)));
                // contexte du flux : cookie de lecture (3) et contextes SABR (5) que le serveur nous a donnes remplacent ceux du
                // lecteur. Mesure 20/10 (v2VNb-yuGxg) : sans renvoyer le contexte recu (partie 57), le serveur ne repond plus que
                // « attendez 8,8 s », indefiniment.
                if (cookie == null && ctx.Count == 0) { outp.Add(x); continue; }
                var sc = new List<PbF>();
                foreach (var y in Pb.Parse(x.B)) {
                    if (y.F == 3 && y.W == 2) { if (cookie == null) sc.Add(y); continue; }
                    if (y.F == 5 && y.W == 2) { ulong ty = Pb.U(Pb.Parse(y.B), 1); if (!ctx.ContainsKey(ty)) sc.Add(y); continue; }
                    sc.Add(y);
                }
                // ordre croissant des types, comme l'extension 0.7.21 (cles numeriques d'un objet JavaScript)
                foreach (ulong ty in ctx.Keys.OrderBy(k => k).ToList()) sc.Add(PbF.Msg(5, new List<PbF> { PbF.Int(1, ty), PbF.Bytes(2, ctx[ty]) }));
                if (cookie != null) sc.Add(PbF.Bytes(3, cookie));
                outp.Add(PbF.Msg(19, sc));
            } else outp.Add(x);
        }
        return Pb.Enc(outp);
    }

    public string NextUrl() {
        rn++;
        string u = Url;
        var m = Regex.Match(u, @"([?&])rn=[^&]*");
        if (m.Success) return u.Substring(0, m.Index) + m.Groups[1].Value + "rn=" + rn + u.Substring(m.Index + m.Length);
        return u + (u.Contains("?") ? "&" : "?") + "rn=" + rn;
    }

    // Limite de debit (« Laisser Internet disponible ») : ces requetes partent du navigateur, le programme ne peut que les
    // espacer. Horloge virtuelle : chaque reponse « coute » octets / limite secondes ; la suivante ne part pas avant.
    DateTime rateClock = DateTime.MinValue;
    void Throttle(int bytes) {
        int kb = Settings.Current.SpeedLimitKB;
        if (kb <= 0) return;
        var now = DateTime.UtcNow;
        if (rateClock < now) rateClock = now;
        rateClock = rateClock.AddSeconds(bytes / (kb * 1024.0));
        if (rateClock > NotBefore && (rateClock - now).TotalSeconds < 60) NotBefore = rateClock;
    }
    // Une reponse complete du serveur : parties de controle + morceaux des pistes (ecrits sur le disque).
    public void Response(byte[] body, long ms) {
        int rest;
        var parts = UmpReader.Split(body, out rest);
        if (rest > 0) throw new FormatException("ump: reponse tronquee (" + rest + " octets)");
        Parts(parts, ms);
    }
    class Hdr { public string Vid; public int Itag; public bool Init; public long Seq; public List<UPart> Data = new List<UPart>(); }
    public void Parts(List<KeyValuePair<int, byte[]>> parts, long ms) { Parts(UmpReader.Of(parts), ms); }
    public void Parts(List<UPart> parts, long ms) {
        var hdr = new Dictionary<int, Hdr>();
        var reach = new Dictionary<int, long>();
        foreach (var u in parts) {
            int t = u.T;
            // petites parties de controle : copiees ; MEDIA (21) : designe le tableau d'origine, sans copie
            byte[] pl = t == 21 ? null : Slice(u);
            if (t == 20) {          // MEDIA_HEADER
                var h = Pb.Parse(pl);
                var e = new Hdr { Itag = (int)Pb.U(h, 3), Init = Pb.U(h, 8) != 0, Seq = (long)Pb.U(h, 9) };
                var vb = Pb.Bt(h, 2); e.Vid = vb != null ? Encoding.UTF8.GetString(vb) : "";
                hdr[(int)Pb.U(h, 1)] = e;
                long hs = (long)Pb.U(h, 11), hd = (long)Pb.U(h, 12);
                if (e.Itag > 0 && Wanted(e.Itag) && hd > 0) { long r; reach.TryGetValue(e.Itag, out r); reach[e.Itag] = Math.Max(r, hs + hd); }
            } else if (t == 21) {   // MEDIA : 1er octet = numero d'en-tete
                Hdr e; if (u.N > 0 && hdr.TryGetValue(u.B[u.O], out e)) e.Data.Add(new UPart { T = 21, B = u.B, O = u.O + 1, N = u.N - 1 });
            } else if (t == 22) {   // MEDIA_END
                Hdr e; if (pl.Length == 0 || !hdr.TryGetValue(pl[0], out e)) continue;
                hdr.Remove(pl[0]);
                if (string.IsNullOrEmpty(e.Vid) || e.Itag == 0) continue;
                if (e.Vid != Inf.Vid) { ForeignVid++; continue; }
                if (Wanted(e.Itag)) Segment(e.Itag, e.Init, e.Seq, e.Data);
            } else if (t == 35) {   // NEXT_REQUEST_POLICY : cookie a renvoyer, attente demandee (echeance PARTAGEE)
                foreach (var x in Pb.Parse(pl)) {
                    if (x.F == 7 && x.W == 2) cookie = x.B;
                    if (x.F == 4 && x.W == 0 && x.V > 0) { var nb = DateTime.UtcNow.AddMilliseconds(Math.Min((double)x.V, 60000)); if (nb > NotBefore) NotBefore = nb; }
                }
            } else if (t == 42) {   // FORMAT_INITIALIZATION_METADATA : dernier numero de morceau de la piste
                int itag = 0; long endSeg = 0;
                foreach (var x in Pb.Parse(pl)) {
                    if (x.F == 2 && x.W == 2) itag = (int)Pb.U(Pb.Parse(x.B), 1);
                    if (x.F == 4 && x.W == 0) endSeg = (long)x.V;
                }
                if (itag > 0 && endSeg > 0) End[itag] = endSeg;
            } else if (t == 57) {   // SABR_CONTEXT_UPDATE : 1 type, 3 valeur => a renvoyer dans chaque requete
                var l = Pb.Parse(pl); ulong cty = Pb.U(l, 1); var cval = Pb.Bt(l, 3);
                if (cty > 0 && cval != null) ctx[cty] = cval;
            } else if (t == 43) {   // SABR_REDIRECT : seulement vers un hote autorise
                var nu = Pb.Bt(Pb.Parse(pl), 1);
                if (nu != null) { string s = Encoding.UTF8.GetString(nu); if (Pilot.FetchAllowed(s, YtSite.FetchHosts)) Url = s; }
            } else if (t == 44) {   // SABR_ERROR : erreur du serveur. Detail journalise (champs lus), puis meme remede que la protection du flux :
                // relecture du lecteur (modele neuf) et nouvel essai, 3 fois au plus (21/10, ShowF : « erreur » au premier essai d'une video)
                string det = "";
                try { foreach (var x in Pb.Parse(pl)) det += (det.Length > 0 ? " " : "") + x.F + "=" + (x.W == 0 ? x.V.ToString() : Clip60(x.B)); } catch (System.Exception) { }
                App.Log("pilot : erreur SABR (" + det + ") a " + Pct + " %, essai " + (Rearms + 1));
                if (Rearms < 3) { if (!Attest) { Attest = true; AttestAt = DateTime.UtcNow; var nb = AttestAt.AddSeconds(Rearms == 0 ? 3 : Rearms == 1 ? 10 : 25); if (nb > NotBefore) NotBefore = nb; } }
                else { Err = true; ErrWhy = "sabr_error " + det; }
            } else if (t == 58) {   // STREAM_PROTECTION_STATUS : 3 = refuse
                ulong st = Pb.U(Pb.Parse(pl), 1);
                if (st >= 3) {
                    if (Rearms < 3) { if (!Attest) { Attest = true; AttestAt = DateTime.UtcNow; var nb = AttestAt.AddSeconds(Rearms == 0 ? 5 : Rearms == 1 ? 15 : 30); if (nb > NotBefore) NotBefore = nb; } }
                    else { Err = true; ErrWhy = "protection_" + st; }
                }
            }
        }
        // pas suivant (audio seul) = ce que le serveur rend vraiment par requete, moins 1 s de marge
        long cov = long.MaxValue;
        foreach (var f in new[] { A, V }) { long r; if (f != null && reach.TryGetValue(f.Itag, out r)) cov = Math.Min(cov, r - ms); }
        if (AudioOnly && cov != long.MaxValue && cov > 5000) Span = Math.Min(120000, cov - 1000);
    }

    static string Clip60(byte[] b) { if (b == null) return ""; string t = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(60, b.Length)); var sb = new StringBuilder(); foreach (char c in t) sb.Append(c < 32 ? ' ' : c); return sb.ToString(); }
    static byte[] Slice(UPart u) { var r = new byte[u.N]; Buffer.BlockCopy(u.B, u.O, r, 0, u.N); return r; }

    void Segment(int itag, bool init, long seq, List<UPart> data) {
        var t = Tracks[itag];
        if (init ? t.HasInit : t.Segs.Contains(seq)) return;   // deja recu
        if (It.Removed) return;                                 // supprime pendant le pas : son dossier vient d'etre efface
        string dir = Path.Combine(It.WorkDir, "i" + itag);
        if (!dirs.Contains(dir)) { Directory.CreateDirectory(dir); dirs.Add(dir); }
        string f = Path.Combine(dir, init ? "init.bin" : seq.ToString("D8") + ".bin");
        long n = 0;
        if (!File.Exists(f)) {
            using (var fs = new FileStream(f + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                foreach (var d in data) { fs.Write(d.B, d.O, d.N); n += d.N; }
            File.Move(f + ".tmp", f);
        } else n = data.Sum(d => (long)d.N);
        if (init) t.HasInit = true; else t.Segs.Add(seq);
        t.Bytes += n;
        lock (It) { It.Received += n; It.LastSeen = DateTime.Now; }
    }
    readonly HashSet<string> dirs = new HashSet<string>();

    // Piste complete : init + octets recus >= taille annoncee (un seuil a 99,5 % laissait tomber la fin : 22 s de video).
    bool Done(MFormat f) { if (f == null) return true; var t = Tracks[f.Itag]; return t.HasInit && f.Len > 0 && t.Bytes >= f.Len; }
    double Ratio(MFormat f) { if (f == null) return 1; var t = Tracks[f.Itag]; return f.Len > 0 ? Math.Min(1.0, (double)t.Bytes / f.Len) : 0; }
    public bool Complete { get { return Done(A) && Done(V); } }
    public int Pct { get { return Complete ? 100 : (int)Math.Floor(Math.Min(Ratio(A), Ratio(V)) * 100); } }

    long? Next() {
        if (todo.Count > 0) { long x = todo[0]; todo.RemoveAt(0); return x; }
        if (cursor < DurMs) { long x = cursor; cursor += Span; return x; }
        return null;
    }
    List<long> Missing() {
        var outp = new List<long>();
        foreach (var f in new[] { A, V }) {
            if (f == null) continue;
            var t = Tracks[f.Itag]; long n;
            if (!t.HasInit || !End.TryGetValue(f.Itag, out n)) { outp.Add(0); continue; }
            double per = (double)DurMs / n;
            for (long q = 1; q <= n; q++) if (!t.Segs.Contains(q)) outp.Add(Math.Max(0, (long)Math.Floor((q - 1) * per) - 1000));
        }
        // une requete couvre ~20 s : une seule position par fenetre
        var seen = new HashSet<long>(); var uniq = new List<long>();
        foreach (long x in outp.OrderBy(v => v)) if (seen.Add(x / Win)) uniq.Add(x);
        return uniq;
    }

    // Resultat d'une requete (succes ou echec).
    public void Result(int rid, bool ok, byte[] body) {
        long ms;
        if (rid == warmRid && warmRid > 0) { warmRid = 0; WarmDone = true; return; }   // lecture muette finie (reussie ou non)
        if (!Out.TryGetValue(rid, out ms)) return;   // inconnue ou deja traitee
        Out.Remove(rid); due.Remove(rid);
        if (Finished || Failed != null) return;
        if (ok && body != null) {
            try { Requests++; NetBytes += body.Length; Throttle(body.Length); Response(body, ms); Served++; fails = 0; }
            catch (System.Exception e) { App.Log("pilot : reponse illisible (" + e.Message + ")"); ok = false; }
        }
        if (!ok) {
            // echecs comptes PAR position ; une position abandonnee repasse par Missing()
            int n; tries.TryGetValue(ms, out n); tries[ms] = n + 1;
            if (++fails > 12) Stop = true;
            else if (n + 1 < 3) todo.Add(ms);
        }
        if (Err) Stop = true;
    }

    // Requetes a lancer maintenant (au plus Par en vol). Gere la fin des passages, la fin du travail et les echecs.
    public List<Dictionary<string, object>> Plan() {
        var acts = new List<Dictionary<string, object>>();
        foreach (var kv in due.Where(x => x.Value < DateTime.UtcNow).ToList()) Result(kv.Key, false, null);
        while (true) {
            if (Finished || Failed != null) return acts;
            if (Complete) { Finished = true; return acts; }   // requetes encore en vol : leurs reponses seront ignorees
            if (Attest) {   // plus aucune requete tant que le lecteur n'a pas relu la video
                if (Out.Count == 0 && warmRid == 0 && !WarmDone && DateTime.UtcNow >= NotBefore) {   // attente croissante : le lecteur finit de se faire reconnaitre
                    Rearms++; warmRid = ++nextRid;
                    // lecture a la premiere position manquante : le lecteur n'a rien en memoire la, il doit redemander
                    var miss = Missing();
                    long seek = miss.Count > 0 ? miss[miss.Count / 2] : DurMs / 2;
                    acts.Add(new Dictionary<string, object> { { "rid", warmRid }, { "op", "warm" }, { "until", "sabr" }, { "timeout", 8000 }, { "seek", seek / 1000 } });
                }
                return acts;
            }
            while (!Stop && Out.Count < Par) {
                long? ms = Next();
                if (ms == null) break;
                int rid = ++nextRid; Out[rid] = ms.Value;
                int delay = NotBefore > DateTime.UtcNow ? (int)(NotBefore - DateTime.UtcNow).TotalMilliseconds : 0;
                due[rid] = DateTime.UtcNow.AddMilliseconds(delay + 45000 + 20000);
                acts.Add(new Dictionary<string, object> {
                    { "rid", rid }, { "op", "fetch" }, { "method", "POST" }, { "url", NextUrl() }, { "body", Convert.ToBase64String(Body(ms.Value)) },
                    { "delay", delay }, { "timeout", 45000 } });
            }
            if (Out.Count > 0) return acts;
            // passage termine : tout est la ? sinon seulement ce qui manque, au plus Rounds (+3 en reprise) passages
            if (Stop) { Failed = Served == 0 || Pct < 5 ? "refused" : "incomplete"; FailPct = Pct; return acts; }
            if (++round > Rounds + (Resumed ? 3 : 0)) { Failed = "incomplete"; FailPct = Pct; return acts; }
            todo.AddRange(Missing());
            if (todo.Count == 0) todo.Add(0);
        }
    }
}

// ---------- gestionnaire generique ----------
static class Pilot {
    static readonly object L = new object();
    public static long ParseMs, StepMs, InBytes;   // banc : temps passe a lire le JSON des pas et a les traiter
    static readonly Dictionary<string, MInfo> infos = new Dictionary<string, MInfo>();                 // video -> infos
    static readonly Dictionary<string, KeyValuePair<string, byte[]>> tpls = new Dictionary<string, KeyValuePair<string, byte[]>>();   // video -> modele
    static readonly Dictionary<string, DateTime> tplAt = new Dictionary<string, DateTime>();   // video -> date du modele
    static readonly Dictionary<string, DateTime> ended = new Dictionary<string, DateTime>();   // travaux finis : leurs pas tardifs sont « perimes »
    const int TplFreshMin = 5;   // modele plus vieux : on relance une lecture muette pour en avoir un neuf (revue Codex, I5)
    static readonly Dictionary<string, SabrJob> jobs = new Dictionary<string, SabrJob>();
    static readonly Dictionary<string, PendingStart> starts = new Dictionary<string, PendingStart>();   // demarrages en attente (infos, lecteur)

    class PendingStart { public Dictionary<string, object> M; public string Vid; public int Step; public int Rid; public DateTime At = DateTime.UtcNow; }

    public static bool HostIn(string host, string[] list) {
        host = (host ?? "").ToLowerInvariant();
        foreach (var h in list) if (host == h || host.EndsWith("." + h)) return true;
        return false;
    }
    public static bool FetchAllowed(string url, string[] hosts) {
        Uri u; return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https" && HostIn(u.Host, hosts);
    }
    static bool PageOk(string page) { Uri u; return Uri.TryCreate(page ?? "", UriKind.Absolute, out u) && u.Scheme == "https" && HostIn(u.Host, YtSite.PageHosts); }

    public static object Rules() {
        return new Dictionary<string, object> { { "ok", true }, { "v", 1 }, { "sites", new object[] { YtSite.Rules() } } };
    }

    public static void Keep(MInfo inf) {
        lock (L) {
            infos[inf.Vid] = inf;
            if (infos.Count > 40) foreach (var k in infos.OrderBy(x => x.Value.At).Take(infos.Count - 40).Select(x => x.Key).ToList()) infos.Remove(k);
        }
    }
    static MInfo InfoOf(string vid) { lock (L) { MInfo i; return vid != null && infos.TryGetValue(vid, out i) ? i : null; } }

    static Dictionary<string, object> MediaFor(string page, bool compat) {
        var inf = InfoOf(YtSite.VidOf(page));
        if (inf == null) return null;
        var m = YtSite.Media(inf, compat);
        var resume = new Dictionary<string, object>();
        foreach (bool audio in new[] { false, true }) {
            var c = Capture.Resumable(inf.Vid, audio);
            if (c == null) continue;
            long got = Capture.Have(c, null);
            if (got > 0) resume[audio ? "audio" : "video"] = c.Total > 0 ? (int)Math.Min(99, got * 100 / c.Total) : 1;
        }
        if (resume.Count > 0) m["resume"] = resume;
        return m;
    }

    // Donnee capturee par l'extension (corps en base64). Tout est controle ici : la page qui l'envoie est une donnee.
    public static object Data(Dictionary<string, object> m) {
        string page = Json.Str(m, "page"), rule = Json.Str(m, "rule"), url = Json.Str(m, "url") ?? "";
        if (!PageOk(page)) return Err("page");
        byte[] body;
        try { body = Convert.FromBase64String(Json.Str(m, "body") ?? ""); } catch (System.Exception) { return Err("body"); }
        if (body.Length > 16 * 1024 * 1024) return Err("size");
        if (rule == "sabr") {
            string vid = YtSite.VidOf(page);
            if (vid == null || !FetchAllowed(url, YtSite.FetchHosts) || body.Length < 50) return Err("sabr");
            try { Pb.Parse(body); } catch (System.Exception) { return Err("sabr pb"); }
            // capture partie d'une AUTRE page (navigation interne pendant la requete) : ignoree (revue Codex, I5)
            string hint = Json.Str(m, "hint");
            if (!string.IsNullOrEmpty(hint) && YtSite.VidOf(hint) != vid) return new Dictionary<string, object> { { "ok", true }, { "ignored", "page" } };
            lock (L) {
                tpls[vid] = new KeyValuePair<string, byte[]>(url, body); tplAt[vid] = DateTime.UtcNow;
                if (tpls.Count > 40) foreach (var k in tplAt.OrderBy(x => x.Value).Take(tpls.Count - 40).Select(x => x.Key).ToList()) { tpls.Remove(k); tplAt.Remove(k); }
            }
        } else if (rule == "player" || rule == "initial" || rule == "page") {
            MInfo inf = null;
            string txt = Encoding.UTF8.GetString(body);
            if (rule == "player") { try { inf = YtSite.FromPlayer(Json.Obj(txt)); } catch (System.Exception) { } }
            else inf = YtSite.FromHtml(txt);
            // une page ne REMPLACE que les infos de sa propre video ; celles d'une autre video (reponse du lecteur pendant une
            // navigation interne) ne font que remplir un vide (revue Codex 21/10, I4 : onglet A empoisonnant la video de B)
            if (inf != null && (inf.Vid == YtSite.VidOf(page) || InfoOf(inf.Vid) == null)) Keep(inf);
        } else return Err("rule");
        var media = MediaFor(page, Json.Bool(m, "compat"));
        var r = new Dictionary<string, object> { { "ok", true } };
        if (media != null) r["media"] = media;
        return r;
    }

    // Que sait-on de cette page ? Si c'est une page de video sans infos : demander la page elle-meme (meme origine).
    public static object Page(Dictionary<string, object> m) {
        string page = Json.Str(m, "page");
        var r = new Dictionary<string, object> { { "ok", true } };
        if (!PageOk(page) || YtSite.VidOf(page) == null) return r;
        var media = MediaFor(page, Json.Bool(m, "compat"));
        if (media != null) r["media"] = media;
        else if (Json.Bool(m, "allowFetch")) r["fetch"] = new Dictionary<string, object> { { "rule", "page" }, { "url", page }, { "method", "GET" } };
        return r;
    }

    static int Par(Dictionary<string, object> m, bool audioOnly) {
        int p; return int.TryParse(Json.Str(m, "par") ?? "", out p) && p >= 1 && p <= 16 ? p : 8;   // mesure 21/10 (1 h 57, 720p) : 4 => 29,3 s, 6 => 28,5 s, 8 => 25,5 s (extension 0.7.21 : 32,5 s)
    }

    // Debut d'un telechargement depuis une page. Etapes : infos (page demandee si besoin), modele du lecteur (lecture
    // muette d'une seconde si besoin), puis requetes directes. yt-dlp d'abord si le reseau le permet (sans filtre).
    public static object Start(Dictionary<string, object> m) {
        string page = Json.Str(m, "page");
        string vid = YtSite.VidOf(page);
        if (!PageOk(page) || vid == null) return Err("notMedia");
        // 0.7.32 : plus de sonde yt-dlp avant YouTube. Elle coutait ~7 s a CHAQUE demarrage (mesure 21/10 : « commande lente 6669 ms ») et le moteur
        // du programme est plus rapide que yt-dlp ; la sonde ne reste qu'a la demande (« probe »: true, bancs).
        if (Json.Bool(m, "probe") && Ytdlp.Probe(page)) {
            var add = new Dictionary<string, object>(m); add["url"] = page; add["kind"] = "page";
            try { var it = Mgr.Add(add); return new Dictionary<string, object> { { "ok", true }, { "via", "app" }, { "id", it.Id } }; }
            catch (DuplicateException d) { return new Dictionary<string, object> { { "ok", false }, { "error", "duplicate" }, { "id", d.Existing.Id } }; }
        }
        Sweep();
        var ps = new PendingStart { M = m, Vid = vid };
        string sid = "s" + Guid.NewGuid().ToString("N").Substring(0, 10);
        lock (L) {
            foreach (var k in starts.Where(x => (DateTime.UtcNow - x.Value.At).TotalMinutes > 5).Select(x => x.Key).ToList()) starts.Remove(k);
            starts[sid] = ps;
        }
        return Advance(sid, ps, null);
    }

    // Fait avancer un demarrage en attente ; rend { id, actions } (id provisoire « s... » tant que le telechargement
    // n'existe pas, puis l'id de l'element ShowLef).
    static object Advance(string sid, PendingStart ps, Dictionary<string, object> res, List<string> bins = null) {
        if (res != null && ps.Step == 1 && Json.Bool(res, "ok")) {   // page recue
            try { var inf = YtSite.FromHtml(Encoding.UTF8.GetString(BodyOf(res, bins))); if (inf != null) Keep(inf); } catch (System.Exception) { }
        }
        var info = InfoOf(ps.Vid);
        if (info == null) {
            if (ps.Step >= 1) { lock (L) starts.Remove(sid); return Err("noPlayer"); }
            ps.Step = 1; ps.Rid = 1;
            return Acts(sid, new Dictionary<string, object> { { "rid", 1 }, { "op", "fetch" }, { "method", "GET" }, { "url", Json.Str(ps.M, "page") }, { "timeout", 20000 }, { "same", true } });
        }
        if (info.Drm) { lock (L) starts.Remove(sid); return Err("drm"); }
        if (info.Live) { lock (L) starts.Remove(sid); return Err("live"); }
        KeyValuePair<string, byte[]> tp;
        bool has, fresh; DateTime at;
        lock (L) { has = tpls.TryGetValue(ps.Vid, out tp); fresh = tplAt.TryGetValue(ps.Vid, out at) && (DateTime.UtcNow - at).TotalMinutes < TplFreshMin; }
        if (!has || (!fresh && ps.Step < 2)) {
            if (ps.Step >= 2 && !has) { lock (L) starts.Remove(sid); return Err("noPlayer"); }
            // aucune requete du lecteur pour cette video : lecture muette jusqu'a la premiere requete « sabr », puis tout est remis
            ps.Step = 2; ps.Rid = 2;
            return Acts(sid, new Dictionary<string, object> { { "rid", 2 }, { "op", "warm" }, { "until", "sabr" }, { "timeout", 8000 } });
        }
        lock (L) starts.Remove(sid);
        return Begin(ps, info, tp);
    }

    static object Begin(PendingStart ps, MInfo info, KeyValuePair<string, byte[]> tp) {
        var m = ps.M;
        bool audioOnly = Json.Bool(m, "audioOnly"), compat = Json.Bool(m, "compat");
        int wantH = YtSite.WantHeight(info, Json.Str(m, "quality"), compat);
        var a = YtSite.BestAudio(info, compat);
        var v = audioOnly ? null : YtSite.BestVideo(info, compat, wantH);
        if (a == null || (!audioOnly && v == null)) return Err("noFormat");
        var bm = new Dictionary<string, object> {
            { "title", info.Title }, { "pageUrl", Json.Str(m, "page") }, { "quality", Json.Str(m, "quality") }, { "audioOnly", audioOnly },
            { "audioFormat", Json.Str(m, "audioFormat") }, { "total", a.Len + (v != null ? v.Len : 0) }, { "thumb", info.Thumb }, { "key", info.Vid } };
        long tot; long.TryParse(Convert.ToString(bm["total"], CultureInfo.InvariantCulture), out tot);
        string full = Mgr.DiskShort(Mgr.DefaultFolder(new Item { Url = Json.Str(m, "page"), Kind = "capture", AudioOnly = audioOnly }), tot, 2.2);
        if (full != null && Capture.Resumable(info.Vid, audioOnly) == null) { Capture.Refuse(new Dictionary<string, object> { { "pageUrl", Json.Str(m, "page") }, { "title", info.Title }, { "audioOnly", audioOnly }, { "thumb", info.Thumb }, { "total", tot } }, full); return Err("app"); }
        var it = Capture.Begin(bm);
        SabrJob j;
        try { j = new SabrJob(it, info, a, v, audioOnly, tp.Key, tp.Value, Par(m, audioOnly)); }
        catch (System.Exception e) { Capture.Abort(it, e.Message); return Err("template"); }
        lock (L) jobs[it.Id] = j;
        ParseMs = StepMs = InBytes = 0;
        App.Log("pilot : debut " + info.Vid + " audio=" + a.Itag + (v != null ? " video=" + v.Itag : "") + (j.Resumed ? " (reprise)" : ""));
        lock (j) return StepReply(j);
    }

    static object Acts(string id, params Dictionary<string, object>[] a) {
        return new Dictionary<string, object> { { "ok", true }, { "id", id }, { "actions", a } };
    }

    public static object Step(Dictionary<string, object> m) { return Step(m, null); }
    public static object Step(Dictionary<string, object> m, List<string> bins) {
        string id = Json.Str(m, "id") ?? "";
        var results = Json.List(m, "results");
        PendingStart ps; bool pending; lock (L) pending = starts.TryGetValue(id, out ps);
        if (pending) {
            var res = results.FirstOrDefault(r => Json.Str(r, "rid") == ps.Rid.ToString());
            if (res == null) return Acts(id);   // battement de coeur : rien de neuf
            return Advance(id, ps, res, bins);
        }
        SabrJob j; bool has, gone; lock (L) { has = jobs.TryGetValue(id, out j); gone = ended.ContainsKey(id); }
        // pas parti avant la fin du travail (plusieurs requetes en vol) : sans effet, et surtout pas une erreur affichee
        // apres une reussite (revue Codex 21/10, I1)
        if (!has && gone) return new Dictionary<string, object> { { "ok", true }, { "id", id }, { "stale", true }, { "actions", new object[0] } };
        if (!has) return Err("unknown id");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { lock (j) return StepLocked(j, results, bins); } finally { StepMs += sw.ElapsedMilliseconds; }
    }
    // corps d'un resultat : base64, ou « #n » = n-ieme corps sorti avant la lecture JSON (Ipc.CutBodies)
    static byte[] BodyOf(Dictionary<string, object> r, List<string> bins) {
        string b = Json.Str(r, "body") ?? "";
        int n;
        if (bins != null && b.StartsWith("#") && int.TryParse(b.Substring(1), out n) && n >= 0 && n < bins.Count) b = bins[n];
        return Convert.FromBase64String(b);
    }
    static object StepLocked(SabrJob j, List<Dictionary<string, object>> results, List<string> bins) {
        {
            // annule dans le programme (corbeille, arret) : l'extension arrete tout
            if (!j.It.Removed && j.It.Status == "paused") {   // pause dans le programme = arret ; reprise par le bouton de la page
                lock (j.It) { j.It.Status = "error"; j.It.Error = Tr.T("e_capStopped"); }
                Mgr.Save();
            }
            if (j.It.Removed || j.It.Status != "downloading") { Forget(j); App.Log("pilot : arrete par le programme (" + j.It.Status + ") " + j.Inf.Vid); return Err("cancelled"); }
            j.It.LastSeen = DateTime.Now;
            foreach (var r in results) {
                int rid; if (!int.TryParse(Json.Str(r, "rid") ?? "", out rid)) continue;
                byte[] body = null;
                if (Json.Bool(r, "ok")) { try { body = BodyOf(r, bins); } catch (System.Exception) { } }
                j.Result(rid, body != null, body);
            }
            if (j.WarmDone) {   // relecture du lecteur finie : sa requete neuve (si elle est arrivee) remplace le modele
                j.WarmDone = false;
                KeyValuePair<string, byte[]> tp; DateTime at; bool neu;
                lock (L) neu = tpls.TryGetValue(j.Inf.Vid, out tp) && tplAt.TryGetValue(j.Inf.Vid, out at) && at > j.AttestAt;
                if (neu) j.Swap(tp.Key, tp.Value);
                App.Log("pilot : protection du flux " + j.Inf.Vid + " a " + j.Pct + " %, relecture " + j.Rearms + (neu ? ", modele neuf" : ", sans modele neuf"));
                j.Resume();
            }
            return StepReply(j);
        }
    }

    static object StepReply(SabrJob j) {
        var acts = j.Plan();
        var r = new Dictionary<string, object> { { "ok", true }, { "id", j.It.Id }, { "actions", acts }, { "pct", j.Pct } };
        if (j.Finished) {
            j.It.CapAudio = j.A.Itag + ":" + j.A.Ext(true);
            j.It.CapVideo = j.V != null ? j.V.Itag + ":" + j.V.Ext(false) : null;
            double s = (DateTime.UtcNow - j.T0).TotalSeconds;
            App.Log("pilot : fin " + j.Inf.Vid + " " + j.Requests + " requetes, " + (j.NetBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mo en "
                + s.ToString("0.0", CultureInfo.InvariantCulture) + " s (JSON " + ParseMs + " ms, traitement " + StepMs + " ms, " + (InBytes / 1048576) + " Mo recus)" + (j.ForeignVid > 0 ? ", " + j.ForeignVid + " morceaux d'une autre video ignores" : ""));
            Forget(j);
            Capture.End(j.It);
            r["done"] = true;
        } else if (j.Failed != null) {
            App.Log("pilot : echec " + j.Inf.Vid + " " + j.Failed + " (" + j.FailPct + " %)" + (j.ErrWhy != null ? " " + j.ErrWhy : ""));
            if (j.Failed == "refused" && j.Served == 0) lock (L) { tpls.Remove(j.Inf.Vid); tplAt.Remove(j.Inf.Vid); }
            Forget(j);
            Capture.Abort(j.It, Tr.T("e_capStopped"));
            return new Dictionary<string, object> { { "ok", false }, { "error", j.Failed }, { "pct", j.FailPct } };
        }
        return r;
    }

    static void Forget(SabrJob j) {
        lock (L) {
            jobs.Remove(j.It.Id); ended[j.It.Id] = DateTime.UtcNow;
            foreach (var k in ended.Where(x => (DateTime.UtcNow - x.Value).TotalMinutes > 30).Select(x => x.Key).ToList()) ended.Remove(k);
        }
    }
    // travaux dont l'element n'est plus « en cours » (abandonne par Mgr.Tick faute de nouvelles, supprime) : oublies
    // (revue Codex, I2 : aucun balayage avant)
    static void Sweep() {
        List<SabrJob> dead;
        lock (L) dead = jobs.Values.Where(x => x.It.Removed || x.It.Status != "downloading").ToList();
        foreach (var x in dead) Forget(x);
    }

    public static object Abort(Dictionary<string, object> m) {
        string id = Json.Str(m, "id") ?? "";
        lock (L) starts.Remove(id);
        SabrJob j; bool has; lock (L) has = jobs.TryGetValue(id, out j);
        if (has) { Forget(j); Capture.Abort(j.It, Tr.T("e_capStopped")); }
        return new Dictionary<string, object> { { "ok", true } };
    }

    static object Err(string e) { return new Dictionary<string, object> { { "ok", false }, { "error", e } }; }
}

// ---------- reprise sans geste et playlists (0.7.35) ----------
// Le programme ne peut pas parler le premier a l'extension (c'est Chrome qui lance l'hote). L'extension pose donc une attente
// longue (`pilot_wait`, 20 s) ; quand le programme a quelque chose a lui demander (« Reprendre » sur une ligne, video suivante d'une
// playlist), il repond tout de suite : l'extension ouvre la page EN ARRIERE-PLAN et relance le telechargement toute seule.
static class Wake {
    static readonly object L = new object();
    static readonly List<Dictionary<string, object>> q = new List<Dictionary<string, object>>();
    static DateTime lastPoll = DateTime.MinValue;
    // l'extension attend-elle ? (derniere attente < 45 s) : sinon la reprise retombe sur l'ancien geste (ouvrir le lien)
    public static bool Alive { get { return (DateTime.UtcNow - lastPoll).TotalSeconds < 45; } }
    public static void Probe(string token) {
        lock (L) {
            string tag = "extension:" + token;
            q.RemoveAll(x => (string)x["tag"] == tag);
            q.Add(new Dictionary<string, object> { { "type", "extension_probe" }, { "token", token }, { "tag", tag } });
            Monitor.PulseAll(L);
        }
    }
    public static void Add(string url, string quality, bool audioOnly, string audioFormat, string tag) {
        lock (L) {
            q.RemoveAll(x => (string)x["tag"] == (tag ?? ""));
            q.Add(new Dictionary<string, object> { { "url", url }, { "quality", quality ?? "" }, { "audioOnly", audioOnly }, { "audioFormat", audioFormat ?? "" }, { "tag", tag ?? "" } });
            Monitor.PulseAll(L);
        }
    }
    // retire une demande que personne n'a prise ; true = elle y etait encore
    public static bool Drop(string tag) { lock (L) return q.RemoveAll(x => (string)x["tag"] == (tag ?? "")) > 0; }
    public static object Wait(int sec) {
        sec = Math.Max(1, Math.Min(25, sec));
        lock (L) {
            var end = DateTime.UtcNow.AddSeconds(sec);
            lastPoll = DateTime.UtcNow;
            while (q.Count == 0 && DateTime.UtcNow < end) { Monitor.Wait(L, 1000); lastPoll = DateTime.UtcNow; }
            var take = q.ToList(); q.Clear();
            return new Dictionary<string, object> { { "ok", true }, { "wakes", take } };
        }
    }
    // l'extension n'a pas pu ouvrir la page : une ligne de reprise retombe sur le lien, une playlist saute la video
    public static object Failed(string tag) {
        tag = tag ?? "";
        if (tag.StartsWith("pl:")) { Playlists.Skip(tag.Substring(3)); return new Dictionary<string, object> { { "ok", true } }; }
        Item it; lock (Mgr.Items) it = Mgr.Items.FirstOrDefault(i => i.Id == tag);
        if (it != null && (it.Url ?? "").StartsWith("https://")) {
            it.Error = Tr.T("e_capStopped"); Mgr.Save();
            try { System.Diagnostics.Process.Start(it.Url); } catch (System.Exception) { }
        }
        return new Dictionary<string, object> { { "ok", true } };
    }
    // « Reprendre » sur une capture coupee
    public static void Resume(Item it) {
        string tag = it.Id;
        Add(it.Url, it.Quality, it.AudioOnly, it.AudioFormat, tag);
        it.Error = Tr.T("e_capWaking"); Mgr.Save();
        new Thread(() => {
            Thread.Sleep(25000);
            if (Drop(tag)) { App.Log("reprise : l'extension n'a pas repondu, ouverture du lien"); try { System.Diagnostics.Process.Start(it.Url); } catch (System.Exception) { } }
        }) { IsBackground = true }.Start();
    }
}

public class PlRow { public string Id, Title; public int Idx; public bool Known; }

// Playlist : l'extension lit la liste dans la page ; le programme montre la fenetre a cocher, puis demande les videos UNE A LA FOIS
// (la suivante part quand la precedente est finie), dans un dossier au nom de la playlist, noms « 01 - Titre ».
static class Playlists {
    class Run {
        public string Name, Folder, Quality, AudioFormat; public bool AudioOnly; public int Width;
        public List<PlRow> Todo = new List<PlRow>(); public int Pos; public string Cur; public DateTime CurAt;
        public int Ok, Skipped;
    }
    static readonly object L = new object();
    static Run run;
    static Thread th;
    public static Func<string, List<PlRow>, List<string>> OnPick;   // fenetre a cocher (interface) : identifiants choisis, null si annule
    static readonly Dictionary<string, string[]> plan = new Dictionary<string, string[]>();   // video -> { dossier, prefixe }

    public static string[] PlanFor(string vid) { lock (L) { string[] p; return vid != null && plan.TryGetValue(vid, out p) ? p : null; } }
    public static bool Busy { get { lock (L) return run != null; } }

    public static object Add(Dictionary<string, object> m) {
        string name = Fmt.SafeName(Json.Str(m, "name") ?? "playlist");
        bool audio = Json.Bool(m, "audioOnly");
        var rows = new List<PlRow>(); var seen = new HashSet<string>();
        foreach (var e in Json.List(m, "items")) {
            string id = Json.Str(e, "id") ?? "";
            if (!Regex.IsMatch(id, @"^[\w-]{6,20}$") || !seen.Add(id)) continue;   // sans doublon
            int idx; int.TryParse(Json.Str(e, "idx") ?? "0", out idx);
            string title = Json.Str(e, "title") ?? "";
            rows.Add(new PlRow { Id = id, Title = title.Length > 200 ? title.Substring(0, 200) : title, Idx = idx > 0 ? idx : rows.Count + 1 });
        }
        if (rows.Count == 0) return Err("empty");
        if (rows.Count > 2000) rows = rows.Take(2000).ToList();
        lock (L) if (run != null) return Err("busy");
        lock (Mgr.Items) foreach (var r in rows) r.Known = Mgr.Items.Any(i => i.Kind == "capture" && i.CapKey == r.Id && i.AudioOnly == audio && !i.Removed && i.Status == "done");
        if (OnPick == null) return Err("noui");
        // banc d'essai (instance lancee avec --dir) : « auto » coche les nouvelles sans ouvrir la fenetre
        var chosen = Json.Bool(m, "auto") && App.Dir != App.DefaultDir ? rows.Where(r => !r.Known).Select(r => r.Id).ToList() : OnPick(name, rows);
        if (chosen == null || chosen.Count == 0) return new Dictionary<string, object> { { "ok", true }, { "count", 0 } };
        var set = new HashSet<string>(chosen);
        var rn = new Run { Name = name, Quality = Json.Str(m, "quality"), AudioFormat = Json.Str(m, "audioFormat"), AudioOnly = audio };
        rn.Todo = rows.Where(r => set.Contains(r.Id)).ToList();
        rn.Width = Math.Max(2, rows.Max(r => r.Idx).ToString().Length);
        var it0 = new Item { Url = "https://www.youtube.com/", Kind = "capture", AudioOnly = audio };
        rn.Folder = Path.Combine(Sites.FolderFor(it0.Url) ?? Mgr.DefaultFolder(it0), name);
        lock (L) {
            run = rn;
            foreach (var r in rn.Todo) plan[r.Id] = new[] { rn.Folder, r.Idx.ToString().PadLeft(rn.Width, '0') + " - " };
            if (th == null) { th = new Thread(Loop) { IsBackground = true, Name = "playlist" }; th.Start(); }
        }
        App.Log("playlist : " + name + " (" + rn.Todo.Count + " videos sur " + rows.Count + ")");
        Advance();
        return new Dictionary<string, object> { { "ok", true }, { "count", rn.Todo.Count } };
    }

    // une video ne s'ouvre pas (privee, supprimee, page muette) : on passe a la suivante
    public static void Skip(string vid) { lock (L) { if (run != null && run.Cur == vid) { run.Skipped++; App.Log("playlist : video sautee " + vid); run.Cur = null; } } Advance(); }

    static void Advance() {
        string url = null, tag = null; Run r;
        lock (L) {
            r = run;
            if (r == null || r.Cur != null) return;
            while (r.Pos < r.Todo.Count) {
                var next = r.Todo[r.Pos++];
                bool have; lock (Mgr.Items) have = Mgr.Items.Any(i => i.Kind == "capture" && i.CapKey == next.Id && i.AudioOnly == r.AudioOnly && !i.Removed && i.Status == "done");
                if (have) { r.Skipped++; continue; }   // deja telechargee : jamais deux fois
                r.Cur = next.Id; r.CurAt = DateTime.Now;
                url = "https://www.youtube.com/watch?v=" + next.Id; tag = "pl:" + next.Id;
                break;
            }
            if (url == null) { App.Log("playlist : fin " + r.Name + " (" + r.Ok + " faites, " + r.Skipped + " sautees)"); run = null; return; }
        }
        Wake.Add(url, r.Quality, r.AudioOnly, r.AudioFormat, tag);
    }

    static void Loop() {
        while (true) {
            Thread.Sleep(2000);
            try {
                string cur; Run r; DateTime at;
                lock (L) { r = run; cur = r == null ? null : r.Cur; at = r == null ? DateTime.UtcNow : r.CurAt; }
                if (r == null) continue;
                if (cur == null) { Advance(); continue; }
                Item it; lock (Mgr.Items) it = Mgr.Items.Where(i => i.Kind == "capture" && i.CapKey == cur && i.AudioOnly == r.AudioOnly && !i.Removed).OrderByDescending(i => i.Added).FirstOrDefault();
                double waited = (DateTime.Now - at).TotalSeconds;
                if (it != null && it.Status == "done" && it.Finished >= at) { lock (L) { r.Ok++; r.Cur = null; } Advance(); }
                else if (it != null && it.Status == "error" && it.Finished >= at) { lock (L) { r.Skipped++; r.Cur = null; } Advance(); }
                else if ((it == null || it.Status == "error" || it.Status == "paused") && waited > 90) Skip(cur);   // rien n'est parti en 90 s
            } catch (System.Exception e) { App.Log("playlist : " + e.Message); }
        }
    }
    static object Err(string e) { return new Dictionary<string, object> { { "ok", false }, { "error", e } }; }
}
