// ShowLef - moteur : file d'attente, HTTP multi-connexions avec reprise, HLS, yt-dlp, ffmpeg.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public class Seg {
    public long Start { get; set; }
    public long End { get; set; }     // inclus
    public long Done { get; set; }    // octets deja ecrits depuis Start
    [ScriptIgnore] public bool Busy;
    [ScriptIgnore] public long Remaining { get { return End - Start + 1 - Done; } }
}

public class Item {
    public string Id { get; set; }
    public string Url { get; set; }
    public string Kind { get; set; }          // file / hls / dash / page
    public string Name { get; set; }
    public string Folder { get; set; }
    public string Title { get; set; }
    public string Referer { get; set; }
    public string UserAgent { get; set; }
    public string Quality { get; set; }
    public bool AudioOnly { get; set; }
    public string AudioFormat { get; set; }
    public string VariantUrl { get; set; }
    public List<Dictionary<string, object>> Cookies { get; set; }
    public string Status { get; set; }
    public string Error { get; set; }
    public long Total { get; set; }
    public long Received { get; set; }
    public bool Ranges { get; set; }
    public string Validator { get; set; }     // ETag ou Last-Modified releve au debut
    public List<Seg> Segs { get; set; }
    public DateTime Added { get; set; }
    public DateTime Finished { get; set; }
    public string CapVideo { get; set; }     // capture navigateur : « itag:ext » de la piste retenue
    public string CapAudio { get; set; }
    public string CapKey { get; set; }       // capture YouTube : identifiant de la video (reprise apres coupure, 0.7.12)
    public string Thumb { get; set; }        // image de la video (poster, og:image) envoyee par l'extension
    public int Prio { get; set; }            // file d'attente : plus grand = passe avant (« Telecharger ensuite », 0.7.27)
    public double Duration { get; set; }     // secondes (ffprobe sur le fichier fini), 0 = inconnue
    [System.Web.Script.Serialization.ScriptIgnore] public DateTime MergeStart { get; set; }   // debut de la conversion audio (calcul du temps restant)
    [System.Web.Script.Serialization.ScriptIgnore] public int MergePct { get; set; }   // 0..99 pendant la conversion audio (non enregistre)

    [ScriptIgnore] public double Speed;
    [ScriptIgnore] public long LastBytes;
    [ScriptIgnore] public CancellationTokenSource Cts;
    [ScriptIgnore] public Process Proc;
    [ScriptIgnore] public Thread Worker;
    [ScriptIgnore] public bool Removed;
    [ScriptIgnore] public int Gen;                 // numero du lancement en cours : un ancien travailleur qui finit apres une reprise n'ecrit plus rien
    [ScriptIgnore] public DateTime LastSeen;       // capture : dernier morceau recu de l'extension
    [ScriptIgnore] public string FilePath { get { return Path.Combine(Folder ?? "", Name ?? ""); } }
    [ScriptIgnore] public string PartPath { get { return FilePath + ".kpart"; } }
    [ScriptIgnore] public string WorkDir { get { return Path.Combine(App.TempDir, Id); } }
    [ScriptIgnore] public double Eta { get { return Speed > 1 && Total > 0 ? (Total - Received) / Speed : 0; } }

    public Item() { Status = "queued"; Added = DateTime.Now; Cookies = new List<Dictionary<string, object>>(); }
}

class StopException : Exception { }
class DuplicateException : Exception {
    public Item Existing;
    public DuplicateException(Item e) : base("duplicate") { Existing = e; }
}

// Limiteur de debit global (seau a jetons), partage par toutes les connexions.
static class Limiter {
    static readonly object L = new object();
    static double tokens; static DateTime last = DateTime.UtcNow;
    public static void Take(int n) {
        int kb = Settings.Current.SpeedLimitKB;
        if (kb <= 0) return;
        double rate = kb * 1024.0;
        // Bloc plus gros que le seau (lecture de 64 Ko a 10 Ko/s) : debite par morceaux, jamais laisse passer d'un coup
        // (revue Codex n° 17 : « n > rate » autorisait chaque gros bloc immediatement).
        while (n > 0) {
            int part = (int)Math.Min(n, Math.Max(1, rate));
            lock (L) {
                var now = DateTime.UtcNow;
                tokens = Math.Min(rate, tokens + (now - last).TotalSeconds * rate);
                last = now;
                if (tokens >= part) { tokens -= part; n -= part; continue; }
            }
            Thread.Sleep(20);
        }
    }
}

static class Net {
    static Net() {
        ServicePointManager.DefaultConnectionLimit = 256;
        ServicePointManager.Expect100Continue = false;
        try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
        catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
    }
    public const string DefaultUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    public static string CookieHeader(Item it, Uri u) {
        if (it.Cookies == null || it.Cookies.Count == 0) return null;
        var parts = new List<string>();
        foreach (var c in it.Cookies) {
            string dom = (Json.Str(c, "domain") ?? "").TrimStart('.').ToLowerInvariant();
            string host = u.Host.ToLowerInvariant();
            // Restrictions du cookie respectees (revue Codex n° 1) : hote exact, chemin, HTTPS seulement, expiration.
            if (Json.Bool(c, "hostOnly") ? host != dom : (dom.Length > 0 && host != dom && !host.EndsWith("." + dom))) continue;
            if (Json.Bool(c, "secure") && u.Scheme != "https") continue;
            string path = Json.Str(c, "path") ?? "/";
            if (path.Length > 1 && !(u.AbsolutePath == path || u.AbsolutePath.StartsWith(path.EndsWith("/") ? path : path + "/"))) continue;
            double exp;
            if (double.TryParse(Json.Str(c, "expirationDate") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out exp) && exp > 0
                && DateTimeOffset.FromUnixTimeSeconds((long)exp) < DateTimeOffset.UtcNow) continue;
            string name = Json.Str(c, "name"), val = Json.Str(c, "value");
            if (!string.IsNullOrEmpty(name)) parts.Add(name + "=" + (val ?? ""));
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    public static HttpWebRequest Req(Item it, string url, long from, long to) {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.UserAgent = string.IsNullOrEmpty(it.UserAgent) ? DefaultUA : it.UserAgent;
        if (!string.IsNullOrEmpty(it.Referer)) r.Referer = it.Referer;
        string ck = CookieHeader(it, r.RequestUri);
        if (ck != null) r.Headers[HttpRequestHeader.Cookie] = ck;
        r.Accept = "*/*";
        r.AllowAutoRedirect = true;
        r.Timeout = 30000; r.ReadWriteTimeout = 30000;
        r.AutomaticDecompression = DecompressionMethods.None;
        if (from >= 0) { if (to >= 0) r.AddRange(from, to); else r.AddRange(from); }
        return r;
    }

    public static HttpWebResponse Resp(HttpWebRequest r) {
        try { return (HttpWebResponse)r.GetResponse(); }
        catch (WebException e) {
            var hr = e.Response as HttpWebResponse;
            if (hr != null) { int code = (int)hr.StatusCode; hr.Close(); throw new HttpFail(code); }
            throw;
        }
    }

    public static string Text(Item it, string url) {
        using (var resp = Resp(Req(it, url, -1, -1)))
        using (var s = resp.GetResponseStream())
        using (var rd = new StreamReader(s, Encoding.UTF8)) return rd.ReadToEnd();
    }
    public static byte[] Bytes(Item it, string url, CancellationToken ct) {
        using (var resp = Resp(Req(it, url, -1, -1)))
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
            var buf = new byte[65536]; int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); Limiter.Take(n); ms.Write(buf, 0, n); }
            return ms.ToArray();
        }
    }
    public static string Abs(string baseUrl, string rel) { return new Uri(new Uri(baseUrl), rel.Trim()).ToString(); }
}

// Erreur -> code court partage par le programme (Ui.Error, notification) et l'extension (champ « code » de list).
// Un 403 ne dit pas « connectez-vous » avec certitude : « le site refuse l'acces » + le geste (revue Codex n° 22).
static class ErrCode {
    public static string Of(string e) {
        e = e ?? "";
        if (e.Length == 0) return null;
        var m = Regex.Match(e, @"HTTP (\d{3})");
        if (m.Success) {
            int c = int.Parse(m.Groups[1].Value);
            if (c == 401 || c == 403) return "denied";
            if (c == 404 || c == 410) return "gone";
            if (c == 418) return "blocked";
            if (c == 429 || c >= 500) return "busy";
        }
        if (e == Tr.T("e_capStopped")) return "capStopped";
        if (e == Tr.T("e_drm") || e.Contains("DRM")) return "drm";
        if (e == Tr.T("e_noFormat")) return "noFormat";
        if (e == Tr.T("e_tls")) return "tls";
        if (e == Tr.T("e_blocked")) return "blocked";
        if (Regex.IsMatch(e, "not enough space|espace insuffisant|disk (is )?full|There is not enough|No space left", RegexOptions.IgnoreCase)) return "disk";
        if (Regex.IsMatch(e, "incomplete|timed? ?out|d.lai|connexion|connection|aborted|reset|refus|unable to connect|resolve", RegexOptions.IgnoreCase)) return "net";
        return null;
    }
}

class HttpFail : Exception {
    public int Code;
    public HttpFail(int c) : base("HTTP " + c) { Code = c; }
    public bool Fatal { get { return Code >= 400 && Code < 500 && Code != 408 && Code != 429; } }
}

// Telechargement HTTP d'un fichier : N connexions par plages, reprise, decoupage dynamique
// (une connexion qui finit coupe en deux le plus gros segment restant, comme IDM).
static class HttpDl {
    const long MinSplit = 1024 * 1024;

    public static void Probe(Item it) {
        var r = Net.Req(it, it.Url, 0, 0);
        using (var resp = Net.Resp(r)) {
            it.Url = resp.ResponseUri.ToString();
            string cr = resp.Headers["Content-Range"];
            if ((int)resp.StatusCode == 206 && cr != null && cr.Contains("/")) {
                long t; it.Ranges = long.TryParse(cr.Substring(cr.LastIndexOf('/') + 1), out t);
                it.Total = it.Ranges ? t : -1;
            } else { it.Ranges = false; it.Total = resp.ContentLength; }
            it.Validator = resp.Headers["ETag"] ?? resp.Headers["Last-Modified"];
            string cd = resp.Headers["Content-Disposition"];
            if (cd != null && it.Name != null && it.Name.StartsWith("~")) {
                var m = Regex.Match(cd, "filename\\*=UTF-8''([^;]+)", RegexOptions.IgnoreCase);
                string fn = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
                if (fn == null) { m = Regex.Match(cd, "filename=\"?([^\";]+)\"?", RegexOptions.IgnoreCase); if (m.Success) fn = m.Groups[1].Value; }
                if (!string.IsNullOrEmpty(fn)) it.Name = Fmt.SafeName(Fmt.ShortFileName(fn));
            }
            if (it.Name != null && it.Name.StartsWith("~")) {
                it.Name = it.Name.Substring(1);
                if (Path.GetExtension(it.Name).Length == 0) it.Name += ExtFromType(resp.ContentType);
            }
        }
    }

    public static string ExtFromType(string ct) {
        ct = (ct ?? "").ToLowerInvariant();
        if (ct.Contains("mp4")) return ct.StartsWith("audio") ? ".m4a" : ".mp4";
        if (ct.Contains("webm")) return ".webm";
        if (ct.Contains("mpeg") && ct.StartsWith("audio")) return ".mp3";
        if (ct.Contains("ogg")) return ".ogg";
        if (ct.Contains("matroska")) return ".mkv";
        if (ct.Contains("quicktime")) return ".mov";
        if (ct.Contains("pdf")) return ".pdf";
        if (ct.Contains("zip")) return ".zip";
        return "";
    }

    // Reprise : si la taille ou l'ETag/Last-Modified ont change, les morceaux deja recus ne vont plus
    // avec le fichier distant -> on repart de zero plutot que de coller deux versions.
    static void CheckUnchanged(Item it) {
        if (it.Segs == null || it.Segs.Count == 0 || it.Total <= 0) return;
        try {
            using (var resp = Net.Resp(Net.Req(it, it.Url, 0, 0))) {
                string cr = resp.Headers["Content-Range"];
                long t = -1;
                if (cr != null && cr.Contains("/")) long.TryParse(cr.Substring(cr.LastIndexOf('/') + 1), out t);
                string v = resp.Headers["ETag"] ?? resp.Headers["Last-Modified"];
                bool changed = (t > 0 && t != it.Total) || (!string.IsNullOrEmpty(it.Validator) && !string.IsNullOrEmpty(v) && v != it.Validator);
                if (changed) {
                    App.Log("fichier distant modifie, reprise annulee : " + it.Url);
                    it.Segs = null; it.Total = 0; it.Received = 0;
                    try { File.Delete(it.PartPath); } catch { }
                }
            }
        } catch (HttpFail) { } catch (WebException) { }   // controle impossible : on tente la reprise
    }

    public static void Run(Item it, CancellationToken ct) {
        CheckUnchanged(it);
        if (it.Segs == null || it.Segs.Count == 0 || it.Total <= 0) {
            Mgr.Unique(it, true);
            Probe(it);
            Mgr.Unique(it, false);
            it.Segs = new List<Seg>();
            it.Received = 0;
            if (it.Ranges && it.Total > 0) {
                int n = it.Total < 2 * MinSplit ? 1 : (int)Math.Min(Settings.Current.Connections, it.Total / MinSplit);
                long size = it.Total / n;
                for (int i = 0; i < n; i++)
                    it.Segs.Add(new Seg { Start = i * size, End = i == n - 1 ? it.Total - 1 : (i + 1) * size - 1 });
            }
        }
        Directory.CreateDirectory(it.Folder);
        if (!it.Ranges || it.Total <= 0) { Single(it, ct); return; }
        // reprise apres pause : les segments de la session precedente ne sont plus « occupes »
        lock (it) foreach (var sg in it.Segs) sg.Busy = false;
        if (!File.Exists(it.PartPath)) { lock (it) { foreach (var sg in it.Segs) sg.Done = 0; it.Received = 0; } }

        using (var fs = new FileStream(it.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) {
            if (fs.Length != it.Total) fs.SetLength(it.Total);
        }
        int conns = Math.Max(1, Math.Min(Settings.Current.Connections, 32));
        Exception fatal = null;
        var threads = new List<Thread>();
        for (int i = 0; i < conns; i++) {
            var th = new Thread(() => {
                try { Conn(it, ct); }
                catch (OperationCanceledException) { }
                catch (Exception e) { lock (it) { if (fatal == null) fatal = e; } }
            });
            th.IsBackground = true; th.Start(); threads.Add(th);
        }
        foreach (var th in threads) th.Join();
        ct.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;
        lock (it) { if (it.Segs.Any(s => s.Remaining > 0)) throw new Exception("incomplete"); }
        Finish(it);
    }

    static Seg Next(Item it) {
        lock (it) {
            var free = it.Segs.FirstOrDefault(s => !s.Busy && s.Remaining > 0);
            if (free != null) { free.Busy = true; return free; }
            var big = it.Segs.Where(s => s.Busy && s.Remaining > 2 * MinSplit).OrderByDescending(s => s.Remaining).FirstOrDefault();
            if (big == null) return null;
            long pos = big.Start + big.Done;
            long mid = pos + big.Remaining / 2;
            var n = new Seg { Start = mid, End = big.End, Busy = true };
            big.End = mid - 1;
            it.Segs.Add(n);
            return n;
        }
    }

    static void Conn(Item it, CancellationToken ct) {
        var buf = new byte[65536];
        using (var fs = new FileStream(it.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
            Seg s;
            while ((s = Next(it)) != null) {
              try {
                int fails = 0;
                while (true) {
                    ct.ThrowIfCancellationRequested();
                    long pos; long end;
                    lock (it) { pos = s.Start + s.Done; end = s.End; }
                    if (pos > end) break;
                    long before; lock (it) before = s.Done;
                    try {
                        var req = Net.Req(it, it.Url, pos, end);
                        using (var resp = Net.Resp(req)) {
                            if ((int)resp.StatusCode != 206) throw new Exception("range refused");
                            using (var st = resp.GetResponseStream()) {
                                int n;
                                while ((n = st.Read(buf, 0, buf.Length)) > 0) {
                                    ct.ThrowIfCancellationRequested();
                                    Limiter.Take(n);
                                    long w;
                                    lock (it) {
                                        pos = s.Start + s.Done;
                                        w = Math.Min(n, s.End - pos + 1);   // le segment a pu etre coupe en deux
                                        if (w <= 0) break;
                                    }
                                    fs.Seek(pos, SeekOrigin.Begin);
                                    fs.Write(buf, 0, (int)w);
                                    lock (it) { s.Done += w; it.Received += w; }
                                    if (w < n) break;
                                }
                            }
                        }
                        bool progressed;
                        lock (it) { if (s.Remaining <= 0) break; progressed = s.Done > before; }
                        // flux coupe proprement avant la fin : on reprend. Mais une reponse 206 VIDE (aucun octet) n'est pas
                        // un progres : elle compte comme un echec, sinon boucle sans fin (revue Codex n° 14).
                        if (progressed) fails = 0;
                        else { if (++fails > Settings.Current.Retries) throw new Exception("incomplete"); Thread.Sleep(Math.Min(10000, 500 * fails)); }
                    } catch (OperationCanceledException) { throw; }
                    catch (HttpFail e) { if (e.Fatal || ++fails > Settings.Current.Retries) throw; Thread.Sleep(Math.Min(10000, 500 * fails)); }
                    catch (Exception e) {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException();
                        if (e.Message == "range refused" || ++fails > Settings.Current.Retries) throw;
                        Thread.Sleep(Math.Min(10000, 500 * fails));
                    }
                }
              } finally { lock (it) s.Busy = false; }   // pause comprise : sinon le segment reste « occupe » a la reprise
            }
        }
    }

    // Serveur sans plages (ou taille inconnue) : un seul flux, reprise impossible.
    static void Single(Item it, CancellationToken ct) {
        var buf = new byte[65536];
        int fails = 0;
        while (true) {
            try {
                it.Received = 0;
                using (var resp = Net.Resp(Net.Req(it, it.Url, -1, -1)))
                using (var st = resp.GetResponseStream())
                using (var fs = new FileStream(it.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                    if (resp.ContentLength > 0) it.Total = resp.ContentLength;
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) {
                        ct.ThrowIfCancellationRequested();
                        Limiter.Take(n); fs.Write(buf, 0, n); it.Received += n;
                    }
                }
                if (it.Total > 0 && it.Received < it.Total) throw new Exception("incomplete");
                it.Total = it.Received;
                Finish(it);
                return;
            } catch (OperationCanceledException) { throw; }
            catch (HttpFail e) { if (e.Fatal || ++fails > Settings.Current.Retries) throw; Thread.Sleep(1000 * fails); }
            catch (Exception) { if (ct.IsCancellationRequested || ++fails > Settings.Current.Retries) throw; Thread.Sleep(1000 * fails); }
        }
    }

    // Publication du fichier fini. Un fichier apparu a ce nom PENDANT le telechargement (autre programme, autre
    // telechargement) n'est jamais efface : on prend un autre nom (revue Codex n° 8).
    static void Finish(Item it) {
        string part = it.PartPath;
        if (File.Exists(it.FilePath)) Mgr.Unique(it, false);
        File.Move(part, it.FilePath);
    }
}

// HLS (.m3u8) : master -> variante selon la qualite, segments en parallele, AES-128,
// init fMP4 (EXT-X-MAP), piste audio separee, assemblage puis remux MP4 par ffmpeg.
static class Hls {
    class S { public string Uri; public string KeyUri; public byte[] Iv; public long Seq; public long BrLen = -1, BrOff = -1; public string Method; }
    class Pl { public List<S> Segs = new List<S>(); public string Map; public long MapLen = -1, MapOff = 0; public bool Ended; public double Duration; }

    static Dictionary<string, string> Attrs(string line) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int c = line.IndexOf(':'); if (c < 0) return d;
        foreach (Match m in Regex.Matches(line.Substring(c + 1), "([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)"))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    // Choix de la variante video (et de la piste audio associee) dans un master.
    public static void Pick(Item it, string url, string text, out string videoUrl, out string audioUrl) {
        videoUrl = url; audioUrl = null;
        if (!text.Contains("#EXT-X-STREAM-INF")) return;
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        var vars = new List<Tuple<long, int, string, string>>();   // bande passante, hauteur, url, groupe audio
        var audios = new Dictionary<string, string>();
        for (int i = 0; i < lines.Length; i++) {
            if (lines[i].StartsWith("#EXT-X-MEDIA:") && lines[i].Contains("TYPE=AUDIO")) {
                var a = Attrs(lines[i]); string g, u;
                if (a.TryGetValue("GROUP-ID", out g) && a.TryGetValue("URI", out u)) {
                    string def; bool isDef = a.TryGetValue("DEFAULT", out def) && def == "YES";
                    if (!audios.ContainsKey(g) || isDef) audios[g] = Net.Abs(url, u);
                }
            }
            if (lines[i].StartsWith("#EXT-X-STREAM-INF")) {
                var a = Attrs(lines[i]);
                string bw, res, ag; long b = 0; int h = 0;
                if (a.TryGetValue("BANDWIDTH", out bw)) long.TryParse(bw, out b);
                if (a.TryGetValue("RESOLUTION", out res) && res.Contains("x")) int.TryParse(res.Split('x')[1], out h);
                a.TryGetValue("AUDIO", out ag);
                int j = i + 1; while (j < lines.Length && (lines[j].Length == 0 || lines[j].StartsWith("#"))) j++;
                if (j < lines.Length) vars.Add(Tuple.Create(b, h, Net.Abs(url, lines[j]), ag));
            }
        }
        if (vars.Count == 0) return;
        Tuple<long, int, string, string> pick = null;
        if (!string.IsNullOrEmpty(it.VariantUrl)) pick = vars.FirstOrDefault(v => v.Item3 == it.VariantUrl);
        int q;
        if (pick == null && int.TryParse(it.Quality, out q)) {
            pick = vars.Where(v => v.Item2 > 0 && v.Item2 <= q).OrderByDescending(v => v.Item2).ThenByDescending(v => v.Item1).FirstOrDefault();
            // aucune sous le plafond (« fichier leger » sur un flux 720p minimum) : la plus petite, jamais la plus grosse
            if (pick == null) pick = vars.Where(v => v.Item2 > 0).OrderBy(v => v.Item2).ThenBy(v => v.Item1).FirstOrDefault();
        }
        if (pick == null && it.AudioOnly) pick = vars.OrderBy(v => v.Item1).First();
        if (pick == null) pick = vars.OrderByDescending(v => v.Item2).ThenByDescending(v => v.Item1).First();
        videoUrl = pick.Item3;
        string au;
        if (pick.Item4 != null && audios.TryGetValue(pick.Item4, out au)) audioUrl = au;
    }

    static Pl Parse(string url, string text) {
        var p = new Pl();
        string method = null, keyUri = null; byte[] iv = null; long seq = 0;
        long nextOff = 0; long brLen = -1, brOff = -1;
        foreach (string raw in text.Split('\n')) {
            string l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(l.Substring(22), out seq);
            else if (l.StartsWith("#EXT-X-ENDLIST")) p.Ended = true;
            else if (l.StartsWith("#EXT-X-KEY:")) {
                var a = Attrs(l); string m, u, v;
                method = a.TryGetValue("METHOD", out m) ? m : "NONE";
                keyUri = a.TryGetValue("URI", out u) ? Net.Abs(url, u) : null;
                iv = a.TryGetValue("IV", out v) ? Hex(v) : null;
                if (method == "SAMPLE-AES" || method == "SAMPLE-AES-CTR") throw new Exception(Tr.T("e_drm"));
                string kf; if (a.TryGetValue("KEYFORMAT", out kf) && kf != "identity") throw new Exception(Tr.T("e_drm"));
            } else if (l.StartsWith("#EXT-X-MAP:")) {
                var a = Attrs(l); string u, br;
                if (a.TryGetValue("URI", out u)) p.Map = Net.Abs(url, u);
                // plage de l'initialisation (fMP4 unique decoupe par plages) : sans elle, le fichier entier etait colle en tete
                if (a.TryGetValue("BYTERANGE", out br)) { var b2 = br.Split('@'); long.TryParse(b2[0], out p.MapLen); if (b2.Length > 1) long.TryParse(b2[1], out p.MapOff); }
            } else if (l.StartsWith("#EXT-X-BYTERANGE:")) {
                string[] br = l.Substring(17).Split('@');
                long.TryParse(br[0], out brLen);
                if (br.Length > 1) long.TryParse(br[1], out brOff); else brOff = nextOff;
                nextOff = brOff + brLen;
            } else if (l.StartsWith("#EXTINF:")) {
                double d; string v = l.Substring(8).Split(',')[0];
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) p.Duration += d;
            } else if (!l.StartsWith("#")) {
                p.Segs.Add(new S { Uri = Net.Abs(url, l), Method = method, KeyUri = keyUri, Iv = iv, Seq = seq, BrLen = brLen, BrOff = brOff });
                seq++; brLen = -1; brOff = -1;
            }
        }
        return p;
    }

    static byte[] Hex(string s) {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
        s = s.PadLeft(32, '0');
        var b = new byte[16];
        for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    public static void Run(Item it, CancellationToken ct) {
        string text = Net.Text(it, it.Url);
        if (!text.TrimStart().StartsWith("#EXTM3U")) throw new Exception(Tr.T("e_notHls"));
        string vUrl, aUrl;
        Pick(it, it.Url, text, out vUrl, out aUrl);
        Pl video = Parse(vUrl, vUrl == it.Url ? text : Net.Text(it, vUrl));
        Pl audio = aUrl != null ? Parse(aUrl, Net.Text(it, aUrl)) : null;
        if (it.AudioOnly && audio != null) { video = audio; audio = null; }
        if (!video.Ended) { Live(it, ct, vUrl, aUrl); return; }

        Directory.CreateDirectory(it.WorkDir);
        int total = video.Segs.Count + (audio != null ? audio.Segs.Count : 0);
        var jobs = new List<Tuple<S, string>>();
        for (int i = 0; i < video.Segs.Count; i++) jobs.Add(Tuple.Create(video.Segs[i], Path.Combine(it.WorkDir, "v" + i.ToString("00000"))));
        if (audio != null) for (int i = 0; i < audio.Segs.Count; i++) jobs.Add(Tuple.Create(audio.Segs[i], Path.Combine(it.WorkDir, "a" + i.ToString("00000"))));

        // Reprise : les morceaux deja recus ne valent que pour la MEME liste (adresses, plages, cles). Liste changee
        // entre-temps (playlist renouvelee) : on repart de zero plutot que de coller deux videos (revue Codex n° 13).
        // Adresses SANS leurs parametres : un jeton d'acces qui change a chaque lecture ne doit pas tout faire recommencer.
        Func<string, string> bare = u => u == null ? "" : u.Split('?')[0];
        string ident = string.Join("\n", jobs.Select(j => bare(j.Item1.Uri) + "|" + j.Item1.BrOff + "|" + j.Item1.BrLen + "|" + bare(j.Item1.KeyUri)))
            + "\nmap:" + bare(video.Map) + "|" + (audio != null ? bare(audio.Map) : "");
        string identFile = Path.Combine(it.WorkDir, "segments.id");
        string oldIdent = File.Exists(identFile) ? File.ReadAllText(identFile, Encoding.UTF8) : null;
        if (oldIdent != ident) {
            if (oldIdent != null) App.Log("playlist changee depuis la pause, morceaux effaces : " + it.Url);
            foreach (var j in jobs) try { if (File.Exists(j.Item2)) File.Delete(j.Item2); } catch { }
            File.WriteAllText(identFile, ident, Encoding.UTF8);
        }
        int done = jobs.Count(j => File.Exists(j.Item2));
        long bytes = jobs.Where(j => File.Exists(j.Item2)).Sum(j => new FileInfo(j.Item2).Length);
        it.Received = bytes;
        var keys = new Dictionary<string, byte[]>();
        int idx = -1; Exception fatal = null;
        var threads = new List<Thread>();
        int conns = Math.Max(1, Math.Min(Settings.Current.Connections, 16));
        for (int t = 0; t < conns; t++) {
            var th = new Thread(() => {
                try {
                    while (true) {
                        int i = Interlocked.Increment(ref idx);
                        if (i >= jobs.Count || fatal != null) return;
                        var j = jobs[i];
                        if (File.Exists(j.Item2)) continue;
                        byte[] data = Fetch(it, j.Item1, keys, ct);
                        File.WriteAllBytes(j.Item2 + ".tmp", data);
                        File.Move(j.Item2 + ".tmp", j.Item2);
                        lock (it) {
                            it.Received += data.Length; done++;
                            it.Total = (long)(it.Received * (double)total / Math.Max(1, done));
                        }
                    }
                } catch (OperationCanceledException) { }
                catch (Exception e) { lock (it) { if (fatal == null) fatal = e; } }
            });
            th.IsBackground = true; th.Start(); threads.Add(th);
        }
        foreach (var th in threads) th.Join();
        ct.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;

        it.Status = "merging";
        string vFile = Path.Combine(it.WorkDir, video.Map != null ? "video.mp4" : "video.ts");
        Concat(it, video, "v", vFile, ct);
        string aFile = null;
        if (audio != null) { aFile = Path.Combine(it.WorkDir, audio.Map != null ? "audio.mp4" : "audio.ts"); Concat(it, audio, "a", aFile, ct); }

        Directory.CreateDirectory(it.Folder);
        string ff = Tools.Ffmpeg;
        if (ff == null) {
            if (aFile != null) throw new Exception(Tr.T("e_noFf"));
            it.Name = Path.ChangeExtension(it.Name, Path.GetExtension(vFile));
            Mgr.Unique(it, false);
            File.Move(vFile, it.FilePath);
        } else {
            it.Name = Path.ChangeExtension(it.Name, it.AudioOnly && aFile == null && video.Map == null ? ".m4a" : ".mp4");
            Mgr.Unique(it, false);
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", Q(vFile) };
            if (aFile != null) args.AddRange(new[] { "-i", Q(aFile), "-map", "0:v?", "-map", "0:a?", "-map", "1:a" });
            args.AddRange(new[] { "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", Q(it.FilePath) });
            if (Proc.Run(it, ff, string.Join(" ", args), null, ct) != 0) {
                // certains flux ont un audio non AAC : on retente sans le filtre
                args.RemoveRange(args.Count - 5, 2);
                if (Proc.Run(it, ff, string.Join(" ", args), null, ct) != 0) throw new Exception("ffmpeg remux failed");
            }
        }
        try { Directory.Delete(it.WorkDir, true); } catch { }
    }

    static byte[] Fetch(Item it, S s, Dictionary<string, byte[]> keys, CancellationToken ct) {
        int fails = 0;
        while (true) {
            ct.ThrowIfCancellationRequested();
            try {
                byte[] data;
                if (s.BrLen > 0) {
                    using (var resp = Net.Resp(Net.Req(it, s.Uri, s.BrOff, s.BrOff + s.BrLen - 1))) {
                        // Plage ignoree (200 + fichier entier) ou mauvaise plage : jamais enregistree comme un morceau (revue Codex n° 12).
                        string cr = resp.Headers["Content-Range"] ?? "";
                        if ((int)resp.StatusCode != 206 || !cr.StartsWith("bytes " + s.BrOff + "-")) throw new Exception("range refused");
                        using (var st = resp.GetResponseStream()) using (var ms = new MemoryStream()) { st.CopyTo(ms); data = ms.ToArray(); }
                    }
                    if (data.Length != s.BrLen) throw new Exception("incomplete");
                    Limiter.Take(data.Length);
                } else data = Net.Bytes(it, s.Uri, ct);
                if (s.Method == "AES-128" && s.KeyUri != null) {
                    byte[] key;
                    lock (keys) { if (!keys.TryGetValue(s.KeyUri, out key)) { key = Net.Bytes(it, s.KeyUri, ct); keys[s.KeyUri] = key; } }
                    byte[] iv = s.Iv;
                    if (iv == null) { iv = new byte[16]; long q = s.Seq; for (int i = 15; i >= 8; i--) { iv[i] = (byte)(q & 0xff); q >>= 8; } }
                    using (var aes = new AesManaged { Key = key, IV = iv, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
                    using (var dec = aes.CreateDecryptor()) data = dec.TransformFinalBlock(data, 0, data.Length);
                }
                return data;
            } catch (OperationCanceledException) { throw; }
            catch (HttpFail e) { if (e.Fatal || ++fails > Settings.Current.Retries) throw; Thread.Sleep(500 * fails); }
            catch (System.Exception e) { if (ct.IsCancellationRequested || e.Message == "range refused" || ++fails > Settings.Current.Retries) throw; Thread.Sleep(500 * fails); }
        }
    }

    static void Concat(Item it, Pl p, string prefix, string outFile, CancellationToken ct) {
        using (var o = new FileStream(outFile, FileMode.Create, FileAccess.Write)) {
            if (p.Map != null) {
                byte[] m = p.MapLen > 0 ? Fetch(it, new S { Uri = p.Map, BrLen = p.MapLen, BrOff = p.MapOff }, new Dictionary<string, byte[]>(), ct) : Net.Bytes(it, p.Map, ct);
                o.Write(m, 0, m.Length);
            }
            for (int i = 0; i < p.Segs.Count; i++) {
                ct.ThrowIfCancellationRequested();
                string f = Path.Combine(it.WorkDir, prefix + i.ToString("00000"));
                using (var s = File.OpenRead(f)) s.CopyTo(o);
            }
        }
    }

    // Direct (pas d'ENDLIST) : enregistrement par ffmpeg jusqu'a la pause.
    static void Live(Item it, CancellationToken ct, string vUrl, string aUrl) {
        string ff = Tools.Ffmpeg;
        if (ff == null) throw new Exception(Tr.T("e_noFf"));
        it.Name = Path.ChangeExtension(it.Name, ".mkv");
        Mgr.Unique(it, false);
        Directory.CreateDirectory(it.Folder);
        string hdr = "-user_agent " + Q(string.IsNullOrEmpty(it.UserAgent) ? Net.DefaultUA : it.UserAgent);
        if (!string.IsNullOrEmpty(it.Referer)) hdr += " -referer " + Q(it.Referer);
        string ck = Net.CookieHeader(it, new Uri(vUrl));
        if (ck != null) hdr += " -headers " + Q("Cookie: " + ck + "\r\n");
        string args = "-y -hide_banner -loglevel error " + hdr + " -i " + Q(vUrl);
        if (aUrl != null) args += " " + hdr + " -i " + Q(aUrl) + " -map 0:v? -map 1:a";
        args += " -c copy " + Q(it.FilePath);
        Proc.Run(it, ff, args, null, ct, true);
        if (File.Exists(it.FilePath)) { it.Total = it.Received = new FileInfo(it.FilePath).Length; return; }
        ct.ThrowIfCancellationRequested();
        throw new Exception("live recording failed");
    }

    public static string Q(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
}

// Lancement d'un outil (ffmpeg / yt-dlp), lignes de sortie lues au fil de l'eau.
static class Proc {
    public static int Run(Item it, string exe, string args, Action<string> onLine, CancellationToken ct, bool gracefulStop = false, IDictionary<string, string> env = null) {
        var psi = new ProcessStartInfo(exe, args) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data == null) return; if (onLine != null) onLine(e.Data); lock (err) { if (err.Length < 4000) err.AppendLine(e.Data); } };
        p.Start();
        if (it != null) it.Proc = p;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        while (!p.WaitForExit(250)) {
            if (gracefulStop && it != null && File.Exists(it.FilePath)) { try { it.Received = new FileInfo(it.FilePath).Length; it.Total = 0; } catch { } }
            if (ct.IsCancellationRequested) {
                try {
                    if (gracefulStop) { p.StandardInput.Write("q"); p.StandardInput.Flush(); if (p.WaitForExit(8000)) break; }
                    KillTree(p.Id);
                } catch { }
                p.WaitForExit(5000);
                break;
            }
        }
        p.WaitForExit();
        if (it != null) it.Proc = null;
        if (p.ExitCode != 0 && it != null && !ct.IsCancellationRequested) {
            string e = err.ToString().Trim();
            var last = e.Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? e.Split('\n').LastOrDefault();
            if (!string.IsNullOrEmpty(last)) it.Error = Regex.Replace(last.Trim(), @"^ERROR:\s*(\[[^\]]+\]\s*)?", "");
            App.Log(Path.GetFileName(exe) + " rc=" + p.ExitCode + " : " + e);
        }
        return p.ExitCode;
    }
    public static void KillTree(int pid) {
        try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + pid) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
    }
}

// Reseau filtre qui INSPECTE le HTTPS (NetFree, Techloq...) : chaque site arrive signe par l'autorite du filtre.
// Windows lui fait confiance (Chrome et le moteur HTTP du programme marchent), yt-dlp (Python) non, meme avec
// no-certifi : CERTIFICATE_VERIFY_FAILED « unable to get local issuer certificate » puis « No video formats found! »
// (journal de Motti, 24/10/2026, NetFree ; reproduit au banc tests/filtre-tls.mjs).
// On ne baisse AUCUNE verification : on demande a WINDOWS la chaine du site, et seulement si Windows la valide, ses
// certificats sont donnes a yt-dlp en plus du magasin Windows (SSL_CERT_FILE, lu par Python apres ses magasins).
// Un site que Windows refuse reste refuse. Appris une fois pour la session : le filtre ne change pas d'autorite.
static class TlsTrust {
    static readonly object L = new object();
    static readonly Dictionary<string, X509Certificate2> certs = new Dictionary<string, X509Certificate2>();
    static readonly HashSet<string> asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static string bundle;
    // banc seulement (--trust-ca avec --dir) : autorite du filtre simule, tenue pour « validee par Windows »
    public static X509Certificate2 BenchCa;

    public static bool IsCertError(string line) { return line != null && line.Contains("CERTIFICATE_VERIFY_FAILED"); }

    public static IDictionary<string, string> Env() {
        lock (L) return bundle == null ? null : new Dictionary<string, string> { { "SSL_CERT_FILE", bundle } };
    }

    // true = de NOUVEAUX certificats ont ete appris (un nouvel essai a un sens)
    public static bool Learn(params string[] urls) {
        bool added = false;
        foreach (string u in urls) {
            string host; try { host = new Uri(u).Host; } catch { continue; }
            if (string.IsNullOrEmpty(host)) continue;
            lock (L) { if (!asked.Add(host)) continue; }
            List<X509Certificate2> chain = null;
            try { chain = ChainOf(host); } catch (System.Exception e) { App.Log("tls " + host + " : chaine illisible (" + e.GetType().Name + " " + e.Message + ")"); }
            if (chain == null) continue;
            lock (L) foreach (var c in chain) if (!certs.ContainsKey(c.Thumbprint)) { certs[c.Thumbprint] = c; added = true; App.Log("tls " + host + " : autorite apprise " + c.Subject); }
        }
        if (!added) return false;
        lock (L) {
            var sb = new StringBuilder();
            foreach (var c in certs.Values)
                sb.Append("-----BEGIN CERTIFICATE-----\n").Append(Convert.ToBase64String(c.RawData, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n")).Append("\n-----END CERTIFICATE-----\n");
            string f = Path.Combine(App.Dir, "tls-trust.pem");
            Directory.CreateDirectory(App.Dir);
            File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
            bundle = f;
        }
        return true;
    }

    // Autorites de la chaine que Windows a validee pour cet hote (sans le certificat du site lui-meme) ; null si refusee.
    // Le juge est SChannel lui-meme (erreurs de la poignee de main : chaine ET nom), pas un controle refait a la main :
    // 0.7.45 refaisait chaine + nom et ne disait que « refusee » ; chez Motti (NetFree, 24/10) la raison est restee
    // inconnue. Desormais le journal dit POURQUOI (erreurs, statut de chaque maillon, emetteurs).
    static List<X509Certificate2> ChainOf(string host) {
        var pe = SslPolicyErrors.None;
        var els = new List<X509Certificate2>();
        var why = new List<string>();
        using (var tcp = Open(host, 443))
        using (var ssl = new SslStream(tcp.GetStream(), false, (s, cert, ch, err) => {
            pe = err;
            if (ch != null && ch.ChainStatus.Length > 0)
                why.Add("statut " + string.Join("+", ch.ChainStatus.Select(x => x.Status.ToString()).ToArray()));
            if (ch != null)
                foreach (X509ChainElement e in ch.ChainElements) {
                    els.Add(new X509Certificate2(e.Certificate));
                    string st = string.Join("+", e.ChainElementStatus.Select(x => x.Status.ToString()).Where(x => x != "NoError").ToArray());
                    why.Add(e.Certificate.Subject + " <- " + e.Certificate.Issuer + (st.Length > 0 ? " [" + st + "]" : ""));
                }
            return true;   // rien n'est envoye : on ferme juste apres
        })) {
            ssl.ReadTimeout = ssl.WriteTimeout = 8000;
            // TLS 1.2 explicite : sans lui le .NET Framework propose SSL3 / TLS 1.0, refuses partout (« Echec d'un appel a SSPI »)
            ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
        }
        bool ok = pe == SslPolicyErrors.None;
        if (!ok && BenchCa != null && pe == SslPolicyErrors.RemoteCertificateChainErrors && els.Count > 0) {
            // banc : meme chaine, l'autorite du filtre simule en plus ; refusee si ce n'est pas ELLE qui manquait
            using (var chain = new X509Chain()) {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ExtraStore.Add(BenchCa);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                var all = chain.Build(els[0]) ? chain.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate).ToList() : null;
                if (all != null && all.Count > 1 && all[all.Count - 1].Thumbprint == BenchCa.Thumbprint) { ok = true; els = all; }
            }
        }
        if (!ok) {
            // ou est l'emetteur du dernier maillon ? (absent partout = autorite du filtre non installee dans Windows)
            string top = els.Count > 0 ? els[els.Count - 1].Issuer : "", seen = "";
            foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                foreach (var nm in new[] { StoreName.Root, StoreName.CertificateAuthority, StoreName.AuthRoot })
                    try {
                        var st = new X509Store(nm, loc); st.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                        int n = st.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, top, false).Count; st.Close();
                        if (n > 0) seen += " " + loc + "/" + nm;
                    } catch { }
            App.Log("tls " + host + " : refusee par Windows (" + pe + ") : " + string.Join(" | ", why.ToArray()) + " ; emetteur dans :" + (seen.Length > 0 ? seen : " aucun magasin"));
            return null;
        }
        App.Log("tls " + host + " : chaine validee par Windows : " + string.Join(" | ", why.ToArray()));
        return els.Skip(1).ToList();
    }

    // Meme chemin reseau que yt-dlp : proxy des variables d'environnement, sinon celui de Windows, sinon direct.
    static TcpClient Open(string host, int port) {
        Uri px = null;
        string env = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy");
        if (!string.IsNullOrEmpty(env)) { try { px = new Uri(env.Contains("://") ? env : "http://" + env); } catch { } }
        if (px == null) {
            try { var target = new Uri("https://" + host + "/"); var sys = WebRequest.GetSystemWebProxy().GetProxy(target); if (sys != null && sys != target) px = sys; } catch { }
        }
        var tcp = new TcpClient { ReceiveTimeout = 8000, SendTimeout = 8000 };
        if (px == null) { tcp.Connect(host, port); return tcp; }
        tcp.Connect(px.Host, px.Port);
        var st = tcp.GetStream();
        byte[] req = Encoding.ASCII.GetBytes("CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n\r\n");
        st.Write(req, 0, req.Length);
        var head = new StringBuilder();
        while (!head.ToString().EndsWith("\r\n\r\n")) {
            int b = st.ReadByte();
            if (b < 0 || head.Length > 8192) { tcp.Close(); throw new IOException("proxy : reponse incomplete"); }
            head.Append((char)b);
        }
        if (!Regex.IsMatch(head.ToString(), @"^HTTP/1\.[01] 200")) { tcp.Close(); throw new IOException("proxy : " + head.ToString().Split('\r')[0]); }
        return tcp;
    }
}

// yt-dlp : DASH, et « telecharger la page » (le site est analyse par yt-dlp).
static class Ytdlp {
    public static void Run(Item it, CancellationToken ct) {
        string yt = Tools.Ytdlp;
        if (yt == null) throw new Exception(Tr.T("e_noYt"));
        Directory.CreateDirectory(it.Folder);
        Directory.CreateDirectory(it.WorkDir);
        // no-certifi : magasin de certificats de Windows (filtres type NetFree / Techloq qui inspectent le HTTPS)
        // --no-quiet : « --print » rend yt-dlp muet ; sans lui ni « Retrying fragment … 418 » (refus du filtre) ni Destination/Merger
        // n'arrivaient ici (mesure 25/10 : morceaux refuses, 0 ligne 418 lue, fichier de 777 o rendu comme reussi)
        var a = new List<string> { "--no-quiet", "--newline", "--progress", "--no-colors", "--compat-options", "no-certifi", "--encoding", "utf-8", "--no-playlist", "--no-mtime", "--windows-filenames",
            "-N", Settings.Current.Connections.ToString(),
            "--progress-template", Hls.Q("download:KP %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s"),
            "--print", Hls.Q("after_move:KF %(filepath)s"),
            "-P", Hls.Q(it.Folder), "-P", Hls.Q("temp:" + it.WorkDir) };
        string baseName = Path.GetFileNameWithoutExtension(it.Name ?? "");
        a.AddRange(new[] { "-o", Hls.Q(string.IsNullOrEmpty(baseName) || it.Kind == "page" ? "%(title).150B.%(ext)s" : baseName.Replace("%", "%%") + ".%(ext)s") });
        string ff = Tools.Ffmpeg;
        if (ff != null) a.AddRange(new[] { "--ffmpeg-location", Hls.Q(ff) });
        // moteur JavaScript pour YouTube (sinon une partie des formats manque)
        string deno = Tools.Find("deno.exe", null);
        if (deno != null) a.AddRange(new[] { "--js-runtimes", Hls.Q("deno:" + deno) });
        if (!string.IsNullOrEmpty(it.Referer)) a.AddRange(new[] { "--referer", Hls.Q(it.Referer) });
        if (!string.IsNullOrEmpty(it.UserAgent)) a.AddRange(new[] { "--user-agent", Hls.Q(it.UserAgent) });
        if (Settings.Current.SpeedLimitKB > 0) a.AddRange(new[] { "--limit-rate", Settings.Current.SpeedLimitKB + "K" });
        string cookieFile = WriteCookies(it);
        if (cookieFile != null) a.AddRange(new[] { "--cookies", Hls.Q(cookieFile) });
        int q;
        if (it.AudioOnly) a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", it.AudioFormat == "original" ? "best" : it.AudioFormat == "m4a" ? "m4a" : "mp3", "--audio-quality", "0" });
        // qualite chiffree : H.264 + AAC d'abord = un MP4 que tout lecteur ouvre (sinon AV1/Opus en MKV)
        else if (int.TryParse(it.Quality, out q)) a.AddRange(new[] { "-f", Hls.Q("bv*+ba/b"), "-S", Hls.Q("res:" + q + ",vcodec:h264,acodec:m4a"), "--merge-output-format", "mp4/mkv" });
        else a.AddRange(new[] { "-f", Hls.Q("bv*+ba/b"), "--merge-output-format", "mp4/mkv" });
        a.Add("--"); a.Add(Hls.Q(it.Url));

        string final = null;
        bool tlsFail = false, blocked = false;
        var trace = new StringBuilder();
        // refus du filtre (418) = yt-dlp arrete TOUT DE SUITE : chez Motti (NetFree, 25/10) il continuait 4 a 18 min sur le DASH
        // pour 765 o (la page de refus) puis echouait a la fusion (« Invalid argument ») ; l'extension refait ensuite par Chrome
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Action<string> onLine = line => {
            if (TlsTrust.IsCertError(line)) tlsFail = true;
            if (line.Contains("HTTP Error 418") || line.Contains("Blocked by")) {   // le filtre refuse CETTE requete (le navigateur, lui, passe)
                if (!blocked) App.Log("yt-dlp arrete au premier refus du filtre : " + line.Trim());
                blocked = true; try { stop.Cancel(); } catch { }
            }
            // sortie standard de yt-dlp (destination, fusion, ffmpeg) : absente du journal sans cela
            if (trace.Length < 3000 && (line.StartsWith("[download] Destination") || line.StartsWith("[Merger]") || line.StartsWith("[ffmpeg]") || line.StartsWith("[info]") || line.StartsWith("Deleting")))
                lock (trace) trace.AppendLine(line);
            if (line.StartsWith("KP ")) {
                var p = line.Split(' ');
                long d, t, te; double sp;
                if (p.Length >= 5) {
                    if (long.TryParse(p[1], out d)) it.Received = d;
                    if (long.TryParse(p[2], out t)) it.Total = t;
                    else if (double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out sp)) it.Total = (long)sp;
                    if (long.TryParse(p[3], out te) && it.Total <= 0) it.Total = te;
                }
            } else if (line.StartsWith("KF ")) final = line.Substring(3).Trim();
            else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[VideoRemuxer]")) it.Status = "merging";
            else if (line.Contains("[download] Destination:") && (it.Name == null || it.Kind == "page")) it.Name = Path.GetFileName(line.Substring(line.IndexOf(':') + 1).Trim());
        };
        int rc = Proc.Run(it, yt, string.Join(" ", a), onLine, stop.Token, false, TlsTrust.Env());
        // reseau filtre qui inspecte le HTTPS : on apprend de Windows la chaine du site, puis UN nouvel essai
        if (rc != 0 && tlsFail && !blocked && !ct.IsCancellationRequested && TlsTrust.Learn(it.Url, it.Referer)) {
            tlsFail = false; it.Error = null; it.Received = 0;
            rc = Proc.Run(it, yt, string.Join(" ", a), onLine, stop.Token, false, TlsTrust.Env());
        }
        if (blocked && rc == 0) rc = 1;   // arrete par nous : jamais un succes, meme si le processus a eu le temps de sortir a 0
        stop.Dispose();
        if (cookieFile != null) try { File.Delete(cookieFile); } catch { }
        ct.ThrowIfCancellationRequested();
        if (rc != 0 && it.Error != null && it.Error.Contains("Invalid argument"))
            App.Log("yt-dlp sortie : dossier=" + it.Folder + " (" + it.Folder.Length + " car.) temp=" + it.WorkDir + " (" + it.WorkDir.Length + " car.) nom=" + (it.Name ?? "") + " (" + (it.Name ?? "").Length + " car.)\n" + trace);
        if (rc != 0) {
            if (blocked) { App.Log("yt-dlp refuse par le filtre : " + it.Error); throw new Exception(Tr.T("e_blocked")); }
            if (tlsFail) throw new Exception(Tr.T("e_tls"));
            if (it.Error != null && it.Error.Contains("DRM")) throw new Exception(Tr.T("e_drm"));
            if (it.Error != null && it.Error.Contains("Requested format is not available")) throw new Exception(Tr.T("e_noFormat"));
            throw new Exception(it.Error ?? ("yt-dlp rc=" + rc));
        }
        if (final != null && File.Exists(final)) { it.Folder = Path.GetDirectoryName(final); it.Name = Path.GetFileName(final); it.Total = it.Received = new FileInfo(final).Length; }
        // titre du site souvent enorme : nom raccourci (meme regle que les autres telechargements)
        if (final != null && File.Exists(it.FilePath)) {
            string shortN = Fmt.SafeName(Fmt.ShortFileName(it.Name));
            if (shortN != it.Name) {
                string old = it.FilePath, oldName = it.Name;
                it.Name = shortN;
                Mgr.Unique(it, false);
                try { File.Move(old, it.FilePath); } catch (System.Exception e) { App.Log("renommage " + old + " : " + e.Message); it.Name = oldName; }
            }
        }
        try { Directory.Delete(it.WorkDir, true); } catch { }
    }

    // YouTube : peut-on telecharger DIRECTEMENT (yt-dlp) ? Sans filtre, oui ; derriere un filtre qui renvoie l'appel
    // de yt-dlp ailleurs (Techloq : youtubei/v1/player -> filter.techloq.com), non -> l'extension passe a la capture du
    // lecteur. Simulation seulement (rien n'est telecharge), 10 s au plus. Le resultat est garde 10 minutes.
    static readonly Dictionary<string, Tuple<bool, DateTime>> probeCache = new Dictionary<string, Tuple<bool, DateTime>>();
    public static bool Probe(string url) {
        string yt = Tools.Ytdlp;
        if (yt == null || string.IsNullOrEmpty(url)) return false;
        string key; try { key = new Uri(url).Host; } catch { return false; }
        // refus garde par SITE (un filtre bloque tout le site), reussite par VIDEO (une video peut etre restreinte seule)
        lock (probeCache) {
            Tuple<bool, DateTime> c;
            // refus garde 1 h (avant : 10 min) : sous un filtre comme Techloq chaque essai ajoutait 3 a 5 s au clic (mesure
            // 21/10, FOCH4) ; pas davantage, un refus peut aussi venir d'une panne passagere (revue Codex)
            if (probeCache.TryGetValue(key, out c) && (DateTime.Now - c.Item2).TotalMinutes < 60) return false;
            if (probeCache.TryGetValue(url, out c) && (DateTime.Now - c.Item2).TotalMinutes < 10) return true;
        }
        var a = new List<string> { "--simulate", "--no-playlist", "--no-warnings", "--compat-options", "no-certifi", "--encoding", "utf-8", "--socket-timeout", "8" };
        string deno = Tools.Find("deno.exe", null);
        if (deno != null) a.AddRange(new[] { "--js-runtimes", Hls.Q("deno:" + deno) });
        a.AddRange(new[] { "-f", Hls.Q("bv*+ba/b"), "--", Hls.Q(url) });
        bool ok = false, tlsFail = false;
        Action<string> onLine = l => { if (TlsTrust.IsCertError(l)) tlsFail = true; };
        using (var cts = new CancellationTokenSource(10000)) {
            try { ok = Proc.Run(null, yt, string.Join(" ", a), onLine, cts.Token, false, TlsTrust.Env()) == 0 && !cts.IsCancellationRequested; } catch { ok = false; }
        }
        if (!ok && tlsFail && TlsTrust.Learn(url))
            using (var cts = new CancellationTokenSource(10000)) {
                try { ok = Proc.Run(null, yt, string.Join(" ", a), null, cts.Token, false, TlsTrust.Env()) == 0 && !cts.IsCancellationRequested; } catch { ok = false; }
            }
        App.Log("ytprobe " + key + " : " + (ok ? "direct possible" : "direct impossible -> capture"));
        lock (probeCache) probeCache[ok ? url : key] = Tuple.Create(ok, DateTime.Now);
        return ok;
    }

    // Fichier cookies au format Netscape (lu par yt-dlp), efface juste apres.
    static string WriteCookies(Item it) {
        if (it.Cookies == null || it.Cookies.Count == 0) return null;
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in it.Cookies) {
            string dom = Json.Str(c, "domain") ?? ""; if (dom.Length == 0) continue;
            bool hostOnly = Json.Bool(c, "hostOnly");
            string d = hostOnly ? dom.TrimStart('.') : (dom.StartsWith(".") ? dom : "." + dom);
            double exp; double.TryParse(Json.Str(c, "expirationDate") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out exp);
            sb.Append(d).Append('\t').Append(hostOnly ? "FALSE" : "TRUE").Append('\t').Append(Json.Str(c, "path") ?? "/").Append('\t')
              .Append(Json.Bool(c, "secure") ? "TRUE" : "FALSE").Append('\t').Append(((long)exp).ToString()).Append('\t')
              .Append(Json.Str(c, "name")).Append('\t').Append(Json.Str(c, "value")).Append('\n');
        }
        string f = Path.Combine(it.WorkDir, "cookies.txt");
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
        return f;
    }
}

// Conversion audio (fichier ou HLS demande en « audio seulement »).
static class Convert2 {
    // Filtres audio choisis dans les reglages : volume regulier (loudnorm) et vitesse (atempo, sans changer la hauteur).
    public static string Filters() {
        var f = new List<string>();
        if (Settings.Current.Loudnorm) f.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        double sp;
        if (double.TryParse(Settings.Current.AudioSpeed ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out sp) && sp > 1.01 && sp <= 2.0)
            f.Add("atempo=" + sp.ToString("0.##", CultureInfo.InvariantCulture));
        return f.Count == 0 ? "" : " -af " + Hls.Q(string.Join(",", f));
    }

    public static void Audio(Item it, CancellationToken ct) {
        string ext = Path.GetExtension(it.Name).ToLowerInvariant();
        string fmt = it.AudioFormat == "m4a" ? "m4a" : "mp3";
        if (it.AudioFormat == "original" && Filters().Length == 0) return;   // « tel quel » : le fichier reste ce qu'il est
        if (ext == "." + fmt && Filters().Length == 0) return;
        string ff = Tools.Ffmpeg; if (ff == null) throw new Exception(Tr.T("e_noFf"));
        it.Status = "merging";
        // Le nom final (.mp3) n'est publie qu'APRES une conversion reussie : conversion interrompue = la source garde son
        // nom de video, et la reprise recommence la conversion (avant : une video declaree finie sous un nom .mp3,
        // revue Codex n° 7). La conversion ecrit dans le dossier de travail, puis le resultat est deplace.
        string src = it.FilePath;
        Directory.CreateDirectory(it.WorkDir);
        string tmp = Path.Combine(it.WorkDir, "audio-out." + fmt);
        string codec = fmt == "mp3" ? "-c:a libmp3lame -q:a 2 -compression_level 5" : "-c:a aac -b:a 192k";
        string cover2; string tags2 = AudioTags.Args(it, out cover2);
        string ci2 = cover2 != null ? " -i " + Hls.Q(cover2) : "", mp2 = cover2 != null ? " -map 0:a -map 1:v -c:v copy" + AudioTags.CoverFlags(fmt) : " -vn";
        if (Proc.Run(it, ff, "-y -hide_banner -loglevel error -i " + Hls.Q(src) + ci2 + mp2 + Filters() + " " + codec + tags2 + " " + Hls.Q(tmp), null, ct) != 0 || !File.Exists(tmp))
            throw new Exception("ffmpeg audio conversion failed");
        ct.ThrowIfCancellationRequested();
        string srcName = it.Name;
        it.Name = Path.ChangeExtension(it.Name, "." + fmt);
        if (!string.Equals(it.Name, srcName, StringComparison.OrdinalIgnoreCase) || !File.Exists(src)) Mgr.Unique(it, false);
        else { File.Delete(src); }   // meme format (filtres seulement) : le resultat remplace la source
        File.Move(tmp, it.FilePath);
        if (!string.Equals(src, it.FilePath, StringComparison.OrdinalIgnoreCase)) try { File.Delete(src); } catch { }
        it.Total = it.Received = new FileInfo(it.FilePath).Length;
    }
}

// Capture navigateur : l'extension envoie les pistes que Chrome a DEJA recues pour la lecture
// (YouTube lit en SABR, que yt-dlp ne sait pas rejouer, et un filtre comme Techloq bloque l'API
// qu'utilisent les outils). On ne telecharge donc que ce que le navigateur avait le droit de lire ;
// le programme recoit les morceaux puis assemble avec ffmpeg, sans reencoder la video.
static class Capture {
    // Disque trop plein pour une capture : l'element apparait en erreur dans le programme (l'extension ne sait pas dire « disque »)
    public static Item Refuse(Dictionary<string, object> m, string why) {
        var it = new Item {
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Url = Json.Str(m, "pageUrl") ?? "", Kind = "capture", Title = Json.Str(m, "title"), AudioOnly = Json.Bool(m, "audioOnly"),
            Thumb = Json.Str(m, "thumb"), Status = "error", Error = why, Finished = DateTime.Now
        };
        long total; long.TryParse(Json.Str(m, "total") ?? "0", out total); it.Total = total;
        it.Name = Fmt.SafeName(string.IsNullOrEmpty(it.Title) ? "video" : Fmt.ShortTitle(it.Title)) + (it.AudioOnly ? ".mp3" : ".mp4");
        it.Folder = Sites.FolderFor(it.Url) ?? Mgr.DefaultFolder(it);
        Mgr.Insert(it);
        return it;
    }
    public static Item Begin(Dictionary<string, object> m) {
        var old = Resumable(Json.Str(m, "key"), Json.Bool(m, "audioOnly"));
        if (old != null) {
            // reprise (0.7.12) : meme element, memes morceaux sur le disque ; l'extension ne demande que ce qui manque
            long t; long.TryParse(Json.Str(m, "total") ?? "0", out t);
            lock (old) {
                old.Status = "downloading"; old.Error = null; old.Speed = 0; old.LastSeen = DateTime.Now;
                if (t > 0) old.Total = t;
                old.Received = Have(old, null);
            }
            App.Log("capture reprise : " + old.Url + " (" + old.Received + " octets deja recus)");
            Mgr.Save();
            return old;
        }
        var it = new Item {
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Url = Json.Str(m, "pageUrl") ?? "", Kind = "capture", Title = Json.Str(m, "title"),
            Quality = Json.Str(m, "quality"), AudioOnly = Json.Bool(m, "audioOnly"),
            AudioFormat = Settings.Current.AudioFormat == "original" ? "original" : (Json.Str(m, "audioFormat") ?? Settings.Current.AudioFormat),
            Thumb = Json.Str(m, "thumb"),
            Status = "downloading"
        };
        long total; long.TryParse(Json.Str(m, "total") ?? "0", out total);
        it.Total = total;
        it.CapKey = Json.Str(m, "key");
        it.Name = Fmt.SafeName(string.IsNullOrEmpty(it.Title) ? "video" : Fmt.ShortTitle(it.Title)) + (it.AudioOnly ? "." + (it.AudioFormat == "original" ? "opus" : it.AudioFormat == "m4a" ? "m4a" : "mp3") : ".mp4");
        it.Folder = Sites.FolderFor(it.Url) ?? Mgr.DefaultFolder(it);
        // video d'une playlist demandee par le programme : dossier au nom de la playlist, nom « 01 - Titre »
        var pl = Playlists.PlanFor(it.CapKey);
        if (pl != null) { it.Folder = pl[0]; it.Name = pl[1] + it.Name; }
        Mgr.Unique(it, false);
        Directory.CreateDirectory(it.WorkDir);
        it.LastSeen = DateTime.Now;
        Mgr.Insert(it);
        return it;
    }

    // Capture coupee (page rechargee, onglet ferme, Chrome ou programme arrete) de la MEME video et du meme type
    // (MP3 ou video) dont les morceaux sont encore sur le disque : on la reprend au lieu de tout recommencer.
    // « En cours » compte aussi si plus rien n'arrive depuis 15 s (page rechargee avant que l'abandon ne soit vu).
    public static Item Resumable(string key, bool audioOnly) {
        if (string.IsNullOrEmpty(key)) return null;
        lock (Mgr.Items) return Mgr.Items.Where(i => i.Kind == "capture" && i.CapKey == key && i.AudioOnly == audioOnly && !i.Removed
            && (i.Status == "error" || (i.Status == "downloading" && (DateTime.Now - i.LastSeen).TotalSeconds > 15))
            && Directory.Exists(i.WorkDir)).OrderByDescending(i => i.Added).FirstOrDefault();
    }
    // Morceaux deja sur le disque, par piste : { "itag": { init: octets (0 = absent), segs: { "seq": octets } } } ; rend le total d'octets.
    public static long Have(Item it, Dictionary<string, object> outMap) {
        long n = 0;
        if (!Directory.Exists(it.WorkDir)) return 0;
        foreach (var dir in Directory.GetDirectories(it.WorkDir, "i*")) {
            int itag; if (!int.TryParse(Path.GetFileName(dir).Substring(1), out itag)) continue;
            var segs = new Dictionary<string, object>(); long init = 0;
            foreach (var f in Directory.GetFiles(dir, "*.bin")) {
                long len = new FileInfo(f).Length; n += len;
                string b = Path.GetFileNameWithoutExtension(f);
                if (b == "init") init = len;
                else { long q; if (long.TryParse(b, out q)) segs[q.ToString()] = len; }
            }
            if (outMap != null) outMap[itag.ToString()] = new Dictionary<string, object> { { "init", init }, { "segs", segs } };
        }
        return n;
    }

    // Capture abandonnee (onglet ferme, extension arretee, envoi en echec) : l'element ne doit pas rester « en cours »
    // pour toujours et bloquer la file (revue Codex n° 9). L'extension l'annule (capture_abort) ; sinon, sans aucun
    // morceau recu depuis 3 minutes, Mgr.Tick l'arrete.
    public const int StaleSeconds = 180;
    public static void Abort(Item it, string why) {
        lock (it) { if (it.Kind != "capture" || it.Status != "downloading") return; it.Status = "error"; it.Error = why; it.Speed = 0; }
        App.Log("capture interrompue (" + why + ") : " + it.Url);
        Mgr.Save();
        Mgr.Pump();
    }

    static string Track(string t) {
        var sb = new StringBuilder();
        foreach (char c in (t ?? "").ToLowerInvariant()) if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.') sb.Append(c);
        if (sb.Length == 0 || sb.ToString().Contains("..")) throw new Exception("bad track");
        return sb.ToString();
    }

    public static void Chunk(Item it, string track, long offset, string b64) {
        byte[] data = Convert.FromBase64String(b64);
        using (var fs = new FileStream(Path.Combine(it.WorkDir, Track(track)), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(data, 0, data.Length);
        }
        lock (it) { it.Received += data.Length; it.LastSeen = DateTime.Now; }
    }

    // Morceau d'une piste (capture en flux) : un fichier par morceau, assembles dans l'ordre a la fin.
    public static void Segment(Item it, int itag, bool init, long seq, string b64) {
        byte[] data = Convert.FromBase64String(b64);
        string dir = Path.Combine(it.WorkDir, "i" + itag);
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, init ? "init.bin" : seq.ToString("D8") + ".bin");
        if (File.Exists(f)) return;   // morceau deja recu
        File.WriteAllBytes(f + ".tmp", data);
        File.Move(f + ".tmp", f);
        lock (it) { it.Received += data.Length; it.LastSeen = DateTime.Now; }
    }

    static string Join(Item it, string spec, string kind) {
        if (string.IsNullOrEmpty(spec)) return null;
        string[] p = spec.Split(':');
        int itag; if (!int.TryParse(p[0], out itag)) throw new Exception("bad itag");
        string ext = p.Length > 1 && (p[1] == "webm" || p[1] == "mp4" || p[1] == "m4a") ? p[1] : "mp4";
        string dir = Path.Combine(it.WorkDir, "i" + itag);
        string init = Path.Combine(dir, "init.bin");
        if (!File.Exists(init)) throw new Exception("init manquant (" + kind + ")");
        string outFile = Path.Combine(it.WorkDir, kind + "." + ext);
        using (var o = new FileStream(outFile, FileMode.Create, FileAccess.Write)) {
            using (var s = File.OpenRead(init)) s.CopyTo(o);
            foreach (var f in Directory.GetFiles(dir, "*.bin").Where(x => Path.GetFileName(x) != "init.bin").OrderBy(x => x, StringComparer.Ordinal))
                using (var s = File.OpenRead(f)) s.CopyTo(o);
        }
        return outFile;
    }

    public static void End(Item it) {
        it.Status = "queued";
        Mgr.Save();
        Mgr.Pump();
    }

    // duree d'un fichier en secondes (« ffmpeg -i » la donne sur sa sortie d'erreur), 0 si inconnue
    static double ProbeSeconds(string ff, string file) {
        try {
            var psi = new ProcessStartInfo(ff, "-hide_banner -i " + Hls.Q(file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi)) {
                string e = p.StandardError.ReadToEnd(); p.WaitForExit(15000);
                var m = Regex.Match(e, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (m.Success) return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        } catch (System.Exception) { }
        return 0;
    }

    public static void Mux(Item it, CancellationToken ct) {
        string ff = Tools.Ffmpeg;
        if (ff == null) throw new Exception(Tr.T("e_noFf"));
        it.Status = "merging";
        if (!string.IsNullOrEmpty(it.CapAudio) || !string.IsNullOrEmpty(it.CapVideo)) {   // capture en flux : assembler les morceaux des pistes retenues
            Join(it, it.CapAudio, "audio");
            if (!it.AudioOnly || string.IsNullOrEmpty(it.CapAudio)) Join(it, it.CapVideo, "video");   // flux HLS d'une seule piste : video seule
        }
        string v = Directory.GetFiles(it.WorkDir, "video.*").FirstOrDefault();
        string a = Directory.GetFiles(it.WorkDir, "audio.*").FirstOrDefault();
        Directory.CreateDirectory(it.Folder);
        string q = "-y -hide_banner -loglevel error ";
        if (it.AudioOnly) {
            string src = a ?? v;
            if (src == null) throw new Exception("no track");
            string cover; string tags = AudioTags.Args(it, out cover);
            if (it.AudioFormat == "original" && Convert2.Filters().Length == 0) {
                // piste recopiee sans conversion : Opus (.webm) => .opus, AAC (.mp4/.m4a) => .m4a ; quelques secondes au plus
                string sx = Path.GetExtension(src).ToLowerInvariant();
                string ox = sx == ".webm" || sx == ".opus" || sx == ".ogg" ? ".opus" : sx == ".mp3" ? ".mp3" : ".m4a";
                it.Name = Path.ChangeExtension(it.Name, ox); Mgr.Unique(it, false);
                if (Proc.Run(it, ff, q + "-i " + Hls.Q(src) + " -vn -c:a copy" + tags + " " + Hls.Q(it.FilePath), null, ct) != 0) throw new Exception("ffmpeg copy failed");
                it.Error = null;
                try { Directory.Delete(it.WorkDir, true); } catch { }
                return;
            }
            string codec = it.AudioFormat == "m4a" ? "-c:a aac -b:a 192k" : "-c:a libmp3lame -q:a 2 -compression_level 5";
            // La conversion en MP3/M4A d'un long morceau prend des minutes (2 h 20 d'Opus => ~5 min) : on affiche son avancement
            // (duree du morceau lue d'abord, puis « -progress ») au lieu de « … » sans fin (ShowF 21/10).
            double total = ProbeSeconds(ff, src);
            it.MergePct = 0;
            Action<string> prog = line => {
                if (total <= 0 || !line.StartsWith("out_time_us=")) return;
                if (it.MergeStart == default(DateTime)) it.MergeStart = DateTime.UtcNow;
                long us; if (long.TryParse(line.Substring(12), out us) && us > 0) it.MergePct = (int)Math.Max(1, Math.Min(99, us / 1e6 * 100.0 / total));
            };
            try {
                // balises (0.7.30) : titre, adresse d'origine et pochette (vignette en cache) ecrits dans le fichier, lus par le telephone et la voiture
                string ci = cover != null ? " -i " + Hls.Q(cover) : "", mp = cover != null ? " -map 0:a -map 1:v -c:v copy" + AudioTags.CoverFlags(it.AudioFormat) : " -vn";
                if (Proc.Run(it, ff, q + "-progress pipe:1 -nostats -i " + Hls.Q(src) + ci + mp + Convert2.Filters() + " " + codec + tags + " " + Hls.Q(it.FilePath), prog, ct) != 0) throw new Exception("ffmpeg audio failed");
            } finally { it.MergePct = 0; it.MergeStart = default(DateTime); }
        } else {
            if (v == null) throw new Exception("no video track");
            string inputs = "-i " + Hls.Q(v) + (a != null ? " -i " + Hls.Q(a) + " -map 0:v:0 -map 1:a:0" : "");
            if (Proc.Run(it, ff, q + inputs + " -c copy -movflags +faststart " + Hls.Q(it.FilePath), null, ct) != 0) {
                // codecs que le MP4 refuse : MKV, toujours sans reencoder
                try { if (File.Exists(it.FilePath)) File.Delete(it.FilePath); } catch { }
                it.Name = Path.ChangeExtension(it.Name, ".mkv");
                Mgr.Unique(it, false);
                if (Proc.Run(it, ff, q + inputs + " -c copy " + Hls.Q(it.FilePath), null, ct) != 0) throw new Exception("ffmpeg mux failed");
            }
        }
        it.Error = null;
        try { Directory.Delete(it.WorkDir, true); } catch { }
    }
}

// Extrait d'un fichier deja telecharge : Url = chemin source, Quality = "debut-fin" en secondes.
static class Clip {
    public static Item Create(Item src, double from, double to) {
        var it = new Item {
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Url = src.FilePath, Kind = "clip", Title = src.Title, Folder = src.Folder,
            Quality = from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture),
            Name = Path.GetFileNameWithoutExtension(src.Name) + " (" + Tr.T("clipName") + " " + Lbl(from) + "-" + Lbl(to) + ")" + Path.GetExtension(src.Name)
        };
        Mgr.Unique(it, false);
        Mgr.Insert(it);
        Mgr.Pump();
        return it;
    }
    static string Lbl(double sec) {
        var t = TimeSpan.FromSeconds(Math.Floor(sec));
        return (t.TotalHours >= 1 ? ((int)t.TotalHours) + "h" : "") + t.Minutes.ToString("00") + "m" + t.Seconds.ToString("00");
    }
    public static void Run(Item it, CancellationToken ct) {
        string ff = Tools.Ffmpeg;
        if (ff == null) throw new Exception(Tr.T("e_noFf"));
        if (!File.Exists(it.Url)) throw new Exception("source introuvable");
        string[] p = it.Quality.Split('-');
        double a = double.Parse(p[0], CultureInfo.InvariantCulture), b = double.Parse(p[1], CultureInfo.InvariantCulture);
        it.Status = "merging";
        string ext = Path.GetExtension(it.Url).ToLowerInvariant();
        bool audio = ext == ".mp3" || ext == ".m4a" || ext == ".aac" || ext == ".opus" || ext == ".ogg" || ext == ".wav" || ext == ".flac";
        string ss = a.ToString("0.###", CultureInfo.InvariantCulture), dur = (b - a).ToString("0.###", CultureInfo.InvariantCulture);
        // coupe precise a la seconde : on reencode le passage (court) ; une copie sans reencodage part de
        // l'image cle precedente (mesure : 2 s-5 s demandes, 5,1 s obtenues)
        string args = audio
            ? "-y -hide_banner -loglevel error -ss " + ss + " -i " + Hls.Q(it.Url) + " -t " + dur + " " + Hls.Q(it.FilePath)
            : "-y -hide_banner -loglevel error -ss " + ss + " -i " + Hls.Q(it.Url) + " -t " + dur + " -c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 160k -movflags +faststart " + Hls.Q(it.FilePath);
        if (Proc.Run(it, ff, args, null, ct) != 0) throw new Exception("ffmpeg clip failed");
    }
}

// Corbeille de Windows : envoi (Microsoft.VisualBasic, jamais de boite de confirmation) et retour (Annuler).
static class Recycle {
    public static void Send(string path) {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    // Chemin d'origine lu dans le fichier $I de la corbeille (v1 : 520 octets fixes ; v2 : longueur + chemin).
    // Le nom affiche par l'Explorateur et ses colonnes dependent de la langue : on ne s'y fie pas.
    static string Origin(string iFile) {
        try {
            byte[] b = File.ReadAllBytes(iFile);
            if (b.Length < 28) return null;
            long ver = BitConverter.ToInt64(b, 0);
            string s;
            if (ver >= 2) { int n = BitConverter.ToInt32(b, 24); s = Encoding.Unicode.GetString(b, 28, Math.Min(Math.Max(0, n) * 2, b.Length - 28)); }
            else s = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            return s.TrimEnd('\0');
        } catch { return null; }
    }

    // `restored` : chemin reellement retabli. Un autre fichier occupe deja la place : l'original revient a cote,
    // sous un autre nom, et les deux sont gardes (revue Codex n° 20).
    public static bool Restore(string path, out string restored) {
        restored = null;
        try {
            string full = Path.GetFullPath(path);
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string dir = Path.Combine(Path.GetPathRoot(full), "$Recycle.Bin", sid);
            if (!Directory.Exists(dir)) return false;
            foreach (var f in new DirectoryInfo(dir).GetFiles("$I*").OrderByDescending(x => x.LastWriteTimeUtc)) {
                if (!string.Equals(Origin(f.FullName), full, StringComparison.OrdinalIgnoreCase)) continue;
                string r = Path.Combine(dir, "$R" + f.Name.Substring(2));
                if (!File.Exists(r)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                string target = full;
                for (int i = 2; File.Exists(target) && i < 1000; i++)
                    target = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + " (" + i + ")" + Path.GetExtension(full));
                File.Move(r, target);
                try { File.Delete(f.FullName); } catch { }
                restored = target;
                return true;
            }
        } catch (System.Exception e) { App.Log("corbeille : " + e.Message); }
        return false;
    }
}

// File d'attente : persistance, ordonnancement, actions.
static class Mgr {
    public static readonly List<Item> Items = new List<Item>();
    public static event Action<Item> Finished;
    // File retenue jusqu'a l'heure programmee. L'echeance est calculee au moment ou la file est retenue (prochaine
    // occurrence de l'heure) : un PC reveille APRES l'heure part quand meme (revue Codex n° 18 : la fenetre d'une
    // minute etait manquee si le PC dormait).
    static bool held; static DateTime heldSince;
    public static bool Held { get { return held; } set { held = value; if (value) heldSince = DateTime.Now; } }
    static bool hadActivity;
    static string FilePath { get { return Path.Combine(App.Dir, "downloads.json"); } }
    static readonly object SaveLock = new object();

    public static void Load() {
        try {
            // arret brutal pendant une ecriture : le .tmp complet sert de liste
            string src = File.Exists(FilePath) ? FilePath : File.Exists(FilePath + ".tmp") ? FilePath + ".tmp" : null;
            if (src != null) {
                var l = Json.De<List<Item>>(File.ReadAllText(src, Encoding.UTF8));
                if (l != null) foreach (var it in l) {
                    // capture coupee par un arret du programme : les morceaux ne sont pas complets, rien a assembler
                    if (it.Kind == "capture" && it.Status == "downloading") { it.Status = "error"; it.Error = Tr.T("e_capStopped"); }
                    else if (it.Status == "downloading" || it.Status == "merging") it.Status = "queued";
                    // historique : plus aucun secret de connexion pour un element termine (revue Codex n° 2)
                    if (it.Status == "done") it.Cookies = new List<Dictionary<string, object>>();
                    // JavaScriptSerializer relit les dates en UTC
                    if (it.Added.Kind == DateTimeKind.Utc) it.Added = it.Added.ToLocalTime();
                    if (it.Finished.Kind == DateTimeKind.Utc) it.Finished = it.Finished.ToLocalTime();
                    Items.Add(it);
                }
            }
        } catch (Exception e) { App.Log("downloads.json illisible : " + e.Message); }
        // fichier de cookies pour yt-dlp reste apres un arret brutal : jamais garde (revue Codex n° 2)
        try { if (Directory.Exists(App.TempDir)) foreach (var f in Directory.GetFiles(App.TempDir, "cookies.txt", SearchOption.AllDirectories)) File.Delete(f); } catch { }
        Held = Settings.Current.ScheduleOn;
    }

    public static void Save() {
        lock (SaveLock) {
            try {
                string s;
                lock (Items) {
                    var copy = Items.Select(i => { lock (i) return Json.Ser(i); }).ToList();
                    s = "[" + string.Join(",", copy) + "]";
                }
                Directory.CreateDirectory(App.Dir);
                File.WriteAllText(FilePath + ".tmp", s, new UTF8Encoding(false));
                Fmt.ReplaceFile(FilePath + ".tmp", FilePath);
            } catch (Exception e) { App.Log("save : " + e.Message); }
        }
    }

    static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi", ".flv", ".ts", ".3gp" };
    static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".wma" };

    public static string GuessKind(string url) {
        string u = url.ToLowerInvariant().Split('?', '#')[0];
        if (u.EndsWith(".m3u8")) return "hls";
        if (u.EndsWith(".mpd")) return "dash";
        string ext = Path.GetExtension(u);
        if (ext.Length > 1 && ext.Length <= 6 && !new[] { ".html", ".htm", ".php", ".aspx", ".asp", ".jsp" }.Contains(ext)) return "file";
        return "page";
    }

    public static Item Add(Dictionary<string, object> m) {
        var s = Settings.Current;
        var it = new Item {
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Url = Json.Str(m, "url"),
            Kind = Json.Str(m, "kind"),
            Title = Json.Str(m, "title"),
            Referer = Json.Str(m, "referer"),
            UserAgent = Json.Str(m, "userAgent"),
            Quality = Json.Str(m, "quality") ?? s.Quality,
            AudioOnly = Json.Bool(m, "audioOnly"),
            AudioFormat = s.AudioFormat == "original" ? "original" : (Json.Str(m, "audioFormat") ?? s.AudioFormat),
            VariantUrl = Json.Str(m, "variantUrl"),
            Thumb = Json.Str(m, "thumb"),
            Cookies = Json.List(m, "cookies")
        };
        if (string.IsNullOrEmpty(it.Url) || !(it.Url.StartsWith("http://") || it.Url.StartsWith("https://"))) throw new Exception("bad url");
        if (string.IsNullOrEmpty(it.Kind) || it.Kind == "auto") it.Kind = GuessKind(it.Url);
        if (!Json.Bool(m, "force")) {
            Item dup;
            // le format audio fait partie du resultat demande : MP3 puis M4A de la meme source = deux fichiers (revue Codex n° 16)
            lock (Items) dup = Items.FirstOrDefault(o => o.Status == "done" && o.Url == it.Url && o.AudioOnly == it.AudioOnly
                && (!it.AudioOnly || (o.AudioFormat ?? "mp3") == (it.AudioFormat ?? "mp3"))
                && (o.VariantUrl ?? "") == (it.VariantUrl ?? "") && (o.Quality ?? "") == (it.Quality ?? "") && File.Exists(o.FilePath));
            if (dup != null) { dup.Error = null; throw new DuplicateException(dup); }
        }
        string fn = Json.Str(m, "filename");
        string urlName = "";
        try { urlName = Uri.UnescapeDataString(Path.GetFileName(new Uri(it.Url).AbsolutePath)); } catch { }
        string ext = Path.GetExtension(urlName).ToLowerInvariant();
        if (!string.IsNullOrEmpty(fn)) it.Name = Fmt.SafeName(Fmt.ShortFileName(fn));
        else if (it.Kind == "file") {
            // un titre d'onglet est plus parlant que « video.mp4 » ou « 8f3a...mp4 »
            bool generic = urlName.Length == 0 || Path.GetFileNameWithoutExtension(urlName).Length < 4 || Regex.IsMatch(urlName, "^[0-9a-f_-]{16,}\\.", RegexOptions.IgnoreCase);
            if (generic && !string.IsNullOrEmpty(it.Title) && (VideoExt.Contains(ext) || AudioExt.Contains(ext))) it.Name = Fmt.SafeName(Fmt.ShortTitle(it.Title)) + ext;
            else it.Name = "~" + Fmt.SafeName(urlName.Length > 0 ? Fmt.ShortFileName(urlName) : Fmt.ShortTitle(it.Title ?? "download"));
        } else it.Name = Fmt.SafeName(Fmt.ShortTitle(!string.IsNullOrEmpty(it.Title) ? it.Title : Path.GetFileNameWithoutExtension(urlName))) + (it.Kind == "hls" || it.Kind == "dash" ? ".mp4" : "");

        string folder = Json.Str(m, "folder");
        // dossier du site de la page (pas celui de l'hebergeur video, souvent dans un cadre)
        string siteFolder = Sites.FolderFor(Json.Str(m, "pageUrl") ?? it.Referer ?? it.Url);
        it.Folder = !string.IsNullOrEmpty(folder) ? folder : siteFolder ?? DefaultFolder(it);
        if (!it.Name.StartsWith("~")) Unique(it, false);
        lock (Items) Items.Insert(0, it);
        Save();
        Pump();
        return it;
    }

    public static string DefaultFolder(Item it) {
        var s = Settings.Current;
        if (!s.SortByType) return s.Folder;
        string e2 = Path.GetExtension((it.Name ?? "").TrimStart('~')).ToLowerInvariant();
        string sub = it.AudioOnly || AudioExt.Contains(e2) ? Tr.T("dirAudio")
            : it.Kind != "file" || VideoExt.Contains(e2) ? Tr.T("dirVideo") : Tr.T("dirFiles");
        return Path.Combine(s.Folder, sub);
    }

    public static void Insert(Item it) {
        lock (Items) Items.Insert(0, it);
        Save();
    }

    // Nom libre : « fichier (2).mp4 » si le nom est deja pris (ou en cours ailleurs dans la file).
    public static void Unique(Item it, bool skipPending) {
        if (skipPending && it.Name.StartsWith("~")) return;
        string n = it.Name.TrimStart('~');
        if (it.Name.StartsWith("~")) return;
        string bas = Path.GetFileNameWithoutExtension(n), ext = Path.GetExtension(n);
        string own = it.FilePath;
        for (int i = 2; i < 1000; i++) {
            string full = Path.Combine(it.Folder, n);
            // un .kpart a notre propre nom est notre reprise, pas un conflit
            bool taken = File.Exists(full) || (File.Exists(full + ".kpart") && !string.Equals(full, own, StringComparison.OrdinalIgnoreCase));
            if (!taken) lock (Items) taken = Items.Any(o => o != it && o.Status != "done" && string.Equals(o.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (!taken) break;
            n = bas + " (" + i + ")" + ext;
        }
        it.Name = n;
    }

    // Elements en attente dans l'ordre de depart : priorite la plus haute d'abord, a egalite le plus ancien (Items est du plus recent au plus ancien).
    public static List<Item> QueueOrder(IEnumerable<Item> items) {
        return items.Where(i => i.Status == "queued").Reverse().OrderByDescending(i => i.Prio).ToList();
    }
    // « Telecharger ensuite » : passe devant les autres en attente.
    public static void StartNext(Item it) {
        if (it.Status != "queued") return;
        lock (Items) it.Prio = Items.Max(i => i.Prio) + 1;
        Save(); Pump();
    }
    // « Commencer maintenant » : part tout de suite, meme si le nombre de telechargements en meme temps est atteint.
    public static void StartNow(Item it) {
        if (it.Status != "queued") return;
        lock (Items) { it.Prio = Items.Max(i => i.Prio) + 1; Start(it); }
        Save();
    }
    // Place libre sur le disque : null = assez ; sinon le texte technique de l'erreur (le code « disk » le traduit en phrase claire).
    // Marge de 150 Mo ; assemblage (video + audio, conversion) = jusqu'a 2,2 fois la taille ; taille inconnue : 500 Mo.
    public static string DiskShort(string folder, long remaining, double factor) {
        long need = remaining > 0 ? (long)(remaining * factor) + 150L * 1048576 : 500L * 1048576;
        foreach (string dir in new[] { folder, factor > 1.0 ? App.TempDir : null }) {
            if (string.IsNullOrEmpty(dir)) continue;
            try {
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace;
                if (free < need) return "disk full: " + Fmt.Size(need) + " needed, " + Fmt.Size(free) + " free on " + Path.GetPathRoot(Path.GetFullPath(dir));
            } catch (System.Exception) { /* lecteur reseau ou introuvable : on ne bloque pas */ }
        }
        return null;
    }

    public static void Pump() {
        if (Held) {
            DateTime at;
            if (!DateTime.TryParseExact(Settings.Current.ScheduleTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) return;
            DateTime due = heldSince.Date + at.TimeOfDay;
            if (due < heldSince) due = due.AddDays(1);
            if (DateTime.Now >= due) held = false;
            else return;
        }
        int started = 0;
        lock (Items) {
            int running = Items.Count(i => i.Status == "downloading" || i.Status == "merging");
            foreach (var it in QueueOrder(Items)) {
                if (running >= Settings.Current.MaxParallel) break;
                Start(it); running++; started++;
            }
            if (running == 0 && hadActivity && !Items.Any(i => i.Status == "queued")) {
                hadActivity = false;
                if (Settings.Current.ShutdownWhenDone && Items.Any(i => i.Status == "done" && (DateTime.Now - i.Finished).TotalMinutes < 5)) {
                    if (Finished != null) Finished(null);
                    try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                }
            }
        }
        // hors du verrou Items : Save prend SaveLock puis Items (l'ordre inverse interbloquerait)
        if (started > 0) Save();
    }

    static void Start(Item it) {
        string full = DiskShort(it.Folder, it.Total > 0 ? it.Total - it.Received : 0, it.Kind == "file" ? 1.0 : 2.2);
        if (full != null) { it.Status = "error"; it.Error = full; App.Log("disque : " + full + " (" + it.Name + ")"); return; }
        it.Status = "downloading"; it.Error = null; hadActivity = true;
        it.Cts = new CancellationTokenSource();
        var ct = it.Cts.Token;
        // Pause puis reprise rapides : l'ancien travailleur peut encore tourner (lecture reseau bloquee). Le nouveau
        // l'attend avant de toucher aux fichiers, et l'ancien, qui n'a plus le bon numero, n'ecrit plus rien
        // (revue Codex n° 6 : deux travailleurs sur le meme element).
        int gen = ++it.Gen;
        var prev = it.Worker;
        it.Worker = new Thread(() => {
            if (prev != null && prev.IsAlive) prev.Join();
            if (it.Gen != gen || ct.IsCancellationRequested) return;
            try {
                // Premier lancement : les outils s'installent en arriere-plan ; un telechargement qui en a
                // besoin les attend au lieu d'echouer (un fichier direct sans conversion part tout de suite).
                bool plain = it.Kind != "hls" && it.Kind != "dash" && it.Kind != "page" && it.Kind != "capture" && it.Kind != "clip" && !it.AudioOnly;
                while (!plain && ToolsSetup.Busy && !ct.IsCancellationRequested) Thread.Sleep(500);
                if (it.Kind == "hls") Hls.Run(it, ct);
                else if (it.Kind == "dash" || it.Kind == "page") { Ytdlp.Run(it, ct); if (it.AudioOnly) Convert2.Audio(it, ct); }   // volume et vitesse aussi pour yt-dlp (revue Codex n° 15)
                else if (it.Kind == "capture") Capture.Mux(it, ct);
                else if (it.Kind == "clip") Clip.Run(it, ct);
                else {
                    // audio seul repris APRES la fin du telechargement (conversion interrompue) : la source est complete, on ne
                    // la retelecharge pas
                    bool srcReady = it.AudioOnly && it.Total > 0 && !File.Exists(it.PartPath) && File.Exists(it.FilePath) && new FileInfo(it.FilePath).Length == it.Total;
                    if (!srcReady) HttpDl.Run(it, ct);
                    if (it.AudioOnly) Convert2.Audio(it, ct);
                }
                if (it.Kind == "hls" && it.AudioOnly) Convert2.Audio(it, ct);
                if (it.Gen != gen) return;
                if (File.Exists(it.FilePath)) {
                    it.Total = it.Received = new FileInfo(it.FilePath).Length;
                    // provenance Internet, comme un telechargement de Chrome (revue Codex n° 4) ; un extrait est un fichier a nous
                    if (it.Kind != "clip") Fmt.MarkInternet(it.FilePath, it.Url, it.Referer);
                }
                it.Status = "done"; it.Finished = DateTime.Now; it.Segs = null; it.Speed = 0;
                it.Cookies = new List<Dictionary<string, object>>();   // plus aucun secret garde une fois fini (revue Codex n° 2)
                if (Finished != null) Finished(it);
            } catch (Exception e) {
                if (it.Gen != gen) return;   // un lancement plus recent a pris la main : il decide seul du statut
                if (ct.IsCancellationRequested) { if (it.Status != "queued") it.Status = "paused"; }
                else {
                    it.Status = "error";
                    if (string.IsNullOrEmpty(it.Error) || !(e is HttpFail) && !e.Message.StartsWith("yt-dlp")) it.Error = e.Message;
                    App.Log("erreur " + it.Url + " : " + e);
                    if (Finished != null) Finished(it);
                }
                it.Speed = 0;
            }
            if (it.Removed) Cleanup(it, false);   // retire pendant qu'il tournait : nettoyer une fois les fichiers liberes
            else Save();
            Pump();
        });
        it.Worker.IsBackground = true;
        it.Worker.Start();
    }

    public static Item Get(string id) { lock (Items) return Items.FirstOrDefault(i => i.Id == id); }

    public static void Pause(Item it) {
        if (it.Status == "queued") { it.Status = "paused"; if (!it.Removed) Save(); return; }
        if (it.Status != "downloading" && it.Status != "merging") return;
        it.Status = "paused";
        if (it.Cts != null) it.Cts.Cancel();
        var p = it.Proc; if (p != null && it.Kind != "hls") try { Proc.KillTree(p.Id); } catch { }
        if (!it.Removed) Save();
    }
    public static void Resume(Item it) {
        // capture du navigateur pas finie : elle ne se continue QUE depuis la page (le programme n'a ni la page ni le lecteur).
        // Avant : remise en file => assemblage de morceaux incomplets. Maintenant : on rouvre la page, dont le bouton propose
        // « Reprendre (63 %) » et ne demande que ce qui manque.
        if (it.Kind == "capture" && string.IsNullOrEmpty(it.CapAudio)) {
            if (it.Status == "paused") { it.Status = "error"; it.Error = Tr.T("e_capStopped"); Save(); }
            if (!(it.Url ?? "").StartsWith("https://")) return;
            // 0.7.35 : l'extension rouvre la page EN ARRIERE-PLAN et relance seule ; sans elle, l'ancien geste (ouvrir le lien)
            if (Wake.Alive) Wake.Resume(it);
            else try { Process.Start(it.Url); } catch { }
            return;
        }
        if (it.Status == "paused" || it.Status == "error") { it.Status = "queued"; it.Error = null; Save(); Pump(); }
    }

    // Supprimer : fichier termine -> corbeille de Windows (annulable) ; sinon annulation + nettoyage des morceaux.
    // Renvoie true si la suppression peut etre annulee (Undo).
    public static readonly Dictionary<string, Item> Trashed = new Dictionary<string, Item>();
    public static bool Trash(Item it) {
        if (it.Status == "done" && File.Exists(it.FilePath)) {
            Recycle.Send(it.FilePath);
            Remove(it, false);
            lock (Trashed) Trashed[it.Id] = it;
            return true;
        }
        Remove(it, it.Status != "done");
        return false;
    }
    // Annuler une suppression : le fichier revient de la corbeille, la ligne revient a sa place.
    public static bool Undo(string id) {
        Item it;
        lock (Trashed) { if (!Trashed.TryGetValue(id, out it)) return false; }
        string back;
        if (!Recycle.Restore(it.FilePath, out back)) return false;   // plus de « restaure » annonce sans restauration
        it.Name = Path.GetFileName(back);
        lock (Trashed) Trashed.Remove(id);
        it.Removed = false;
        lock (Items) {
            if (!Items.Contains(it)) {
                int idx = Items.FindIndex(o => o.Added < it.Added);
                if (idx < 0) Items.Add(it); else Items.Insert(idx, it);
            }
        }
        Save();
        return true;
    }
    public static void Remove(Item it, bool deleteFile) {
        it.Removed = true;
        Pause(it);
        var w = it.Worker; if (w != null && w.IsAlive) w.Join(3000);
        lock (Items) Items.Remove(it);
        Cleanup(it, deleteFile);
        Save();
    }

    static void Cleanup(Item it, bool deleteFile) {
        try { if (File.Exists(it.PartPath)) File.Delete(it.PartPath); } catch { }
        try { if (Directory.Exists(it.WorkDir)) Directory.Delete(it.WorkDir, true); } catch { }
        if (deleteFile) try { if (File.Exists(it.FilePath)) File.Delete(it.FilePath); } catch { }
    }

    // Vitesse lissee, appelee chaque seconde par l'interface.
    public static void Tick() {
        List<Item> stale;
        lock (Items) stale = Items.Where(i => i.Kind == "capture" && i.Status == "downloading" && i.LastSeen != default(DateTime)
            && (DateTime.Now - i.LastSeen).TotalSeconds > Capture.StaleSeconds).ToList();
        foreach (var it in stale) Capture.Abort(it, Tr.T("e_capStopped"));
        lock (Items) foreach (var it in Items) {
            if (it.Status == "downloading" || it.Status == "merging") {
                long d = it.Received - it.LastBytes;
                it.Speed = it.Speed <= 0 ? Math.Max(0, d) : it.Speed * 0.6 + Math.Max(0, d) * 0.4;
            } else it.Speed = 0;
            it.LastBytes = it.Received;
        }
        if (Held) Pump();
    }

    public static object ListForExtension() {
        // 50 derniers + TOUS ceux qui ne sont pas finis : un telechargement suivi par l'extension ne disparait jamais de la
        // liste parce que 50 plus recents sont arrives (revue Codex : l'extension le croyait supprime)
        // Conversion audio en cours (0.7.34) : l'extension n'affichait que « assemblage… » sans pourcentage ni temps restant. On lui presente la
        // conversion comme un telechargement (total 1000, recu = pourcentage x 10, reste calcule sur la vitesse deja observee) : le bandeau
        // de YouTube montre alors « 42 % » et l'heure de fin, sans changer l'extension (donc sans nouvel examen du Store).
        Func<Item, bool> conv = i => i.Status == "merging" && i.MergePct > 0 && i.MergeStart != default(DateTime);
        Func<Item, long> convEta = i => { double el = (DateTime.UtcNow - i.MergeStart).TotalSeconds; return el > 1 ? (long)(el * (100 - i.MergePct) / i.MergePct) : 0; };
        lock (Items) return Items.Where((i, n) => n < 50 || (i.Status != "done" && i.Status != "error")).Select(i => new Dictionary<string, object> {
            { "id", i.Id }, { "name", i.Name.TrimStart('~') }, { "status", conv(i) ? "downloading" : i.Status }, { "total", conv(i) ? 1000L : i.Total },
            { "received", conv(i) ? (long)i.MergePct * 10 : i.Received }, { "speed", conv(i) ? 0L : (long)i.Speed }, { "eta", conv(i) ? convEta(i) : (long)i.Eta }, { "error", i.Error }, { "kind", i.Kind },
            { "code", i.Status == "error" ? (ErrCode.Of(i.Error) ?? "generic") : null }
        }).ToList();
    }
}

// Mise a jour du programme PAR LUI-MEME, sur demande de l'extension (v0.7.13). Une extension n'a pas le droit de lancer
// un programme ; le programme, lui, peut lancer le nouvel installeur. Garde-fous :
// - adresse FIXE (ext.i-call.me), jamais celle du message ;
// - empreinte SHA-256 attendue donnee par l'extension (ecrite dans son code a la publication) : un fichier different,
//   meme servi par notre site, n'est jamais lance ;
// - jamais pendant un telechargement en cours (l'installeur arrete le programme) ;
// - installeur lance en --quiet --restart : il remplace les fichiers puis relance le programme pres de l'horloge.
static class SelfUpdate {
    public const string Url = "https://ext.i-call.me/downloads/ShowLef-Setup.exe";
    // (0.7.15, revue Codex) chaque installeur est AUSSI publie a une adresse par version : une extension restee sur
    // l'empreinte de N1 retrouve N1 meme apres la publication de N2 (avant : adresse unique => empreinte differente
    // => mise a jour refusee). Fiche de la derniere version pour la verification depuis le programme lui-meme.
    const string Base = "https://ext.i-call.me/downloads/";
    const string LatestUrl = Base + "showlef-latest.json";
    static int running;
    public static void Tls() {
        // Net.DefaultUA est une constante : la lire ne lance PAS le constructeur de Net qui active TLS 1.2/1.3
        try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
        catch (System.Exception) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
    }
    static void Get(string url, string file) {
        var rq = (HttpWebRequest)WebRequest.Create(url);
        rq.UserAgent = Net.DefaultUA; rq.Timeout = 60000; rq.ReadWriteTimeout = 60000;
        using (var rs = (HttpWebResponse)rq.GetResponse()) using (var s = rs.GetResponseStream()) using (var o = File.Create(file)) s.CopyTo(o);
    }
    // Derniere version publiee : { version, sha256 } (fichier genere par le site a partir de son manifeste)
    public static Dictionary<string, object> Latest() {
        Tls();
        var rq = (HttpWebRequest)WebRequest.Create(LatestUrl + "?t=" + DateTime.UtcNow.Ticks);
        rq.UserAgent = Net.DefaultUA; rq.Timeout = 20000;
        using (var rs = (HttpWebResponse)rq.GetResponse()) using (var r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return Json.Obj(r.ReadToEnd());
    }
    public static bool Newer(string v) { return Regex.IsMatch(v ?? "", @"^\d+\.\d+\.\d+$") && Cmp(v, App.Version) > 0; }
    public static int Cmp(string a, string b) {
        var x = a.Split('.'); var y = b.Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
            int p = i < x.Length ? int.Parse(x[i]) : 0, q = i < y.Length ? int.Parse(y[i]) : 0;
            if (p != q) return p < q ? -1 : 1;
        }
        return 0;
    }
    static string Sha(string file) {
        using (var sha = SHA256.Create()) using (var f = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
    public static Dictionary<string, object> Run(string sha256) { return Run(sha256, null); }
    public static Dictionary<string, object> Run(string sha256, string version) {
        var res = new Dictionary<string, object> { { "ok", false } };
        string want = (sha256 ?? "").Trim().ToLowerInvariant();
        if (!Regex.IsMatch(want, "^[0-9a-f]{64}$")) { res["error"] = "bad sha256"; return res; }
        int busyN; lock (Mgr.Items) busyN = Mgr.Items.Count(i => i.Status == "downloading" || i.Status == "merging");
        if (busyN > 0) { res["error"] = "busy"; res["busy"] = busyN; return res; }
        if (Interlocked.Exchange(ref running, 1) == 1) { res["error"] = "running"; return res; }
        string dir = Path.Combine(Path.GetTempPath(), "showlef-update"), file = Path.Combine(dir, "ShowLef-Setup.exe");
        try {
            Directory.CreateDirectory(dir);
            if (File.Exists(file)) File.Delete(file);
            // (mesure 20/10 : sans TLS 1.2 explicite, « Impossible de creer un canal securise SSL/TLS »)
            Tls();
            // d'abord l'adresse de CETTE version (immuable), sinon l'adresse fixe de la derniere
            bool got0 = false;
            if (Regex.IsMatch(version ?? "", @"^\d+\.\d+\.\d+$"))
                try { Get(Base + "ShowLef-Setup-" + version + ".exe", file); got0 = true; }
                catch (WebException e) { App.Log("mise a jour : adresse par version indisponible (" + e.Message + "), adresse fixe"); }
            if (!got0) Get(Url, file);
            string got = Sha(file);
            // mesure 20/10 : le site rend une PAGE HTML (code 200) pour un fichier absent => pas d'erreur reseau, juste
            // une mauvaise empreinte. Adresse par version absente ou fausse => on essaie l'adresse fixe avant de refuser.
            if (got != want && got0) { App.Log("mise a jour : adresse par version sans le bon fichier, adresse fixe"); Get(Url, file); got = Sha(file); }
            if (got != want) {
                App.Log("mise a jour refusee : empreinte " + got + " au lieu de " + want);
                try { File.Delete(file); } catch { }
                res["error"] = "sha256 mismatch"; res["got"] = got;
                running = 0;
                return res;
            }
            App.Log("mise a jour : installeur verifie (" + got.Substring(0, 12) + "...), lancement");
            // apres la reponse a l'extension : l'installeur arrete ce programme et l'hote
            new Thread(() => {
                Thread.Sleep(800);
                try { Process.Start(new ProcessStartInfo(file, "--quiet --restart") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); }
                catch (System.Exception e) { App.Log("mise a jour : lancement impossible : " + e.Message); running = 0; }
            }) { IsBackground = false }.Start();
            res["ok"] = true;
            return res;
        } catch (System.Exception e) {
            running = 0;
            App.Log("mise a jour : " + e.Message);
            res["error"] = e.Message;
            return res;
        }
    }
}


// Balises du fichier audio (0.7.30) : titre propre, adresse d'origine (commentaire) et pochette (vignette deja en cache, si elle existe).
// Aucune invention : pas d'artiste ni d'album tant que la source ne les donne pas.
static class AudioTags {
    static string Esc(string s) { return (s ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim(); }
    public static string Args(Item it, out string cover) {
        cover = null;
        var sb = new StringBuilder();
        string title = Fmt.ShortTitle(it.Title);
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension((it.Name ?? "").TrimStart('~'));
        if (!string.IsNullOrWhiteSpace(title)) sb.Append(" -metadata ").Append(Hls.Q("title=" + Esc(title)));
        if (!string.IsNullOrEmpty(it.Url) && (it.Url.StartsWith("http://") || it.Url.StartsWith("https://")) && it.Url.Length < 400) sb.Append(" -metadata ").Append(Hls.Q("comment=" + Esc(it.Url)));
        try { string f = Path.Combine(Thumbs.Dir, it.Id + ".jpg"); if (File.Exists(f) && new FileInfo(f).Length > 200) cover = f; } catch (System.Exception) { }
        return sb.ToString();
    }
    // MP3 : etiquette ID3v2.3 (la plus lue) ; M4A : image « jointe »
    public static string CoverFlags(string fmt) {
        return fmt == "m4a" ? " -disposition:v:0 attached_pic" : " -id3v2_version 3 -metadata:s:v title=\"Cover\" -metadata:s:v comment=\"Cover (front)\"";
    }
}
