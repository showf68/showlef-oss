// ShowLef - tube nomme : recoit les requetes de l'extension (relayees par showlef-host.exe).
// Une connexion = une ligne JSON en entree, une ligne JSON en sortie.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

static class Ipc {
    public static Func<Dictionary<string, object>, string> OnAddAsk;   // fenetre de confirmation (interface) : id ajoute, null si annule
    public static Action OnShow;
    public static Action<string> OnOpen;
    public static Action<Item> OnDuplicate;
    public static Func<string, string> OnPickFolder;   // site -> dossier choisi (null si annule)   // settings / tools / add

    public static void Start() {
        var th = new Thread(Loop) { IsBackground = true, Name = "pipe" };
        th.Start();
    }

    static void Loop() {
        while (true) {
            try {
                var srv = new NamedPipeServerStream(App.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte);
                srv.WaitForConnection();
                var t = new Thread(() => Handle(srv)) { IsBackground = true };
                t.Start();
            } catch (Exception e) { App.Log("pipe : " + e.Message); Thread.Sleep(1000); }
        }
    }

    static void Handle(NamedPipeServerStream srv) {
        using (srv) {
            try {
                var rd = new StreamReader(srv, new UTF8Encoding(false));
                string line = rd.ReadLine();
                var t0 = DateTime.UtcNow;
                string reply = Json.Ser(Process(line));
                double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                if (ms > 2000 && !(line ?? "").Contains("\"pilot_wait\"") && !(line ?? "").Contains("\"playlist_add\"")) App.Log("commande lente (" + (int)ms + " ms) : " + (line ?? "").Substring(0, Math.Min(80, (line ?? "").Length)));
                var b = new UTF8Encoding(false).GetBytes(reply + "\n");
                srv.Write(b, 0, b.Length);
                srv.Flush();
                srv.WaitForPipeDrain();
            } catch (Exception e) { App.Log("pipe handle : " + e.Message); }
        }
    }

    // Contact observe pendant CE processus. Le fichier est conserve pour les anciens guides, mais ne prouve
    // jamais la connexion du rail (ni la presence dans Chrome) : aucune resurrection depuis une trace sur disque.
    static DateTime extSeenAt = DateTime.MinValue;
    static long lastExtensionTicks;
    public static bool ExtensionConnected { get { return ExtensionPresence.RecentContact(Interlocked.Read(ref lastExtensionTicks), DateTime.UtcNow); } }
    static void TouchExtSeen() {
        Interlocked.Exchange(ref lastExtensionTicks, DateTime.UtcNow.Ticks);
        if ((DateTime.UtcNow - extSeenAt).TotalSeconds < 3) return;
        extSeenAt = DateTime.UtcNow;
        try { File.WriteAllText(Path.Combine(App.Dir, "ext-seen.txt"), DateTime.UtcNow.ToString("o")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static object Process(string line) {
        try {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string raw = line ?? "{}";
            // pas d'un telechargement pilote : les corps (base64, plusieurs Mo) sont sortis AVANT la lecture JSON, qui
            // n'a plus que l'enveloppe a lire (mesure 21/10 : 15 s de lecture JSON sur 37 s pour 560 Mo)
            List<string> bins = null;
            if (raw.Length > 65536 && raw.IndexOf("\"cmd\":\"pilot_step\"", 0, Math.Min(raw.Length, 200), StringComparison.Ordinal) >= 0) {
                bins = new List<string>(); raw = CutBodies(raw, bins);
            }
            var m = Json.Obj(raw);
            string cmd = Json.Str(m, "cmd");
            if (cmd == "ping" || cmd == "pilot_wait") TouchExtSeen(); // attente longue renouvelee toutes les 20 s ; preuve en memoire, jamais l'ancien fichier
            if (cmd == "pilot_step") { Pilot.ParseMs += sw.ElapsedMilliseconds; Pilot.InBytes += (line ?? "").Length; }
            // porte d'entree : rien ne part tant que la connexion Google (une fois) n'est pas faite ; la fenetre s'ouvre
            if ((cmd == "add" && !Json.Bool(m, "ask")) || cmd == "capture_begin" || cmd == "pilot_start" || cmd == "playlist_add") {
                if (!Gate.Open) { Gate.Ask(); return Err(Tr.T("e_login")); }
            }
            switch (cmd) {
                case "extension_probe": {
                    string token = Json.Str(m, "token");
                    return new Dictionary<string, object> { { "ok", true }, { "token", token }, { "connected", ExtensionProbe.Check(token, 600) } };
                }
                case "extension_probe_reply": {
                    bool accepted = ExtensionProbe.Acknowledge(Json.Str(m, "token"), DateTime.UtcNow);
                    if (accepted) TouchExtSeen();
                    return new Dictionary<string, object> { { "ok", accepted } };
                }
                case "ping":
                    return new Dictionary<string, object> { { "ok", true }, { "version", App.Version }, { "name", App.Name }, { "selfupdate", true },
                        { "tools", new Dictionary<string, object> { { "ffmpeg", Tools.Ffmpeg != null }, { "ytdlp", Tools.Ytdlp != null } } } };
                case "add":
                    if (Json.Bool(m, "ask") && OnAddAsk != null) {
                        string askedId = OnAddAsk(m);
                        if (askedId == null) return new Dictionary<string, object> { { "ok", false }, { "error", "cancelled" } };
                        return new Dictionary<string, object> { { "ok", true }, { "id", askedId } };
                    }
                    try {
                        var it = Mgr.Add(m);
                        return new Dictionary<string, object> { { "ok", true }, { "id", it.Id } };
                    } catch (DuplicateException d) {
                        if (OnDuplicate != null) OnDuplicate(d.Existing);
                        return new Dictionary<string, object> { { "ok", false }, { "error", "duplicate" }, { "id", d.Existing.Id } };
                    }
                case "capture_begin": {
                    var c = Capture.Begin(m);
                    var have = new Dictionary<string, object>();
                    if (Capture.Have(c, have) == 0) return new Dictionary<string, object> { { "ok", true }, { "id", c.Id } };
                    return new Dictionary<string, object> { { "ok", true }, { "id", c.Id }, { "resumed", true }, { "have", have } };
                }
                case "latest":
                    // derniere version publiee (lecture seule) : la meme que celle du menu « Rechercher les mises a jour »
                    try { var l = SelfUpdate.Latest(); l["ok"] = true; l["newer"] = SelfUpdate.Newer(Json.Str(l, "version")); return l; }
                    catch (System.Exception e) { return Err(e.Message); }
                case "selfupdate":
                    // l'extension demande la mise a jour : adresse fixe, empreinte attendue fournie par l'extension
                    return SelfUpdate.Run(Json.Str(m, "sha256"), Json.Str(m, "version"));
                case "capture_find": {
                    // y a-t-il une capture coupee a reprendre pour cette video ? (bouton « Reprendre (63 %) »)
                    var res = new Dictionary<string, object> { { "ok", true } };
                    foreach (bool audio in new[] { false, true }) {
                        var c = Capture.Resumable(Json.Str(m, "key"), audio);
                        if (c == null) continue;
                        long got = Capture.Have(c, null);
                        if (got <= 0) continue;
                        res[audio ? "audio" : "video"] = c.Total > 0 ? (int)Math.Min(99, got * 100 / c.Total) : 1;
                    }
                    return res;
                }
                case "capture_chunk": {
                    var c = Mgr.Get(Json.Str(m, "id"));
                    if (c == null || c.Kind != "capture") return Err("unknown id");
                    long off; long.TryParse(Json.Str(m, "offset") ?? "0", out off);
                    Capture.Chunk(c, Json.Str(m, "track"), off, Json.Str(m, "data"));
                    return new Dictionary<string, object> { { "ok", true } };
                }
                case "capture_seg": {
                    var c = Mgr.Get(Json.Str(m, "id"));
                    if (c == null || c.Kind != "capture") return Err("unknown id");
                    int itag; long seq;
                    if (!int.TryParse(Json.Str(m, "itag"), out itag) || !long.TryParse(Json.Str(m, "seq") ?? "0", out seq)) return Err("bad segment");
                    Capture.Segment(c, itag, Json.Bool(m, "init"), seq, Json.Str(m, "data"));
                    return new Dictionary<string, object> { { "ok", true } };
                }
                case "capture_end": {
                    var c = Mgr.Get(Json.Str(m, "id"));
                    if (c == null || c.Kind != "capture") return Err("unknown id");
                    var vd = m.ContainsKey("video") ? m["video"] as Dictionary<string, object> : null;
                    var ad = m.ContainsKey("audio") ? m["audio"] as Dictionary<string, object> : null;
                    if (ad != null) c.CapAudio = Json.Str(ad, "itag") + ":" + Json.Str(ad, "ext");
                    if (vd != null) c.CapVideo = Json.Str(vd, "itag") + ":" + Json.Str(vd, "ext");
                    var st = m.ContainsKey("stats") ? m["stats"] as Dictionary<string, object> : null;
                    if (st != null) App.Log("capture navigateur : " + Json.Str(st, "n") + " morceaux, " + Json.Str(st, "workers") + " en parallele, " + Json.Str(st, "wallMs") + " ms en tout, lecture cumulee " + Json.Str(st, "getMs") + " ms, remise cumulee " + Json.Str(st, "sendMs") + " ms, " + Json.Str(st, "bytes") + " octets");
                    Capture.End(c);
                    return new Dictionary<string, object> { { "ok", true } };
                }
                case "capture_abort": {
                    var c = Mgr.Get(Json.Str(m, "id"));
                    if (c == null || c.Kind != "capture") return Err("unknown id");
                    Capture.Abort(c, Tr.T("e_capStopped"));
                    return new Dictionary<string, object> { { "ok", true } };
                }
                // 0.7.16 « comme IDM » : connaissance des sites dans le programme (Pilot.cs), extension = transport generique
                case "pilot_rules": return Pilot.Rules();
                case "pilot_data": return Pilot.Data(m);
                case "pilot_page": return Pilot.Page(m);
                case "pilot_start": return Pilot.Start(m);
                case "pilot_step": return Pilot.Step(m, bins);
                case "pilot_abort": return Pilot.Abort(m);
                case "pilot_wait": { int ws; int.TryParse(Json.Str(m, "sec") ?? "20", out ws); return Wake.Wait(ws); }
                case "pilot_wake_failed": return Wake.Failed(Json.Str(m, "tag"));
                case "playlist_add": return Playlists.Add(m);
                case "ytprobe":
                    return new Dictionary<string, object> { { "ok", true }, { "direct", Ytdlp.Probe(Json.Str(m, "url")) } };
                case "list":
                    return new Dictionary<string, object> { { "ok", true }, { "items", Mgr.ListForExtension() } };
                case "control": {
                    string a = Json.Str(m, "action");
                    // annuler une suppression : la ligne n'est plus dans la liste, elle est gardee a part
                    if (a == "undelete") return Mgr.Undo(Json.Str(m, "id")) ? new Dictionary<string, object> { { "ok", true } } : Err("cannot restore");
                    var x = Mgr.Get(Json.Str(m, "id"));
                    if (x == null) return Err("unknown id");
                    if (a == "delete") { bool undo = Mgr.Trash(x); return new Dictionary<string, object> { { "ok", true }, { "undo", undo } }; }
                    if (a == "pause") Mgr.Pause(x);
                    else if (a == "resume") Mgr.Resume(x);
                    else if (a == "cancel" || a == "remove") Mgr.Remove(x, a == "cancel" && x.Status != "done");
                    else if (a == "open") { if (!File.Exists(x.FilePath)) return Err("file missing"); System.Diagnostics.Process.Start(x.FilePath); }
                    else if (a == "folder") OpenFolder(x);
                    else return Err("unknown action");
                    return new Dictionary<string, object> { { "ok", true } };
                }
                case "open":
                    if (OnOpen != null) OnOpen(Json.Str(m, "what"));
                    return new Dictionary<string, object> { { "ok", true } };
                case "clip": {
                    var src = Mgr.Get(Json.Str(m, "id"));
                    double from, to;
                    if (src == null || src.Status != "done") return Err("unknown id");
                    if (!double.TryParse(Json.Str(m, "from"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out from)
                        || !double.TryParse(Json.Str(m, "to"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out to) || to <= from) return Err("bad range");
                    var c = Clip.Create(src, from, to);
                    return new Dictionary<string, object> { { "ok", true }, { "id", c.Id } };
                }
                case "site_folder": {
                    string site = (Json.Str(m, "site") ?? "").ToLowerInvariant();
                    if (site.StartsWith("www.")) site = site.Substring(4);
                    if (site.Length == 0 || site.IndexOfAny(new[] { '/', '\\', ' ' }) >= 0) return Err("bad site");
                    string action = Json.Str(m, "action") ?? "get";
                    if (action == "pick" && OnPickFolder != null) {
                        string f = OnPickFolder(site);
                        if (f != null) { Settings.Current.SiteFolders[site] = f; Settings.Save(); }
                    } else if (action == "clear") {
                        Settings.Current.SiteFolders.Remove(site); Settings.Save();
                    }
                    string cur; Settings.Current.SiteFolders.TryGetValue(site, out cur);
                    return new Dictionary<string, object> { { "ok", true }, { "folder", cur } };
                }
                case "show":
                    if (OnShow != null) OnShow();
                    return new Dictionary<string, object> { { "ok", true } };
            }
            return Err("unknown cmd");
        } catch (Exception e) { return Err(e.Message); }
    }

    public static void OpenFolder(Item x) {
        if (File.Exists(x.FilePath)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + x.FilePath + "\"");
        else if (Directory.Exists(x.Folder)) System.Diagnostics.Process.Start("explorer.exe", "\"" + x.Folder + "\"");
    }

    static object Err(string e) { return new Dictionary<string, object> { { "ok", false }, { "error", e } }; }

    // Remplace chaque "body":"<base64>" par "body":"#n" et garde le base64 a part (le base64 ne contient ni " ni \).
    static string CutBodies(string s, List<string> bins) {
        var sb = new StringBuilder(4096);
        int p = 0, k;
        while ((k = s.IndexOf("\"body\":\"", p, StringComparison.Ordinal)) >= 0) {
            int a = k + 8, e = s.IndexOf('"', a);
            if (e < 0) break;
            sb.Append(s, p, a - p).Append('#').Append(bins.Count);
            bins.Add(s.Substring(a, e - a));
            p = e;
        }
        sb.Append(s, p, s.Length - p);
        return sb.ToString();
    }

    // Cote client (instance deja lancee, showlef-host) : envoie une ligne, lit la reponse.
    public static string Send(string json, int timeoutMs) {
        using (var c = new NamedPipeClientStream(".", App.PipeName, PipeDirection.InOut)) {
            c.Connect(timeoutMs);
            var b = new UTF8Encoding(false).GetBytes(json + "\n");
            c.Write(b, 0, b.Length); c.Flush();
            return new StreamReader(c, new UTF8Encoding(false)).ReadLine();
        }
    }
}

// Une preuve fraiche, correlee a la demande : aucune preference Chrome ni ancien ping ne peut la satisfaire.
static class ExtensionProbe {
    sealed class Pending { public DateTime Until; public bool Answered; }
    static readonly object gate = new object();
    static readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
    public static bool Check(string token, int waitMs) {
        if (token == null || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{32}$")) return false;
        lock (gate) {
            var now = DateTime.UtcNow;
            foreach (var key in new List<string>(pending.Keys)) if (pending[key].Until <= now) { pending.Remove(key); Wake.Drop("extension:" + key); }
            Pending p;
            if (!pending.TryGetValue(token, out p)) {
                if (pending.Count >= 32) return false;
                pending[token] = p = new Pending { Until = now.AddSeconds(4) };
                Wake.Probe(token);
            }
            var end = now.AddMilliseconds(Math.Max(0, Math.Min(1000, waitMs)));
            while (!p.Answered && DateTime.UtcNow < end) Monitor.Wait(gate, Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds));
            return p.Answered && p.Until > DateTime.UtcNow;
        }
    }
    public static bool Acknowledge(string token, DateTime now) {
        lock (gate) {
            Pending p;
            if (token == null || !pending.TryGetValue(token, out p) || p.Until <= now) return false;
            p.Answered = true; Monitor.PulseAll(gate); return true;
        }
    }
}
