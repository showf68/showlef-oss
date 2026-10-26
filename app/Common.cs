// ShowLef - socle commun : constantes, chemins, JSON, reglages, langues.
// C# 5 (csc du .NET Framework de Windows) : pas d'interpolation $"", pas de ?. ni de =>.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class App {
    public const string Name = "ShowLef";            // nom de travail (placeholder), cf README
    public const string Version = "0.8.0";
    public const string HostName = "com.showlef.app";
    public static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowLef");
    public static string Dir = DefaultDir;           // --dir : donnees a part pour les bancs d'essai
    public static string ToolsDirBench;              // --tools-base (banc) : outils a part, jamais ceux de l'installation
    public static string ToolsDir { get { return ToolsDirBench ?? Path.Combine(DefaultDir, "tools"); } }
    public static string TempDir { get { return Path.Combine(Dir, "temp"); } }
    public static string PipeName { get { return "showlef-" + Environment.UserName.ToLowerInvariant(); } }
    public static string ExePath { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }

    public static void Log(string s) {
        try {
            Directory.CreateDirectory(Dir);
            string f = Path.Combine(Dir, "showlef.log");
            if (File.Exists(f) && new FileInfo(f).Length > 2000000) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n", Encoding.UTF8);
        } catch { }
    }
}

static class Json {
    static JavaScriptSerializer S() { var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; return s; }
    public static string Ser(object o) { return S().Serialize(o); }
    public static Dictionary<string, object> Obj(string s) { return S().Deserialize<Dictionary<string, object>>(s); }
    public static T De<T>(string s) { return S().Deserialize<T>(s); }

    public static string Str(Dictionary<string, object> d, string k) {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }
    public static bool Bool(Dictionary<string, object> d, string k) {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return false;
        if (v is bool) return (bool)v;
        return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
    }
    public static List<Dictionary<string, object>> List(Dictionary<string, object> d, string k) {
        var r = new List<Dictionary<string, object>>();
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return r;
        var arr = v as System.Collections.IEnumerable;
        if (arr == null) return r;
        foreach (object o in arr) { var e = o as Dictionary<string, object>; if (e != null) r.Add(e); }
        return r;
    }
}

public class Settings {
    public string Folder { get; set; }
    public bool SortByType { get; set; }
    public int Connections { get; set; }
    public int MaxParallel { get; set; }
    public int SpeedLimitKB { get; set; }
    public string Quality { get; set; }
    public string AudioFormat { get; set; }
    public string FfmpegPath { get; set; }
    public string YtdlpPath { get; set; }
    public bool Clipboard { get; set; }
    public bool StartWithWindows { get; set; }
    public string Lang { get; set; }
    public bool ScheduleOn { get; set; }
    public string ScheduleTime { get; set; }
    public bool ShutdownWhenDone { get; set; }
    public bool Notify { get; set; }
    public bool CloseToTray { get; set; }
    public int Retries { get; set; }
    public Dictionary<string, string> SiteFolders { get; set; }   // site (sans www.) -> dossier
    public bool Loudnorm { get; set; }        // audio : volume regulier (cours, conferences)
    public string AudioSpeed { get; set; }    // audio : "1", "1.25", "1.5", "1.75", "2"
    public bool PingUsers { get; set; }       // signal quotidien au serveur de ShowLef : actif d'office, sans reglage visible (ShowF 21/10) ; « PingUsers »: false dans settings.json le coupe

    public const int EcoKB = 2048;           // « Laisser Internet disponible » : limite en un clic, en Ko/s

    public Settings() {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        SortByType = false; Connections = 8; MaxParallel = 3; SpeedLimitKB = 0;
        Quality = "720"; AudioFormat = "mp3";   // « qualite normale » (v0.7.5) : la meilleure = souvent la 4K, enorme FfmpegPath = ""; YtdlpPath = "";
        Clipboard = false; StartWithWindows = false; Lang = "";
        ScheduleOn = false; ScheduleTime = "02:00"; ShutdownWhenDone = false;
        Notify = true; CloseToTray = true; Retries = 10;
        Loudnorm = false; AudioSpeed = "1"; PingUsers = true;
        SiteFolders = new Dictionary<string, string>();
    }

    static string FilePath { get { return Path.Combine(App.Dir, "settings.json"); } }
    public static Settings Current = new Settings();

    public static void Load() {
        try { if (File.Exists(FilePath)) Current = Json.De<Settings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Settings(); }
        catch (Exception e) { App.Log("settings illisibles : " + e.Message); Current = new Settings(); }
        Current.Connections = Math.Max(1, Math.Min(32, Current.Connections));
        Current.MaxParallel = Math.Max(1, Math.Min(10, Current.MaxParallel));
        if (Current.Retries < 1) Current.Retries = 10;
        if (Current.SiteFolders == null) Current.SiteFolders = new Dictionary<string, string>();
    }
    public static void Save() {
        Directory.CreateDirectory(App.Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Json.Ser(Current), new UTF8Encoding(false));
        Fmt.ReplaceFile(tmp, FilePath);
    }
}

// Dossier choisi pour un site : « music.example.com » vaut aussi pour « www.music.example.com ».
static class Sites {
    public static string Key(string url) {
        try { return new Uri(url).Host.ToLowerInvariant().StartsWith("www.") ? new Uri(url).Host.ToLowerInvariant().Substring(4) : new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }
    public static string FolderFor(string pageUrl) {
        string k = Key(pageUrl ?? "");
        if (k.Length == 0) return null;
        foreach (var kv in Settings.Current.SiteFolders)
            // chemin complet seulement : un chemin relatif se creerait dans le dossier courant du programme
            // lettre de lecteur (« D:\cours ») ou partage reseau (« \\serveur\cours », revue Codex n° 19)
            if ((k == kv.Key || k.EndsWith("." + kv.Key)) && !string.IsNullOrEmpty(kv.Value) && Path.IsPathRooted(kv.Value)
                && ((kv.Value.Length > 3 && kv.Value[1] == ':') || (kv.Value.StartsWith(@"\\") && kv.Value.TrimStart('\\').Contains('\\')))) return kv.Value;
        return null;
    }
}

// Outils annexes : ffmpeg et yt-dlp. Ordre : chemin des reglages, dossier tools de ShowLef, PATH.
static class Tools {
    public static string Find(string exe, string configured) {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        string local = Path.Combine(App.ToolsDir, exe);
        if (File.Exists(local)) return local;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string d in path.Split(';')) {
            try {
                if (d.Trim().Length == 0) continue;
                string f = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(f)) return f;
            } catch { }
        }
        return null;
    }
    public static string Ffmpeg { get { return Find("ffmpeg.exe", Settings.Current.FfmpegPath); } }
    public static string Ytdlp { get { return Find("yt-dlp.exe", Settings.Current.YtdlpPath); } }
}

static class Fmt {
    public static string Size(long b) {
        if (b < 0) return "?";
        string[] u = { Tr.T("uB"), Tr.T("uKB"), Tr.T("uMB"), Tr.T("uGB"), Tr.T("uTB") };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0")) + " " + u[i];
    }
    public static string Eta(double sec) {
        if (sec <= 0 || double.IsInfinity(sec) || double.IsNaN(sec)) return "";
        var t = TimeSpan.FromSeconds(sec);
        if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        return t.Minutes + ":" + t.Seconds.ToString("00");
    }
    // Titre de page souvent enorme (« Film - prix | cliquez ici pour verifier... ») : on garde ce qui precede
    // le premier « | » quand ce debut fait au moins 8 caracteres (le tiret ne coupe pas : « Artiste - Chant »
    // garde le chant), on retire « - YouTube » en fin, puis on limite a ~80 caracteres sans couper un mot.
    public const int MaxTitle = 80;
    public static string ShortTitle(string t) {
        if (string.IsNullOrEmpty(t)) return t;
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*[-–—]\s*YouTube$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 0.7.29 : nom de fichier propre. Hashtags, etiquettes de promotion « (Official Video) », « [HD] », emojis et symboles
        // disparaissent ; l'hebreu, les chiffres et la ponctuation utile restent. Sans rien de lisible apres nettoyage : on garde l'original.
        string clean = t;
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(^|\s)[#＃]\S+", " ");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s*[\(\[]\s*(official|lyrics?|lyric video|audio|video|clip|hd|4k|full|live)[^\)\]]{0,30}[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[\u2190-\u2BFF\u2600-\u27BF\uFE0F\u200D]|[\uD83C-\uD83E][\uDC00-\uDFFF]", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('-', '–', '—', '|', ',', ':').Trim();
        if (clean.Length >= 3) t = clean;
        for (int i = 0; i < t.Length; i++) {
            char c = t[i];
            bool sep = c == '|' || c == '｜';
            if (!sep) continue;
            string head = t.Substring(0, i).Trim().TrimEnd('-', '–', '—', '|', ',', ':').Trim();
            if (head.Length >= 8) { t = head; break; }
        }
        if (t.Length > MaxTitle) {
            int cut = t.LastIndexOf(' ', MaxTitle);
            t = (cut >= 20 ? t.Substring(0, cut) : t.Substring(0, MaxTitle)).TrimEnd(' ', '-', '–', '—', '|', ',', ':', '.');
        }
        return t;
    }
    // Nom de fichier deja forme (« titre.mp4 ») : seule la partie avant l'extension est raccourcie.
    public static string ShortFileName(string n) {
        if (string.IsNullOrEmpty(n)) return n;
        string ext = Path.GetExtension(n);
        bool realExt = ext.Length >= 2 && ext.Length <= 6 && ext.Skip(1).All(char.IsLetterOrDigit);
        string stem = realExt ? n.Substring(0, n.Length - ext.Length) : n;
        string s2 = ShortTitle(stem);
        return string.IsNullOrEmpty(s2) ? n : s2 + (realExt ? ext : "");
    }

    static readonly char[] Bad = Path.GetInvalidFileNameChars();
    // Remplacement atomique : jamais d'instant sans le fichier (un arret brutal entre Delete et Move
    // laissait une liste vide au relancement).
    public static void ReplaceFile(string tmp, string dst) {
        if (File.Exists(dst)) {
            try { File.Replace(tmp, dst, null, true); return; } catch (Exception e) { App.Log("replace " + dst + " : " + e.Message); }
            File.Copy(tmp, dst, true); File.Delete(tmp); return;
        }
        File.Move(tmp, dst);
    }
    public static string SafeName(string s) {
        if (string.IsNullOrEmpty(s)) return "download";
        // remplacements lisibles plutot que « _ » : « תשפ"ב » garde ses guillemets (gershayim), « // » devient un tiret
        bool heb = s.Any(ch => ch >= '֐' && ch <= '׿');
        s = s.Replace("\"", heb ? "״" : "'").Replace(":", " -").Replace("/", "-").Replace("\\", "-").Replace("|", "-");
        var sb = new StringBuilder();
        foreach (char c in s) if (!(Array.IndexOf(Bad, c) >= 0 || c < 32)) sb.Append(c);
        string r = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s*-(\s*-)+\s*", " - ");
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\s{2,}", " ").Trim().Trim('.');
        if (r.Length > 150) r = r.Substring(0, 150).Trim();
        if (r.Length == 0) return "download";
        // noms reserves de Windows (« CON.mp3 », « nul », « com1.txt ») : inutilisables, on les decale (revue Codex n° 19)
        string stemOnly = r.Split('.')[0].Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(stemOnly, "^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) r = "_" + r;
        return r;
    }

    // Provenance Internet (« Mark of the Web »), comme les telechargements de Chrome : Windows previent avant d'ouvrir
    // un programme recu d'Internet (revue Codex n° 4). Flux NTFS « :Zone.Identifier » ; volume sans flux (FAT, cle
    // USB) : rien, sans erreur.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, uint n, out uint written, IntPtr ov);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    public static void MarkInternet(string path, string url, string referer) {
        try {
            Func<string, string> clean = u => { try { var x = new Uri(u); return x.Scheme == "http" || x.Scheme == "https" ? x.GetLeftPart(UriPartial.Path) : null; } catch { return null; } };
            string host = clean(url), refr = clean(referer);
            string txt = "[ZoneTransfer]\r\nZoneId=3\r\n" + (refr != null ? "ReferrerUrl=" + refr + "\r\n" : "") + (host != null ? "HostUrl=" + host + "\r\n" : "");
            byte[] b = Encoding.ASCII.GetBytes(txt);
            IntPtr h = CreateFileW(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);   // GENERIC_WRITE, CREATE_ALWAYS
            if (h == new IntPtr(-1)) return;
            uint w; WriteFile(h, b, (uint)b.Length, out w, IntPtr.Zero);
            CloseHandle(h);
        } catch (System.Exception e) { App.Log("provenance " + path + " : " + e.Message); }
    }
}

// Langues : hebreu, francais, anglais (comme NoDodo). Defaut = langue de Windows.
static class Tr {
    public static int Lang = 2;
    public static readonly string[] Codes = { "he", "fr", "en" };
    static readonly Dictionary<string, string[]> D = new Dictionary<string, string[]> {
        { "sub",       new[] { "מוריד שמע ווידאו מכל דף", "Téléchargeur audio et vidéo", "Audio & video downloader" } },
        { "add",       new[] { "הוספה", "Ajouter", "Add" } },
        { "resume",    new[] { "המשך", "Reprendre", "Resume" } },
        { "pause",     new[] { "השהיה", "Pause", "Pause" } },
        { "resumeAll", new[] { "המשך הכל", "Tout reprendre", "Resume all" } },
        { "pauseAll",  new[] { "השהה הכל", "Tout suspendre", "Pause all" } },
        { "remove",    new[] { "הסרה", "Retirer", "Remove" } },
        { "removeDel", new[] { "הסרה ומחיקת הקובץ", "Retirer et supprimer le fichier", "Remove and delete file" } },
        { "open",      new[] { "פתיחה", "Ouvrir", "Open" } },
        { "folder",    new[] { "תיקייה", "Dossier", "Folder" } },
        { "settings",  new[] { "הגדרות", "Réglages", "Settings" } },
        { "tools",     new[] { "כלים", "Outils", "Tools" } },
        { "clearDone", new[] { "ניקוי הושלמו", "Effacer les terminés", "Clear finished" } },
        { "copyUrl",   new[] { "העתקת הכתובת", "Copier l'adresse", "Copy URL" } },
        { "retry",     new[] { "ניסיון חוזר", "Réessayer", "Retry" } },
        { "colName",   new[] { "שם", "Nom", "Name" } },
        { "colSize",   new[] { "גודל", "Taille", "Size" } },
        { "colProg",   new[] { "התקדמות", "Progression", "Progress" } },
        { "colSpeed",  new[] { "מהירות", "Vitesse", "Speed" } },
        { "colEta",    new[] { "נותר", "Restant", "Left" } },
        { "colStatus", new[] { "מצב", "État", "Status" } },
        { "colAdded",  new[] { "נוסף", "Ajouté", "Added" } },
        { "st_queued", new[] { "בתור", "En attente", "Queued" } },
        { "st_downloading", new[] { "מוריד", "Téléchargement", "Downloading" } },
        { "st_paused", new[] { "מושהה", "En pause", "Paused" } },
        { "st_merging",new[] { "מעבד", "Assemblage", "Processing" } },
        { "st_done",   new[] { "הושלם", "Terminé", "Done" } },
        { "st_error",  new[] { "שגיאה", "Erreur", "Error" } },
        { "active",    new[] { "פעילים", "actifs", "active" } },
        { "total",     new[] { "סה\"כ", "total", "total" } },
        { "held",      new[] { "התור יתחיל ב-", "La file démarre à ", "Queue starts at " } },
        { "startNow",  new[] { "התחלה עכשיו", "Démarrer maintenant", "Start now" } },
        { "ffOk",      new[] { "ffmpeg מותקן", "ffmpeg présent", "ffmpeg found" } },
        { "ffNo",      new[] { "ffmpeg חסר", "ffmpeg absent", "ffmpeg missing" } },
        { "ytOk",      new[] { "yt-dlp מותקן", "yt-dlp présent", "yt-dlp found" } },
        { "ytNo",      new[] { "yt-dlp חסר", "yt-dlp absent", "yt-dlp missing" } },
        { "url",       new[] { "כתובת", "Adresse", "URL" } },
        { "kind",      new[] { "סוג", "Type", "Type" } },
        { "k_auto",    new[] { "אוטומטי", "Automatique", "Automatic" } },
        { "k_file",    new[] { "קובץ ישיר", "Fichier direct", "Direct file" } },
        { "k_hls",     new[] { "שידור HLS", "Flux HLS", "HLS stream" } },
        { "k_dash",    new[] { "שידור DASH", "Flux DASH", "DASH stream" } },
        { "k_page",    new[] { "דף שלם", "Page entière (yt-dlp)", "Whole page (yt-dlp)" } },
        { "quality",   new[] { "איכות", "Qualité", "Quality" } },
        { "q_best",    new[] { "הטובה ביותר (קובץ גדול)", "La meilleure (fichier lourd)", "Best (large file)" } },
        { "q_normal",  new[] { "איכות רגילה", "Qualité normale", "Normal quality" } },
        { "q_small",   new[] { "קובץ קטן", "Fichier léger", "Small file" } },
        { "audioOnly", new[] { "שמע בלבד", "Audio seulement", "Audio only" } },
        { "audioFmt",  new[] { "פורמט שמע", "Format audio", "Audio format" } },
        { "q_now",     new[] { "להתחיל עכשיו", "Maintenant", "Start now" } },
        { "q_next",    new[] { "הבא בתור", "Ensuite", "Next" } },
        { "st_limitOn",  new[] { "האינטרנט פנוי לשימוש · לחיצה להחזרת המהירות המרבית", "Internet disponible · cliquer pour la vitesse maximale", "Internet kept free · click for full speed" } },
        { "st_limitOff", new[] { "מהירות מרבית · לחיצה להשארת האינטרנט פנוי", "Vitesse maximale · cliquer pour laisser Internet disponible", "Full speed · click to keep Internet free" } },
        { "copySupport", new[] { "להעתיק מידע לתמיכה", "Copier les informations pour le support", "Copy information for support" } },
        { "feedbackLink", new[] { "יצירת קשר ודירוג", "Contact et avis", "Contact and feedback" } },
        { "st_upd",    new[] { "גרסה חדשה {0} · לחיצה לעדכון", "Nouvelle version {0} · cliquer pour mettre à jour", "New version {0} · click to update" } },
        { "st_notice", new[] { "הודעה חדשה · לחיצה לקריאה", "Nouveau message · cliquer pour lire", "New message · click to read" } },
        { "upd_t",     new[] { "נדרש עדכון", "Mise à jour obligatoire", "Update required" } },
        { "upd_b",     new[] { "הגרסה הזאת כבר לא נתמכת. יש לעדכן כדי להמשיך להוריד.", "Cette version n'est plus prise en charge : mettez à jour pour continuer à télécharger.", "This version is no longer supported: please update to keep downloading." } },
        { "upd_now",   new[] { "לעדכן עכשיו", "Mettre à jour maintenant", "Update now" } },
        { "upd_quit",  new[] { "יציאה", "Quitter", "Quit" } },
        { "notice_t",  new[] { "הודעה משולף", "Message de ShowLef", "Message from ShowLef" } },
        { "notice_go", new[] { "לפתיחה", "Ouvrir", "Open" } },
        { "notice_ok", new[] { "סגירה", "Fermer", "Close" } },
        { "empty_t",   new[] { "הורידו את השיעור הראשון שלכם", "Téléchargez votre premier cours", "Download your first lesson" } },
        { "empty_b",   new[] { "בכרום, פתחו סרטון ולחצו על „הורדה”.", "Dans Chrome, ouvrez une vidéo et cliquez sur « Télécharger ».", "In Chrome, open a video and click “Download”." } },
        { "empty_b2",  new[] { "אפשר גם להעתיק את הקישור ולהדביק אותו כאן.", "Vous pouvez aussi copier son lien et le coller ici.", "You can also copy its link and paste it here." } },
        { "empty_btn", new[] { "הדבקת קישור", "Coller un lien", "Paste a link" } },
        { "audioOrig", new[] { "כמו שהוא (מיידי)", "Tel quel (instantané)", "As is (instant)" } },
        { "fileName",  new[] { "שם קובץ", "Nom du fichier", "File name" } },
        { "saveTo",    new[] { "שמירה ב", "Enregistrer dans", "Save to" } },
        { "browse",    new[] { "עיון...", "Parcourir...", "Browse..." } },
        { "download",  new[] { "הורדה", "Télécharger", "Download" } },
        { "later",     new[] { "לתור", "Dans la file", "Queue" } },
        { "cancel",    new[] { "ביטול", "Annuler", "Cancel" } },
        { "ok",        new[] { "אישור", "OK", "OK" } },
        { "s_general", new[] { "כללי", "Général", "General" } },
        { "s_sortType",new[] { "מיון לתיקיות לפי סוג (וידאו / שמע / קבצים)", "Ranger par type (Vidéo / Audio / Fichiers)", "Sort into folders by type (Video / Audio / Files)" } },
        { "s_conn",    new[] { "חיבורים לכל קובץ", "Connexions par fichier", "Connections per file" } },
        { "s_parallel",new[] { "הורדות במקביל", "Téléchargements simultanés", "Simultaneous downloads" } },
        { "s_limit",   new[] { "הגבלת מהירות (ק\"ב/ש, 0 = ללא)", "Vitesse max (Ko/s, 0 = illimitée)", "Speed limit (KB/s, 0 = none)" } },
        { "s_retries", new[] { "ניסיונות חוזרים", "Nouvelles tentatives", "Retries" } },
        { "s_clip",    new[] { "מעקב אחרי לוח ההעתקה", "Surveiller le presse-papiers", "Watch the clipboard" } },
        { "s_startup", new[] { "הפעלה עם הדלקת המחשב", "Lancer avec Windows", "Start with Windows" } },
        { "s_notify",  new[] { "הודעה בסיום הורדה", "Notifier à la fin d'un téléchargement", "Notify when a download ends" } },
        { "s_tray",    new[] { "סגירה למגש (ליד השעון)", "Fermer = réduire près de l'horloge", "Close to tray" } },
        { "s_sched",   new[] { "להתחיל את התור בשעה", "Démarrer la file à", "Start the queue at" } },
        { "s_shutdown",new[] { "כיבוי המחשב בסיום התור", "Éteindre le PC quand la file est finie", "Shut down when the queue is done" } },
        { "s_ffmpeg",  new[] { "נתיב ffmpeg (ריק = אוטומטי)", "Chemin ffmpeg (vide = automatique)", "ffmpeg path (empty = automatic)" } },
        { "s_ytdlp",   new[] { "נתיב yt-dlp (ריק = אוטומטי)", "Chemin yt-dlp (vide = automatique)", "yt-dlp path (empty = automatic)" } },
        { "s_lang",    new[] { "שפה", "Langue", "Language" } },
        { "t_title",   new[] { "כלים נלווים", "Outils annexes", "Companion tools" } },
        { "t_info",    new[] { "ffmpeg מחבר וממיר וידאו ושמע. yt-dlp מוריד מאתרים מורכבים, ו-deno עוזר לו ביוטיוב. כולם חינמיים.", "ffmpeg assemble et convertit audio et vidéo. yt-dlp télécharge depuis les sites complexes, aidé par deno pour YouTube. Tous sont gratuits.", "ffmpeg merges and converts audio and video. yt-dlp downloads from complex sites, helped by deno for YouTube. All are free." } },
        { "t_install", new[] { "התקנה / עדכון", "Installer / mettre à jour", "Install / update" } },
        { "t_working", new[] { "מוריד את הכלים...", "Téléchargement des outils...", "Downloading tools..." } },
        { "t_done",    new[] { "הכלים מוכנים", "Outils prêts", "Tools ready" } },
        { "n_done",    new[] { "ההורדה הושלמה", "Téléchargement terminé", "Download complete" } },
        { "n_doneClick", new[] { "לחיצה כאן: האזנה / צפייה", "Cliquez ici pour écouter / regarder", "Click here to play" } },
        { "n_doneFile",  new[] { "לחיצה כאן: פתיחת התיקייה", "Cliquez ici pour ouvrir le dossier", "Click here to open the folder" } },
        { "lastFolder",  new[] { "תיקיית ההורדה האחרונה", "Dossier du dernier téléchargement", "Last download folder" } },
        { "n_err",     new[] { "ההורדה נכשלה", "Échec du téléchargement", "Download failed" } },
        { "n_clip",    new[] { "קישור למדיה הועתק. ללחוץ כדי להוריד.", "Lien média copié. Cliquer pour télécharger.", "Media link copied. Click to download." } },
        { "n_shutdown",new[] { "התור הסתיים: המחשב ייכבה בעוד דקה (shutdown /a לביטול)", "File terminée : le PC s'éteint dans 1 minute (shutdown /a pour annuler)", "Queue done: PC shuts down in 1 minute (shutdown /a to cancel)" } },
        { "e_noFf",    new[] { "נדרש ffmpeg (תפריט כלים)", "ffmpeg requis (menu Outils)", "ffmpeg required (Tools menu)" } },
        { "e_noYt",    new[] { "נדרש yt-dlp (תפריט כלים)", "yt-dlp requis (menu Outils)", "yt-dlp required (Tools menu)" } },
        { "e_notHls",  new[] { "הכתובת אינה שידור HLS (אולי פג תוקפה)", "Cette adresse n'est pas un flux HLS (lien expiré ?)", "This URL is not an HLS stream (expired link?)" } },
        { "s_loud",    new[] { "שמע: עוצמה אחידה (שיעורים)", "Audio : volume régulier (cours)", "Audio: even volume (lessons)" } },
        { "s_speed",   new[] { "שמע: מהירות קבועה בקובץ", "Audio : vitesse enregistrée dans le fichier", "Audio: speed saved in the file" } },
        { "siteFolderFor", new[] { "תיקייה להורדות מהאתר:", "Dossier des téléchargements venant de", "Folder for downloads from" } },
        { "search",    new[] { "חיפוש", "Rechercher", "Search" } },
        { "clip",      new[] { "חיתוך קטע...", "Extraire un passage...", "Extract a clip..." } },
        { "clipFrom",  new[] { "מ- (דק:שנ)", "Début (min:s)", "From (min:s)" } },
        { "clipTo",    new[] { "עד (דק:שנ)", "Fin (min:s)", "To (min:s)" } },
        { "clipName",  new[] { "קטע", "extrait", "clip" } },
        { "n_dup",     new[] { "כבר הורד: הקובץ קיים", "Déjà téléchargé : le fichier existe", "Already downloaded: the file exists" } },
        { "e_noFormat",new[] { "אין פורמט וידאו נגיש לסרטון הזה (מסנן אינטרנט או הגבלה של האתר)", "Aucun format vidéo accessible pour cette vidéo (filtre internet ou restriction du site)", "No video format reachable for this video (internet filter or site restriction)" } },
        { "e_capStopped", new[] { "ההקלטה מהדפדפן נעצרה (הלשונית נסגרה?). פתחו את הסרטון שוב ולחצו על הורדה", "Capture interrompue (onglet fermé ?). Rouvrez la vidéo et relancez le téléchargement", "Capture stopped (tab closed?). Open the video again and download it again" } },
        { "e_capWaking", new[] { "ממשיך את ההורדה ברקע...", "Reprise en cours en arrière-plan...", "Resuming in the background..." } },
        { "e_drm",     new[] { "תוכן מוגן (DRM): לא ניתן להוריד", "Contenu protégé (DRM) : téléchargement impossible", "Protected content (DRM): cannot download" } },
        { "showWin",   new[] { "פתיחת " + App.Name, "Ouvrir " + App.Name, "Open " + App.Name } },
        { "addClip",   new[] { "הוספה מלוח ההעתקה", "Ajouter depuis le presse-papiers", "Add from clipboard" } },
        { "quit",      new[] { "יציאה", "Quitter", "Quit" } },
        { "checkUpd",  new[] { "בדיקת עדכונים", "Rechercher les mises à jour", "Check for updates" } },
        { "addExt",    new[] { "הוספת התוסף לכרום", "Ajouter l'extension à Chrome", "Add the extension to Chrome" } },
        { "updNone",   new[] { "שולף מעודכן", "ShowLef est à jour", "ShowLef is up to date" } },
        { "updGo",     new[] { "מעדכנים לגרסה", "Mise à jour vers la version", "Updating to version" } },
        { "updBusy",   new[] { "יש הורדה פעילה: העדכון אחרי שתסתיים", "Téléchargement en cours : mise à jour quand il sera fini", "A download is running: update when it is finished" } },
        { "updOffer",  new[] { "גרסה חדשה של שולף ({0}) זמינה. לחצו כאן לעדכון.", "Une nouvelle version de ShowLef ({0}) est disponible. Cliquez ici pour mettre à jour.", "A new version of ShowLef ({0}) is available. Click here to update." } },
        { "updCheck",  new[] { "בדיקת עדכון", "Rechercher une mise à jour", "Check for updates" } },
        { "updChecking", new[] { "בודקים אם יש עדכון…", "Recherche d’une mise à jour…", "Checking for updates…" } },
        { "updFail",   new[] { "העדכון לא הצליח", "La mise à jour n’a pas marché", "The update did not work" } },
        { "dirVideo",  new[] { "וידאו", "Vidéo", "Video" } },
        { "dirAudio",  new[] { "שמע", "Audio", "Audio" } },
        { "dirFiles",  new[] { "קבצים", "Fichiers", "Files" } },
        { "uB",  new[] { "ב'", "o", "B" } },
        { "uKB", new[] { "ק\"ב", "Ko", "KB" } },
        { "uMB", new[] { "מ\"ב", "Mo", "MB" } },
        { "uGB", new[] { "ג'\"ב", "Go", "GB" } },
        { "uTB", new[] { "ט\"ב", "To", "TB" } },
        { "perSec", new[] { "/ש'", "/s", "/s" } },
        { "play",      new[] { "ניגון", "Lire", "Play" } },
        { "trash",     new[] { "מחיקה (לסל המחזור)", "Supprimer (corbeille)", "Delete (recycle bin)" } },
        { "colActs",   new[] { "פעולות", "Actions", "Actions" } },
        { "deleted",   new[] { "נמחק:", "Supprimé :", "Deleted:" } },
        { "deletedN",  new[] { "נמחקו {0} קבצים", "{0} fichiers supprimés", "{0} files deleted" } },
        { "undo",      new[] { "ביטול", "Annuler", "Undo" } },
        { "undoFail",  new[] { "לא ניתן לשחזר את הקובץ מסל המחזור", "Impossible de remettre le fichier depuis la corbeille", "Could not restore the file from the recycle bin" } },
        { "preparing", new[] { "מכין את שולף…", "Préparation de ShowLef…", "Preparing ShowLef…" } },
        { "appName",   new[] { "שולף", "ShowLef", "ShowLef" } },
        { "addLink",   new[] { "הוספת קישור", "Ajouter un lien", "Add a link" } },
        { "f_all",     new[] { "הכול", "Tout", "All" } },
        { "f_active",  new[] { "בהורדה", "En cours", "In progress" } },
        { "f_done",    new[] { "הסתיימו", "Terminés", "Finished" } },
        { "f_error",   new[] { "נכשלו", "Échoués", "Failed" } },
        { "f_video",   new[] { "סרטונים", "Vidéos", "Videos" } },
        { "f_audio",   new[] { "שמע", "Audio", "Audio" } },
        { "f_sched",   new[] { "מתוזמנות", "Programmés", "Scheduled" } },
        { "dlFolder",  new[] { "תיקיית ההורדות", "Téléchargements", "Downloads" } },
        { "downloadsDir", new[] { "הורדות", "Téléchargements", "Downloads" } },
        { "freeSpace", new[] { "{0} פנויים", "{0} libres", "{0} free" } },
        { "searchPh",  new[] { "חיפוש בהורדות", "Rechercher dans les téléchargements", "Search downloads" } },
        { "g_today",   new[] { "היום", "Aujourd'hui", "Today" } },
        { "g_yest",    new[] { "אתמול", "Hier", "Yesterday" } },
        { "g_older",   new[] { "קודם לכן", "Plus ancien", "Older" } },
        { "c_done",    new[] { "הסתיים", "Terminé", "Done" } },
        { "c_queued",  new[] { "ממתין בתור", "En file", "Queued" } },
        { "c_sched",   new[] { "מתוזמן", "Programmé", "Scheduled" } },
        { "c_error",   new[] { "נכשל", "Échec", "Failed" } },
        { "c_dl",      new[] { "בהורדה", "En cours", "Downloading" } },
        { "c_paused",  new[] { "מושהה", "En pause", "Paused" } },
        { "c_merging", new[] { "מעבד את הקובץ", "Assemblage du fichier", "Processing the file" } },
        { "t_video",   new[] { "סרטון", "Vidéo", "Video" } },
        { "t_audio",   new[] { "שמע", "Audio", "Audio" } },
        { "t_file",    new[] { "קובץ", "Fichier", "File" } },
        { "of",        new[] { "{0} מתוך {1}", "{0} sur {1}", "{0} of {1}" } },
        { "etaS",      new[] { "עוד {0} שניות", "reste {0} s", "{0} s left" } },
        { "etaM",      new[] { "עוד {0}", "reste {0}", "{0} left" } },
        { "conns",     new[] { "{0} חיבורים", "{0} connexions", "{0} connections" } },
        { "others",    new[] { "ועוד {0}", "+ {0} autres", "+ {0} more" } },
        { "ago0",      new[] { "עכשיו", "à l'instant", "just now" } },
        { "ago1m",     new[] { "לפני דקה", "il y a 1 min", "1 min ago" } },
        { "agoM",      new[] { "לפני {0} דקות", "il y a {0} min", "{0} min ago" } },
        { "ago1h",     new[] { "לפני שעה", "il y a 1 h", "1 h ago" } },
        { "ago2h",     new[] { "לפני שעתיים", "il y a 2 h", "2 h ago" } },
        { "agoH",      new[] { "לפני {0} שעות", "il y a {0} h", "{0} h ago" } },
        { "yestAt",    new[] { "אתמול {0}", "hier {0}", "yesterday {0}" } },
        { "stActive",  new[] { "{0} בהורדה", "{0} en cours", "{0} downloading" } },
        { "stTotal",   new[] { "{0} הורדות", "{0} téléchargements", "{0} downloads" } },
        { "cancelDl",  new[] { "ביטול ההורדה", "Annuler le téléchargement", "Cancel download" } },
        { "empty",     new[] { "עוד אין הורדות. אפשר להדביק קישור, לגרור אותו לכאן, או ללחוץ על הוספת קישור.", "Aucun téléchargement pour l'instant. Collez ou glissez un lien ici, ou cliquez sur Ajouter un lien.", "No downloads yet. Paste or drop a link here, or click Add a link." } },
        { "emptyFilter", new[] { "אין כאן כלום", "Rien ici", "Nothing here" } },
        { "e_denied",  new[] { "האתר סירב לתת את הקובץ. פתחו את הדף בכרום (והתחברו אם צריך), ואז נסו שוב.", "Le site refuse l'accès : ouvrez la page dans Chrome (connectez-vous si besoin) puis réessayez.", "The site refuses access: open the page in Chrome (sign in if needed), then retry." } },
        { "e_disk",    new[] { "אין מספיק מקום בדיסק. פנו מקום ונסו שוב.", "Disque plein : libérez de la place puis réessayez.", "Disk full: free some space, then retry." } },
        { "e_gone",    new[] { "הקובץ כבר לא נמצא באתר (אולי הקישור פג).", "Fichier introuvable sur le site (lien expiré ?).", "File not found on the site (expired link?)." } },
        { "e_busy",    new[] { "האתר לא עונה כרגע. נסו שוב מאוחר יותר.", "Le site ne répond pas pour l'instant : réessayez plus tard.", "The site is not answering right now: retry later." } },
        { "e_net",     new[] { "החיבור נקטע. בדקו את האינטרנט ונסו שוב.", "Connexion coupée : vérifiez internet puis réessayez.", "Connection lost: check the internet, then retry." } },
        { "e_tls",     new[] { "הסינון של האינטרנט (כמו נטפרי) לא מאפשר להוריד מהאתר הזה. אפשר לבקש מהם לפתוח אותו.", "Le filtre Internet (type NetFree) bloque les téléchargements depuis ce site : demandez-lui de l'ouvrir.", "Your Internet filter (like NetFree) blocks downloads from this site: ask it to open the site." } },
        { "e_login",   new[] { "שולף עדיין לא הופעל: הזדהו פעם אחת עם גוגל בחלון שנפתח בתוכנה.", "ShowLef n'est pas encore activé : authentifiez-vous une fois avec Google dans la fenêtre ouverte par le programme.", "ShowLef is not activated yet: sign in once with Google in the window the program opened." } },
        { "login_t",   new[] { "הפעלה", "Activation", "Activation" } },
        { "login_h",   new[] { "הפעלת שולף", "Activer ShowLef", "Activate ShowLef" } },
        { "login_b",   new[] { "כדי להפעיל את שולף, יש להזדהות פעם אחת עם גוגל.", "Pour activer ShowLef, veuillez vous authentifier une seule fois avec votre compte Google.", "To activate ShowLef, please sign in once with your Google account." } },
        { "login_go",  new[] { "המשך עם גוגל", "Continuer avec Google", "Continue with Google" } },
        { "login_busy", new[] { "ממתינים לגוגל…", "En attente de Google…", "Waiting for Google…" } },
        { "acc_tip",   new[] { "החשבון שלי", "Mon compte", "My account" } },
        { "acc_out",   new[] { "התנתקות", "Se déconnecter", "Sign out" } },
        { "login_later", new[] { "אחר כך", "Plus tard", "Later" } },
        { "login_wait", new[] { "נפתח דף של גוגל בדפדפן: בחרו את החשבון שלכם.", "Une page Google s'ouvre dans le navigateur : choisissez votre compte.", "A Google page opens in your browser: choose your account." } },
        { "login_fail", new[] { "החיבור לא הצליח. אפשר לנסות שוב.", "La connexion n'a pas abouti : vous pouvez réessayer.", "Sign-in did not work: you can try again." } },
        { "e_blocked", new[] { "הסינון של האינטרנט חסם את ההורדה של התוכנה (הדפדפן עצמו עובר). נסו שוב מתוך הדפדפן.", "Le filtre Internet a bloqué le téléchargement du programme (le navigateur, lui, passe) : réessayez depuis le navigateur.", "The Internet filter blocked the program's download (the browser itself gets through): retry from the browser." } },
        { "e_generic", new[] { "ההורדה נכשלה. אפשר לנסות שוב.", "Le téléchargement a échoué : vous pouvez réessayer.", "The download failed: you can retry." } },
        { "prepFail",  new[] { "ההכנה לא הושלמה, ללחוץ כדי לנסות שוב", "Préparation incomplète, cliquer pour réessayer", "Setup incomplete, click to retry" } },
    };
    public static string T(string k) { string[] v; return D.TryGetValue(k, out v) ? v[Lang] : k; }
    public static bool Rtl { get { return Lang == 0; } }

    public static void Init() {
        int i = Array.IndexOf(Codes, (Settings.Current.Lang ?? "").ToLowerInvariant());
        if (i >= 0) { Lang = i; return; }
        string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        i = Array.IndexOf(Codes, sys == "iw" ? "he" : sys);
        Lang = i >= 0 ? i : 2;
    }
}


// ---------- Signal quotidien (0.7.25) : compter les installations et pouvoir annoncer les mises a jour ----------
// Meme principe que Vmail Plus (API/vmail_ext_users.php) : une ligne par installation cote serveur (VMAIL.SHOWLEF_USERS).
// Contenu : identifiant aleatoire, version du programme, langue, version de Windows. Rien d'autre : depuis 0.7.53 plus aucune adresse
// lue dans Chrome (le nom et l'adresse viennent de la connexion Google, acceptee une fois). Au plus un envoi par jour ; desactivable seulement par settings.json ; jamais depuis un banc d'essai (--dir).
static class UsersPing {
    const string Url = "https://api.i-call.me/showlef_users.php";
    public static void Start() { new Thread(Loop) { IsBackground = true }.Start(); }
    static void Loop() {
        Thread.Sleep(90 * 1000);   // le programme finit de demarrer d'abord
        while (true) {
            try { Once(); } catch (System.Exception e) { App.Log("signal quotidien : " + e.Message); }
            Thread.Sleep(TimeSpan.FromHours(6));
        }
    }
    // « Copier les informations pour le support » : versions, etat des outils, dernieres lignes du journal et derniers
    // telechargements. Rien de personnel : pas de cookies, adresses reduites au site, chemin utilisateur masque.
    public static string SupportInfo() {
        var sb = new StringBuilder();
        string user = Environment.UserName;
        Func<string, string> clean = t => {
            t = System.Text.RegularExpressions.Regex.Replace(t ?? "", @"(https?://[^/\s?#]+)[^\s]*", "$1/…");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[A-Za-z]:\\Users\\[^\\\s]+", @"C:\Users\…");
            if (user.Length > 2) t = t.Replace(user, "…");
            return t;
        };
        sb.AppendLine("ShowLef " + App.Version + " - " + WindowsName() + " - langue " + Tr.Codes[Math.Max(0, Math.Min(2, Tr.Lang))] + " - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine("ffmpeg: " + (Tools.Ffmpeg != null ? "oui" : "non") + " ; yt-dlp: " + (Tools.Ytdlp != null ? "oui" : "non") + " ; format audio: " + Settings.Current.AudioFormat + " ; qualite: " + Settings.Current.Quality + " ; limite: " + Settings.Current.SpeedLimitKB + " Ko/s");
        try {
            var items = Mgr.Items.ToList().Take(10);
            sb.AppendLine("--- derniers telechargements ---");
            foreach (var it in items)
                sb.AppendLine(it.Status + " | " + it.Kind + " | " + Fmt.Size(it.Received) + "/" + Fmt.Size(it.Total) + " | " + clean(it.Url) + (string.IsNullOrEmpty(it.Error) ? "" : " | " + clean(it.Error)));
        } catch (System.Exception e) { sb.AppendLine("(liste indisponible : " + e.GetType().Name + " " + e.Message + ")"); }
        try {
            string f = Path.Combine(App.Dir, "showlef.log");
            if (File.Exists(f)) {
                var lines = File.ReadAllLines(f, Encoding.UTF8); sb.AppendLine("--- journal (40 dernieres lignes) ---");
                foreach (var l in lines.Skip(Math.Max(0, lines.Length - 40))) sb.AppendLine(clean(l));
                // la mesure lecture/remise de la voie navigateur sort du journal court des qu'on relance l'appli : on la redonne a part
                sb.AppendLine("--- dernieres captures navigateur (journal complet) ---");
                foreach (var l in lines.Where(x => x.Contains("capture navigateur :")).Reverse().Take(5).Reverse()) sb.AppendLine(clean(l));
            }
        } catch (System.Exception) { }
        return sb.ToString();
    }
    public static string InstallId() {
        string f = Path.Combine(App.Dir, "install.id");
        try { string t = File.Exists(f) ? File.ReadAllText(f).Trim() : ""; if (System.Text.RegularExpressions.Regex.IsMatch(t, "^[a-f0-9]{32}$")) return t; } catch (IOException) { }
        var b = new byte[16]; using (var r = new System.Security.Cryptography.RNGCryptoServiceProvider()) r.GetBytes(b);
        string id = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        File.WriteAllText(f, id);
        return id;
    }
    static string WindowsName() {
        try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) if (k != null) return "Windows build " + k.GetValue("CurrentBuild"); } catch (System.Exception) { }
        return "Windows";
    }
    // POST JSON ; rend la reponse lue (objet JSON) ou null si le serveur n'a pas repondu 200.
    public static Dictionary<string, object> PostJson(string url, Dictionary<string, object> body) {
        byte[] data = Encoding.UTF8.GetBytes(Json.Ser(body));
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
        req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 20000; req.ContentLength = data.Length;
        using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        try {
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return Json.Obj(rd.ReadToEnd());
        } catch (System.Net.WebException e) {
            var h = e.Response as System.Net.HttpWebResponse;
            if (h == null) return null;
            try { using (var rd = new StreamReader(h.GetResponseStream(), Encoding.UTF8)) return Json.Obj(rd.ReadToEnd()); } catch (System.Exception) { return null; }
        }
    }
    public static void Once() {
        if (!Settings.Current.PingUsers) return;
        if (!string.Equals(App.Dir, App.DefaultDir, StringComparison.OrdinalIgnoreCase)) return;   // bancs d'essai
        string stamp = Path.Combine(App.Dir, "users-ping.txt"), today = DateTime.Now.ToString("yyyy-MM-dd");
        try { if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == today) return; } catch (IOException) { }
        var body = new Dictionary<string, object> {
            { "install", InstallId() }, { "v", App.Version }, { "l", Tr.Lang == 0 ? "he" : Tr.Lang == 1 ? "fr" : "en" },
            { "os", WindowsName() } };   // plus aucune adresse lue dans Chrome : le nom et l'adresse viennent de la connexion Google, acceptee par l'utilisateur
        var r = PostJson(Url, body);
        if (r != null && Json.Bool(r, "ok")) { File.WriteAllText(stamp, today); App.Log("signal quotidien : envoye"); }
    }
}


// ---------- Mises a jour et messages (0.7.30) ----------
// Une verification toutes les 6 h (la 1re 2 min apres le demarrage) de deux petits fichiers du site :
//  - showlef-latest.json : derniere version + empreinte (deja publie a chaque version) ;
//  - showlef-notice.json : { "critical_below": "0.7.30", "notice": { "id": 3, "he": "...", "fr": "...", "en": "...", "url": "https://...", "min": "", "max": "" } }
// Niveaux : version plus ancienne que « critical_below » => mise a jour OBLIGATOIRE (fenetre qui ne se ferme pas sans mettre a jour ou quitter) ;
// sinon, simple conseil (lien discret en bas de la fenetre). « notice » : message de ShowF, montre UNE fois (par numero), dans la langue
// de l'utilisateur (hebreu, francais, sinon anglais). id <= 0 ou texte vide = rien a montrer.
static class Notices {
    public static volatile string UpdVersion, UpdSha, NoticeText, NoticeUrl;
    public static volatile bool UpdCritical;
    public static int NoticeId;
    const string NoticeFile = "https://ext.i-call.me/downloads/showlef-notice.json";
    public static void Start() { new Thread(Loop) { IsBackground = true }.Start(); }
    static void Loop() {
        Thread.Sleep(2 * 60 * 1000);
        while (true) {
            try { Once(); } catch (System.Exception e) { App.Log("messages/mises a jour : " + e.Message); }
            Thread.Sleep(TimeSpan.FromHours(2));
        }
    }
    static string Pick(Dictionary<string, object> o) {
        string t = Json.Str(o, Tr.Codes[Math.Max(0, Math.Min(2, Tr.Lang))]);
        return string.IsNullOrWhiteSpace(t) ? Json.Str(o, "en") : t;
    }
    static Dictionary<string, object> FetchNotice() {
        SelfUpdate.Tls();
        var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(NoticeFile + "?t=" + DateTime.UtcNow.Ticks);
        rq.UserAgent = Net.DefaultUA; rq.Timeout = 20000;
        try { using (var rs = (System.Net.HttpWebResponse)rq.GetResponse()) using (var r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return Json.Obj(r.ReadToEnd()); }
        catch (System.Net.WebException) { return null; }
    }
    static bool Ver(string v) { return System.Text.RegularExpressions.Regex.IsMatch(v ?? "", @"^\d+\.\d+\.\d+$"); }
    // A l'ouverture de la fenetre : on revérifie tout de suite (au plus une fois par 10 min), sans attendre le prochain sondage.
    static DateTime lastCheck = DateTime.MinValue;
    public static void CheckNow() {
        if (DateTime.UtcNow - lastCheck < TimeSpan.FromMinutes(10)) return;
        new Thread(() => { try { Once(); } catch (System.Exception e) { App.Log("messages/mises a jour : " + e.Message); } }) { IsBackground = true }.Start();
    }
    public static void Once() {
        if (!string.Equals(App.Dir, App.DefaultDir, StringComparison.OrdinalIgnoreCase)) return;   // bancs d'essai
        lastCheck = DateTime.UtcNow;
        var l = SelfUpdate.Latest(); string v = Json.Str(l, "version");
        var n = FetchNotice();
        string crit = n != null ? Json.Str(n, "critical_below") : null;
        if (SelfUpdate.Newer(v)) { UpdSha = Json.Str(l, "sha256"); UpdVersion = v; UpdCritical = Ver(crit) && SelfUpdate.Cmp(App.Version, crit) < 0; }
        else { UpdVersion = null; UpdCritical = false; }
        var no = n != null && n.ContainsKey("notice") ? n["notice"] as Dictionary<string, object> : null;
        if (no == null) return;
        int id; int.TryParse(Convert.ToString(no.ContainsKey("id") ? no["id"] : 0, System.Globalization.CultureInfo.InvariantCulture), out id);
        string min = Json.Str(no, "min"), max = Json.Str(no, "max"), text = Pick(no);
        if (id <= 0 || id <= Seen() || string.IsNullOrWhiteSpace(text)) return;
        if (Ver(min) && SelfUpdate.Cmp(App.Version, min) < 0) return;    // message reserve aux versions >= min
        if (Ver(max) && SelfUpdate.Cmp(App.Version, max) > 0) return;    // ... et <= max
        string url = Json.Str(no, "url");
        NoticeId = id; NoticeUrl = (url ?? "").StartsWith("https://") ? url : null; NoticeText = text;
    }
    // Mise a jour facultative : vrai UNE seule fois par version proposee (memorise dans upd-offered.txt).
    public static bool OfferNew() {
        string v = UpdVersion; if (v == null) return false;
        string f = Path.Combine(App.Dir, "upd-offered.txt");
        try { if (File.Exists(f) && File.ReadAllText(f).Trim() == v) return false; File.WriteAllText(f, v); } catch (System.Exception) { return false; }
        return true;
    }
    static int Seen() { try { int s; return int.TryParse(File.ReadAllText(Path.Combine(App.Dir, "notice-seen.txt")).Trim(), out s) ? s : 0; } catch (System.Exception) { return 0; } }
    public static void MarkSeen() { try { File.WriteAllText(Path.Combine(App.Dir, "notice-seen.txt"), NoticeId.ToString()); } catch (System.Exception) { } NoticeText = null; }
}
