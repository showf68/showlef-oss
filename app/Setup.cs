// ShowLef-Setup.exe : installe ShowLef pour l'utilisateur courant, sans droits administrateur, en un double-clic.
// Memes gestes que Setup-ShowLef.ps1 : programme + hote natif dans %LOCALAPPDATA%\ShowLef, enregistrement de l'hote
// pour Chrome / Edge / Brave / Chromium, raccourci du menu Demarrer, entree « Applications installees » de Windows.
// ShowLef.exe, showlef-host.exe et l'icone sont embarques dans ce fichier (ressources). « --uninstall » desinstalle,
// « --quiet » sans fenetre (bancs d'essai), « --lang he|fr|en » force la langue.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class SetupMain {
    public const string Version = "0.7.59";
    // Extension non empaquetee (cle fixe du manifeste) + version du Chrome Web Store (identifiant attribue le 19/10/2026).
    static readonly string[] ExtensionIds = { "mepcfiemlmpffknhaebjocpddbaahdfc", "hibookeofnmdllfleebckdkbgijpklfb" };   // non empaquetee, Chrome Web Store
    static readonly string[] Browsers = { @"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser", "Chromium" };
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShowLef";
    public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowLef"); } }
    static string Lnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ShowLef.lnk"); } }

    // Langue de Windows : hebreu, francais, sinon anglais.
    public static int L = 0;
    public static string T(string he, string fr, string en) { return L == 1 ? he : L == 2 ? fr : en; }
    public static bool Rtl { get { return L == 1; } }

    [STAThread]
    static int Main(string[] args) {
        string ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        L = ui == "he" || ui == "iw" ? 1 : ui == "fr" ? 2 : 0;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--lang") L = args[i + 1] == "he" ? 1 : args[i + 1] == "fr" ? 2 : 0;
        bool uninstall = args.Contains("--uninstall"), quiet = args.Contains("--quiet");
        DevArg = args.Contains("--dev");
        if (args.Contains("--guide")) {   // menu du programme « Ajouter l'extension a Chrome » : le guide seul, sans installer
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new SetupForm(false, true);
            Application.Run(f);
            return 0;
        }
        if (quiet) {
            try {
                if (uninstall) Uninstall();
                else {
                    Install(null);
                    // --restart : mise a jour demandee par le programme lui-meme => on le relance pres de l'horloge
                    if (args.Contains("--restart")) Process.Start(new ProcessStartInfo(Path.Combine(Dir, "ShowLef.exe"), "--tray") { UseShellExecute = true });
                }
                return 0;
            }
            catch (System.Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "showlef-setup-error.txt"), e.ToString()); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(uninstall));
        return 0;
    }

    static void StopRunning() {
        foreach (var n in new[] { "ShowLef", "showlef-host" })
            foreach (var p in Process.GetProcessesByName(n)) {
                try { p.Kill(); p.WaitForExit(5000); } catch (System.Exception) { /* deja termine */ }
            }
    }

    static byte[] Res(string name) {
        using (var s = typeof(SetupMain).Assembly.GetManifestResourceStream(name)) {
            if (s == null) throw new InvalidOperationException("ressource absente : " + name);
            var b = new byte[s.Length]; int o = 0;
            while (o < b.Length) { int n = s.Read(b, o, b.Length - o); if (n <= 0) break; o += n; }
            return b;
        }
    }

    // Ecriture avec reessais : un antivirus ou l'hote natif relance par Chrome peut tenir le fichier une seconde.
    static void Put(string path, byte[] data) {
        for (int i = 1; ; i++) {
            try { File.WriteAllBytes(path, data); return; }
            catch (IOException) { if (i >= 20) throw; StopRunning(); Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { if (i >= 20) throw; StopRunning(); Thread.Sleep(500); }
        }
    }

    public static void Install(Action<int> progress) {
        Action<int> step = progress ?? (x => { });
        Directory.CreateDirectory(Dir);
        StopRunning(); step(15);
        Put(Path.Combine(Dir, "ShowLef.exe"), Res("ShowLef.exe")); step(45);
        Put(Path.Combine(Dir, "showlef-host.exe"), Res("showlef-host.exe")); step(55);
        Put(Path.Combine(Dir, "ShowLef.ico"), Res("ShowLef.ico"));
        PutExtension();
        // garde de quoi se desinstaller : copie de cet installeur
        string self = Process.GetCurrentProcess().MainModule.FileName, uninst = Path.Combine(Dir, "ShowLef-Uninstall.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase)) Put(uninst, File.ReadAllBytes(self));
        step(65);

        // hote natif : on garde les identifiants deja autorises (ex. ajoutes a la main) et on ajoute les notres
        string json = Path.Combine(Dir, "com.showlef.app.json");
        var ids = new List<string>(ExtensionIds);
        if (File.Exists(json))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(json), "chrome-extension://([a-p]{32})/"))
                if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
        var sb = new StringBuilder();
        sb.Append("{\n  \"name\": \"com.showlef.app\",\n  \"description\": \"ShowLef - download manager\",\n");
        sb.Append("  \"path\": \"" + Path.Combine(Dir, "showlef-host.exe").Replace("\\", "\\\\") + "\",\n  \"type\": \"stdio\",\n  \"allowed_origins\": [");
        sb.Append(string.Join(", ", ids.Select(i => "\"chrome-extension://" + i + "/\"")));
        sb.Append("]\n}\n");
        File.WriteAllText(json, sb.ToString(), new UTF8Encoding(false));
        foreach (var b in Browsers)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + b + @"\NativeMessagingHosts\com.showlef.app")) k.SetValue("", json);
        step(80);
        AddChromeExtension();

        // raccourci du menu Demarrer (objet COM de Windows, sans reference a ajouter)
        try {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic s = sh.CreateShortcut(Lnk);
            s.TargetPath = Path.Combine(Dir, "ShowLef.exe");
            s.IconLocation = Path.Combine(Dir, "ShowLef.ico");
            s.Save();
        } catch (System.Exception) { /* sans raccourci le programme marche quand meme : l'extension le lance */ }

        // « Applications installees » de Windows : nom, version, editeur, et la desinstallation
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
            k.SetValue("DisplayName", "ShowLef");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "ICALL");
            k.SetValue("DisplayIcon", Path.Combine(Dir, "ShowLef.ico"));
            k.SetValue("InstallLocation", Dir);
            k.SetValue("URLInfoAbout", "https://ext.i-call.me/showlef/");
            k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall");
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long kb = 0; foreach (var f in Directory.GetFiles(Dir)) kb += new FileInfo(f).Length / 1024;
            k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
        }
        step(100);
    }

    // Retire le programme et ses enregistrements. Les fichiers telecharges (dossier Telechargements) restent.
    // Comme IDM : le programme AJOUTE l'extension a Chrome (cle Windows « external extension » vers le Chrome Web Store).
    // Au demarrage suivant, Chrome affiche « ShowLef a ete ajoutee » : un clic sur « Activer ». Pas de mode
    // developpeur, pas de visite du Store. Chrome sur Windows n'accepte ce chemin QUE pour une extension du Store
    // (update_url de Google) : d'ou l'identifiant Store. PC de developpement (extension non empaquetee deja chargee
    // dans un profil) : rien, sinon deux boutons ShowLef sur chaque video.
    const string StoreId = "hibookeofnmdllfleebckdkbgijpklfb", DevId = "mepcfiemlmpffknhaebjocpddbaahdfc";
    const string ChromeExtKey = @"Software\Google\Chrome\Extensions\" + StoreId;
    // (0.7.15, revue Codex) PC de developpement = choix EXPLICITE, garde pour les mises a jour suivantes :
    // `ShowLef-Setup.exe --dev` ecrit HKCU\Software\ShowLef\Dev=1. Repli pour les PC deja installes : extension de
    // developpement trouvee dans un profil Chrome => la marque est posee une fois pour toutes.
    const string OwnKey = @"Software\ShowLef";
    public static bool DevArg;
    static bool IsDevPc() {
        using (var k = Registry.CurrentUser.OpenSubKey(OwnKey)) if (k != null && Convert.ToString(k.GetValue("Dev")) == "1") return true;
        bool found = DevArg;
        string ud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data");
        if (!found && Directory.Exists(ud))
            foreach (var d in Directory.GetDirectories(ud))
                foreach (var f in new[] { "Secure Preferences", "Preferences" })
                    try { string p = Path.Combine(d, f); if (File.Exists(p) && File.ReadAllText(p).Contains("\"" + DevId + "\"")) found = true; }
                    catch (System.Exception) { /* profil illisible : on regarde les autres */ }
        if (found) using (var k = Registry.CurrentUser.CreateSubKey(OwnKey)) k.SetValue("Dev", "1");
        return found;
    }
    static void AddChromeExtension() {
        try {
            if (IsDevPc()) { Registry.CurrentUser.DeleteSubKeyTree(ChromeExtKey, false); return; }
            using (var k = Registry.CurrentUser.CreateSubKey(ChromeExtKey)) k.SetValue("update_url", "https://clients2.google.com/service/update2/crx");
        } catch (System.Exception e) {
            try { File.AppendAllText(Path.Combine(Dir, "setup.log"), DateTime.Now.ToString("s") + " extension Chrome : " + e.Message + "\r\n"); } catch (System.Exception) { }
        }
    }

    // ---------- (0.7.17) extension livree avec le programme, en attendant le Chrome Web Store ----------
    // Tant que Google n'a pas publie l'extension, Chrome ne trouve rien par la cle Windows (le service de mise a jour rend
    // 404). Le programme apporte donc l'extension elle-meme (dossier non empaquete, identifiant fixe par la cle du
    // manifeste) et guide l'utilisateur : chrome://extensions, « Mode developpeur », glisser le dossier. Chrome interdit a
    // un programme de faire ces gestes a sa place (mode developpeur, chargement, page chrome:// en ligne de commande :
    // mesure 21/10, ouvre un nouvel onglet vide). A chaque mise a jour du programme le dossier est remplace : Chrome relit
    // une extension non empaquetee a son demarrage.
    public static string ExtDir { get { return Path.Combine(Dir, "extension"); } }
    static void PutExtension() {
        byte[] zip;
        try { zip = Res("extension.zip"); } catch (InvalidOperationException) { return; }   // installeur sans extension (bancs)
        string tmp = ExtDir + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        using (var z = new System.IO.Compression.ZipArchive(new MemoryStream(zip))) System.IO.Compression.ZipFileExtensions.ExtractToDirectory(z, tmp);
        if (!File.Exists(Path.Combine(tmp, "manifest.json"))) throw new InvalidOperationException("extension incomplete");
        if (Directory.Exists(ExtDir)) Directory.Delete(ExtDir, true);
        Directory.Move(tmp, ExtDir);
    }
    // L'extension du Store est-elle publiee ? true / false (404 du service de mise a jour de Google) / null (inconnu)
    public static bool? StoreLive() {
        try {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var r = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://clients2.google.com/service/update2/crx?response=redirect&prodversion=130&acceptformat=crx3&x=id%3D" + StoreId + "%26uc");
            r.AllowAutoRedirect = false; r.Timeout = 6000;
            using (var resp = (System.Net.HttpWebResponse)r.GetResponse()) { int c = (int)resp.StatusCode; return c == 302 || c == 301 || c == 200 ? (bool?)true : null; }
        } catch (System.Net.WebException e) {
            var h = e.Response as System.Net.HttpWebResponse;
            if (h != null && (int)h.StatusCode == 404) return false;
            return null;
        } catch (System.Exception) { return null; }
    }
    // Une extension ShowLef (Store ou non empaquetee) est-elle dans un profil Chrome ?
    // Lu dans extensions.settings.<id> : une extension SUPPRIMEE y laisse une entree VIDE, et son identifiant reste
    // ailleurs dans le fichier (telemetrie, signatures). Mesure 21/10 chez ShowF : extension supprimee, simple
    // recherche du texte => « deja ajoutee », aucun guide affiche.
    public static bool ExtensionPresent() {
        return ExtensionPresence.Check() == ExtensionState.Present;
    }
    public static bool ExtensionPresent(string userData) {
        return ExtensionPresence.Check(userData) == ExtensionState.Present;
    }
    public static bool ConfirmConnection(string token) { return ConfirmConnection(token, "showlef-" + Environment.UserName.ToLowerInvariant()); }
    internal static bool ConfirmConnection(string token, string pipeName) {
        try {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                pipe.Connect(300);
                var bytes = Encoding.UTF8.GetBytes("{\"cmd\":\"extension_probe\",\"token\":\"" + token + "\"}\n");
                pipe.Write(bytes, 0, bytes.Length); pipe.Flush();
                var reply = new StreamReader(pipe, Encoding.UTF8).ReadLineAsync();
                if (!reply.Wait(1500)) return false;
                var r = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply.Result);
                return r != null && r.ContainsKey("connected") && true.Equals(r["connected"]) && r.ContainsKey("token") && token == Convert.ToString(r["token"]);
            }
        } catch (Exception) { return false; }
    }
    public static string ChromeExe() {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            using (var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                if (k != null) { string p = Convert.ToString(k.GetValue("")); if (File.Exists(p)) return p; }
        foreach (var p in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }) {
            string c = Path.Combine(p, @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static void Uninstall() {
        StopRunning();
        Registry.CurrentUser.DeleteSubKeyTree(ChromeExtKey, false);   // Chrome retire alors l'extension ajoutee par nous
        Registry.CurrentUser.DeleteSubKeyTree(OwnKey, false);
        foreach (var b in Browsers) Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + b + @"\NativeMessagingHosts\com.showlef.app", false);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue("ShowLef", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (File.Exists(Lnk)) File.Delete(Lnk);
        // desinstalle depuis Windows, cet exe vit dans le dossier : celui-ci s'efface juste apres notre sortie
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (self.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Dir + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        else if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }
}

// Fenetre unique : l'installation part toute seule, puis « Ouvrir ShowLef ».
class SetupForm : Form {
    readonly Func<bool> extensionPresent;
    readonly Label title = new Label(), text = new Label();
    readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 100 };
    readonly Button main = new Button(), close = new Button();
    static readonly Color Ink = Color.FromArgb(23, 26, 44), Ink2 = Color.FromArgb(74, 80, 112), Accent = Color.FromArgb(67, 56, 202);

    public SetupForm(bool uninstall) : this(uninstall, false) { }
    readonly bool guideOnly;
    volatile bool needGuide;
    public SetupForm(bool uninstall, bool guide) : this(uninstall, guide, NewConnectionCheck()) { }
    static Func<bool> NewConnectionCheck() {
        string token = Guid.NewGuid().ToString("N"); var born = DateTime.UtcNow;
        return () => { if ((DateTime.UtcNow - born).TotalSeconds >= 3) { token = Guid.NewGuid().ToString("N"); born = DateTime.UtcNow; } return SetupMain.ConfirmConnection(token); };
    }
    internal SetupForm(bool uninstall, bool guide, Func<bool> detectExtension) {
        extensionPresent = detectExtension;
        guideOnly = guide;
        Text = "ShowLef";
        try { using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("ShowLef.ico")) Icon = new Icon(s); } catch (ArgumentException) { }
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(460, 250);
        RightToLeft = SetupMain.Rtl ? RightToLeft.Yes : RightToLeft.No; RightToLeftLayout = SetupMain.Rtl;

        var logo = new PictureBox { Size = new Size(64, 64), Location = new Point(24, 24), SizeMode = PictureBoxSizeMode.Zoom };
        // logo en PNG (Icon.ToBitmap rend du bruit sur les images PNG d'un .ico)
        using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("logo.png")) if (s != null) using (var im = Image.FromStream(s)) logo.Image = new Bitmap(im);
        title.SetBounds(104, 26, 330, 32); title.Font = new Font("Segoe UI Semibold", 15f); title.ForeColor = Ink;
        text.SetBounds(104, 62, 330, 76); text.ForeColor = Ink2;
        bar.SetBounds(24, 150, 412, 8);
        main.SetBounds(256, 188, 180, 40); main.FlatStyle = FlatStyle.Flat; main.FlatAppearance.BorderSize = 0;
        main.BackColor = Accent; main.ForeColor = Color.White; main.Font = new Font("Segoe UI Semibold", 10.5f); main.Cursor = Cursors.Hand; main.Visible = false;
        close.SetBounds(140, 188, 104, 40); close.FlatStyle = FlatStyle.Flat; close.FlatAppearance.BorderColor = Color.FromArgb(227, 230, 240);
        close.BackColor = Color.White; close.ForeColor = Ink; close.Text = SetupMain.T("סגירה", "Fermer", "Close"); close.Visible = false;
        close.Click += (s, e) => Close();
        Controls.AddRange(new Control[] { logo, title, text, bar, main, close });

        if (uninstall) {
            title.Text = SetupMain.T("הסרת שולף", "Désinstaller ShowLef", "Uninstall ShowLef");
            text.Text = SetupMain.T("התוכנה תוסר מהמחשב. הקבצים שהורדתם נשארים במקומם.",
                                    "Le programme sera retiré de ce PC. Les fichiers que vous avez téléchargés restent à leur place.",
                                    "The program will be removed from this PC. The files you downloaded stay where they are.");
            bar.Visible = false;
            main.Text = SetupMain.T("הסרה", "Désinstaller", "Uninstall"); main.Visible = true; close.Visible = true;
            close.Text = SetupMain.T("ביטול", "Annuler", "Cancel");
            main.Click += (s, e) => {
                main.Enabled = false; close.Enabled = false;
                try { SetupMain.Uninstall(); text.Text = SetupMain.T("שולף הוסר.", "ShowLef a été désinstallé.", "ShowLef has been uninstalled."); }
                catch (System.Exception ex) { text.Text = ex.Message; }
                main.Visible = false; close.Text = SetupMain.T("סגירה", "Fermer", "Close"); close.Enabled = true;
            };
        } else if (guide) {
            bar.Visible = false;
            Shown += (s, e) => Guide();
        } else {
            title.Text = SetupMain.T("מתקינים את שולף…", "Installation de ShowLef…", "Installing ShowLef…");
            text.Text = SetupMain.T("רגע אחד, זה לוקח כמה שניות.", "Un instant, cela prend quelques secondes.", "One moment, this takes a few seconds.");
            Shown += (s, e) => new Thread(Run) { IsBackground = true }.Start();
        }
    }

    void Run() {
        string err = null;
        try { SetupMain.Install(p => BeginInvoke((Action)(() => bar.Value = p))); } catch (System.Exception ex) { err = ex.Message; }
        // hors de la fenetre : la question au Store peut prendre quelques secondes
        if (err == null) needGuide = SetupMain.StoreLive() != true && !SetupMain.ExtensionPresent() && Directory.Exists(SetupMain.ExtDir);
        BeginInvoke((Action)(() => Done(err)));
    }

    // ---------- Guide (0.7.21) : ajouter l'extension a Chrome tant que le Store ne l'a pas publiee ----------
    // Rien ne s'ouvre ni ne se copie tout seul (ShowF 21/10) : chaque geste est un bouton. Detection d'abord, puis
    // trois etapes lisibles ; le dossier se GLISSE depuis une tuile de cette fenetre (une seule chose, pas l'explorateur
    // avec tous les fichiers du programme) ou son chemin se copie. « Verifier » relit les profils Chrome a la demande,
    // et une veille le fait aussi toutes les secondes (animee a l'etape 3).
    readonly List<Control> guideCtl = new List<Control>();
    Label status;
    System.Windows.Forms.Timer guideTimer;

    Button Btn(string text, Rectangle r, bool primary) {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Bounds = r };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(205, 210, 230);
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.BackColor = primary ? Accent : Color.White; b.ForeColor = primary ? Color.White : Ink;
        b.Font = new Font("Segoe UI Semibold", 9.5f);
        guideCtl.Add(b); Controls.Add(b); return b;
    }
    Label Lbl(string text, Rectangle r, Font f, Color c) {
        var l = new Label { Text = text, Bounds = r, Font = f, ForeColor = c, AutoSize = false };
        guideCtl.Add(l); Controls.Add(l); return l;
    }
    void Guide() {
        // DETECTER D'ABORD : si l'extension est deja dans Chrome, on le dit tout de suite, sans gestes
        title.SetBounds(104, 26, 430, 32); text.SetBounds(104, 62, 430, 76);
        title.Text = SetupMain.T("בודקים את כרום…", "Vérification de Chrome…", "Checking Chrome…");
        text.Text = SetupMain.T("רגע, בודקים אם שולף כבר נמצאת בכרום.", "Un instant, je regarde si ShowLef est déjà dans Chrome.", "One moment, checking whether ShowLef is already in Chrome.");
        main.Visible = false; close.Visible = false;
        new Thread(() => {
            bool ok = false;
            try { ok = extensionPresent(); } catch (System.Exception) { }
            try { BeginInvoke((Action)(() => { if (ok) GuideDone(); else GuideSteps(); })); } catch (InvalidOperationException) { }
        }) { IsBackground = true }.Start();
    }
    // ----- Assistant a 3 ecrans (maquette V1 choisie par ShowF, 21/10) : un seul geste par ecran, grand, avec un dessin -----
    int gstep = 1;
    Label gTitle, gSub, gHint, gTile, gCount;
    Button gMain, gBack, gSkip, gCopy;
    Panel gPic, gDots;
    Panel gCheck;
    Label gDetail;
    GuideSpinner gSpinner;
    static readonly Color Green = Color.FromArgb(15, 157, 110), Amber = Color.FromArgb(245, 158, 11), Soft = Color.FromArgb(238, 240, 253), Line = Color.FromArgb(227, 230, 240);

    static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad) {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    static void Fill(Graphics g, Rectangle r, int rad, Color c, Color? border) {
        using (var p = Round(r, rad)) { using (var b = new SolidBrush(c)) g.FillPath(b, p); if (border.HasValue) using (var pen = new Pen(border.Value)) g.DrawPath(pen, p); }
    }

    void GuideSteps() {
        ClientSize = new Size(600, 560);
        logoHide();
        gstep = 1;
        gDots = new Panel { Bounds = new Rectangle(0, 18, 600, 14), BackColor = Color.White };
        gDots.Paint += (s, e) => {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (int i = 1; i <= 3; i++) Fill(e.Graphics, new Rectangle(300 - 63 + (i - 1) * 42, 3, 34, 6), 3, i < gstep ? Green : i == gstep ? Accent : Line, null);
        };
        gTitle = Lbl("", new Rectangle(24, 48, 552, 40), new Font("Segoe UI Semibold", 19f), Ink); gTitle.TextAlign = ContentAlignment.MiddleCenter;
        gSub = Lbl("", new Rectangle(50, 90, 500, 56), new Font("Segoe UI", 12f), Ink2); gSub.TextAlign = ContentAlignment.TopCenter;
        gPic = new Panel { Bounds = new Rectangle(90, 150, 420, 160), BackColor = Color.White };
        gPic.Paint += (s, e) => DrawPic(e.Graphics);
        gTile = new Label { Bounds = new Rectangle(60, 28, 300, 104), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.SizeAll, BackColor = Soft, ForeColor = Accent,
            Font = new Font("Segoe UI Semibold", 14f), Visible = false };
        gTile.Paint += (s, e) => { using (var pen = new Pen(Accent, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) e.Graphics.DrawRectangle(pen, 2, 2, gTile.Width - 5, gTile.Height - 5); };
        gTile.MouseDown += (s, e) => {
            if (e.Button != MouseButtons.Left) return;
            GuideAwait();
            gSpinner.Refresh(); status.Refresh();
            try { gTile.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { SetupMain.ExtDir }), DragDropEffects.Copy); } catch (System.Exception) { }
            GuideCheck(false);
        };
        gMain = Btn("", new Rectangle(180, 326, 240, 48), true); gMain.Font = new Font("Segoe UI Semibold", 12f);
        gMain.Click += (s, e) => GuideMain();
        gCopy = Btn(SetupMain.T("להוסיף בלי לגרור", "Ajouter sans glisser", "Add without dragging"), new Rectangle(200, 420, 200, 32), false);
        gCopy.Click += (s, e) => {
            GuideAwait();
            try { Clipboard.SetText(SetupMain.ExtDir); } catch (System.Exception) { }
            gHint.Text = SetupMain.T("הנתיב הועתק. בכרום: 1. „טעינת תוסף שנארז”  2. הדביקו (Ctrl+V) ולחצו Enter  3. „בחירת תיקייה”.",
                                     "Chemin copié. Dans Chrome : 1. « Charger l'extension non empaquetée »  2. collez (Ctrl+V), Entrée  3. « Sélectionner un dossier ».",
                                     "Path copied. In Chrome: 1. “Load unpacked”  2. paste (Ctrl+V), Enter  3. “Select Folder”.");
        };
        gHint = Lbl("", new Rectangle(60, 460, 480, 46), new Font("Segoe UI", 9.5f), Ink2); gHint.TextAlign = ContentAlignment.TopCenter;
        status = Lbl("", new Rectangle(50, 456, 526, 22), new Font("Segoe UI Semibold", 10f), Ink2); status.TextAlign = ContentAlignment.MiddleCenter;
        gSpinner = new GuideSpinner { Bounds = new Rectangle(24, 457, 20, 20), ForeColor = Accent };
        guideCtl.Add(gSpinner); Controls.Add(gSpinner);
        gCheck = new Panel { Bounds = new Rectangle(60, 324, 480, 84), BackColor = Color.White };
        gCheck.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; Fill(e.Graphics, new Rectangle(0, 0, gCheck.Width - 1, gCheck.Height - 1), 12, Soft, Line); };
        guideCtl.Add(gCheck); Controls.Add(gCheck); gCheck.SendToBack();
        gDetail = Lbl("", new Rectangle(116, 367, 278, 24), new Font("Segoe UI", 9.5f), Ink2);
        gBack = Btn(SetupMain.T("→ חזרה", "← Retour", "← Back"), new Rectangle(24, 518, 110, 34), false);
        gBack.Click += (s, e) => GuideGo(Math.Max(1, gstep - 1));
        gCount = Lbl("", new Rectangle(200, 525, 200, 20), new Font("Segoe UI", 9.5f), Ink2); gCount.TextAlign = ContentAlignment.MiddleCenter;
        gSkip = Btn(SetupMain.T("להמשיך אחר כך", "Terminer plus tard", "Finish later"), new Rectangle(416, 518, 160, 34), false);
        gSkip.Click += (s, e) => Close();   // reprise : icone ShowLef pres de l'horloge > « Ajouter l'extension a Chrome »
        gPic.Controls.Add(gTile);   // la tuile vit DANS le dessin : meme zone, jamais cachee par lui
        guideCtl.Add(gDots); guideCtl.Add(gPic);
        Controls.Add(gDots); Controls.Add(gPic);
        main.Visible = false; close.Visible = false;
        status.Text = SetupMain.T("שולף עדיין לא זוהתה בכרום.", "ShowLef n'est pas encore détectée dans Chrome.", "ShowLef is not detected in Chrome yet.");
        GuideGo(1);
        guideTimer = new System.Windows.Forms.Timer { Interval = 450 };
        guideTimer.Tick += (s, e) => GuideCheck(false);
        guideTimer.Start();
        if (StartPosition != FormStartPosition.Manual) CenterToScreen();
    }
    void logoHide() { foreach (Control c in Controls) if (c is PictureBox) c.Visible = false; title.Visible = false; text.Visible = false; }

    void GuideGo(int n) {
        gstep = n;
        gSpinner.Running = n == 3;
        gCheck.Visible = gDetail.Visible = n == 3;
        Control checkParent = n == 3 ? (Control)gCheck : this;
        checkParent.Controls.Add(gMain); checkParent.Controls.Add(status); checkParent.Controls.Add(gSpinner);
        gCheck.Controls.Add(gDetail);
        gMain.Bounds = n == 3 ? new Rectangle(348, 22, 114, 38) : new Rectangle(180, 326, 240, 48);
        gMain.Font = new Font("Segoe UI Semibold", n == 3 ? 10f : 12f);
        gMain.BackColor = n == 3 ? Color.White : Accent; gMain.ForeColor = n == 3 ? Accent : Color.White;
        gMain.FlatAppearance.BorderSize = n == 3 ? 1 : 0; gMain.FlatAppearance.BorderColor = Line;
        status.Bounds = n == 3 ? new Rectangle(56, 13, 278, 29) : new Rectangle(50, 456, 526, 22);
        status.TextAlign = n == 3 ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter;
        status.Font = new Font("Segoe UI Semibold", n == 3 ? 12f : 10f);
        status.BackColor = gDetail.BackColor = gSpinner.BackColor = n == 3 ? Soft : Color.White;
        gSpinner.Bounds = new Rectangle(18, 27, 26, 26);
        gDetail.Bounds = new Rectangle(56, 43, 278, 24);
        if (n == 3 && SetupMain.Rtl) {
            foreach (var control in new Control[] { gMain, status, gSpinner, gDetail }) control.Left = gCheck.Width - control.Right;
            status.TextAlign = ContentAlignment.MiddleRight; gDetail.TextAlign = ContentAlignment.TopRight;
        }
        gDots.Invalidate(); gPic.Invalidate();
        gTile.Visible = n == 3; gCopy.Visible = n == 3;
        gBack.Visible = n > 1;
        gCount.Text = SetupMain.T("צעד " + n + " מתוך 3", "Étape " + n + " sur 3", "Step " + n + " of 3");
        if (n == 1) {
            gTitle.Text = SetupMain.T("1. לפתוח את דף ההרחבות", "1. Ouvrir la page des extensions", "1. Open the extensions page");
            gSub.Text = SetupMain.T("לחיצה אחת, וכרום נפתח על הדף הנכון.", "Un clic, et Chrome s'ouvre directement sur la bonne page.", "One click, and Chrome opens right on the page.");
            gMain.Text = SetupMain.T("לפתוח את כרום", "Ouvrir Chrome", "Open Chrome"); gHint.Text = "";
        } else if (n == 2) {
            gTitle.Text = SetupMain.T("2. להפעיל „מצב מפתח”", "2. Activer « Mode développeur »", "2. Turn on “Developer mode”");
            gSub.Text = SetupMain.T("בדף שנפתח, בפינה הימנית העליונה, הזיזו את המתג.", "Dans la page qui vient de s'ouvrir, en haut à droite, glissez l'interrupteur.", "On the page that just opened, at the top right, flip the switch.");
            gMain.Text = SetupMain.T("עשיתי, הבא", "C'est fait, suivant", "Done, next"); gHint.Text = "";
        } else {
            gTitle.Text = SetupMain.T("3. לגרור את שולף לתוך הדף", "3. Glisser ShowLef dans la page", "3. Drag ShowLef into the page");
            gSub.Text = SetupMain.T("השאירו את שני החלונות גלויים. לחצו והחזיקו על התיקייה, גררו אותה לדף של כרום ושחררו.",
                                    "Gardez les deux fenêtres visibles. Maintenez le clic sur le dossier, glissez-le dans la page de Chrome, puis relâchez.",
                                    "Keep both windows visible. Hold the click on the folder, drag it into Chrome's page, then release.");
            gTile.Text = SetupMain.T("תיקיית שולף", "Dossier ShowLef", "ShowLef folder") + "\n" + SetupMain.T("(גררו אותי)", "(glissez-moi)", "(drag me)");
            gMain.Text = SetupMain.T("בדיקה", "Vérifier", "Check now");
            gHint.Text = SetupMain.T("הגרירה לא עובדת? לחצו על „להוסיף בלי לגרור”.", "Le glisser ne marche pas ? Cliquez sur « Ajouter sans glisser ».", "Dragging does not work? Click “Add without dragging”.");
            GuideAwait();
            GuideCheck(false);
        }
    }
    void GuideMain() {
        if (gstep == 1) { OpenChrome(null, null); GuideGo(2); }
        else if (gstep == 2) GuideGo(3);
        else GuideCheck(true);
    }

    void DrawPic(Graphics g) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Fill(g, new Rectangle(0, 0, 419, 159), 10, Soft, null);
        if (gstep == 3) return;
        using (var f = new Font("Segoe UI", 8f)) using (var fb = new Font("Segoe UI Semibold", 11f)) using (var ink2 = new SolidBrush(Ink2)) using (var ink = new SolidBrush(Ink)) {
            Fill(g, new Rectangle(40, 20, 340, 120), 8, Color.White, Color.FromArgb(205, 210, 230));
            if (gstep == 1) {
                Fill(g, new Rectangle(41, 21, 338, 24), 7, Color.FromArgb(232, 234, 246), null);
                Fill(g, new Rectangle(58, 27, 230, 12), 6, Color.White, null); g.DrawString("chrome://extensions", f, ink2, 68, 26);
                for (int i = 0; i < 3; i++) Fill(g, new Rectangle(58 + i * 108, 62, 96, 64), 6, Soft, null);
            } else {
                g.DrawString(SetupMain.T("הרחבות", "Extensions", "Extensions"), fb, ink, 56, 30);
                g.DrawString(SetupMain.T("מצב מפתח", "Mode développeur", "Developer mode"), f, ink2, 232, 34);
                Fill(g, new Rectangle(344, 31, 34, 18), 9, Accent, null);
                using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, 362, 33, 14, 14);
                using (var pen = new Pen(Amber, 3) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawEllipse(pen, 331, 20, 60, 40);
                for (int i = 0; i < 3; i++) Fill(g, new Rectangle(58 + i * 108, 70, 96, 56), 6, Soft, null);
            }
            // c'est un DESSIN, pas une commande : l'interrupteur a activer est celui de la vraie page de Chrome
            using (var cap = new Font("Segoe UI", 8.5f, FontStyle.Italic)) g.DrawString(SetupMain.T("תמונה להמחשה בלבד — מבצעים את הפעולה בכרום", "Simple repère : faites ce geste dans Chrome", "Illustration only — do this in Chrome"), cap, ink2, 40, 142);
        }
    }
    bool checking;
    void GuideAwait() {
        if (gSpinner != null) gSpinner.Running = gstep == 3;
        status.Text = SetupMain.T("מתחברים לכרום…", "Connexion à Chrome…", "Connecting to Chrome…");
        gDetail.Text = SetupMain.T("האישור יופיע מיד אחרי ההוספה.", "Confirmation automatique après l’ajout.", "Confirmed automatically after adding.");
    }
    void GuideCheck(bool byUser) {
        if (byUser) GuideAwait();
        if (checking) return;
        checking = true;
        new Thread(() => {
            bool ok = false;
            try { ok = extensionPresent(); } catch (System.Exception) { }
            try {
                BeginInvoke((Action)(() => {
                    checking = false;
                    if (IsDisposed) return;
                    if (ok) { if (guideTimer != null) guideTimer.Stop(); GuideDone(); }
                    else if (byUser) { status.Text = SetupMain.T("ממתינים לכרום…", "En attente de Chrome…", "Waiting for Chrome…"); gDetail.Text = SetupMain.T("הוסיפו את שולף או טענו אותו מחדש.", "Ajoutez ou rechargez ShowLef dans Chrome.", "Add or reload ShowLef in Chrome."); }
                }));
            } catch (InvalidOperationException) { }
        }) { IsBackground = true }.Start();
    }
    void GuideDone() {
        if (guideTimer != null) guideTimer.Stop();
        if (gSpinner != null) gSpinner.Running = false;
        foreach (var c in guideCtl) { Controls.Remove(c); c.Dispose(); }
        guideCtl.Clear();
        foreach (Control c in Controls) if (c is PictureBox) c.Visible = true;
        title.Visible = true; text.Visible = true;
        ClientSize = new Size(560, 250);
        title.SetBounds(104, 26, 430, 32); text.SetBounds(104, 62, 430, 76);
        title.Text = SetupMain.T("שולף נוסף לכרום ✓", "ShowLef est ajoutée à Chrome ✓", "ShowLef has been added to Chrome ✓");
        text.Text = SetupMain.T("התוסף ענה והתוכנה מחוברת לכרום. בסרטונים נתמכים, חפשו את כפתור „הורדה”. אפשר לסגור את החלון.",
                                "L’extension a répondu : ShowLef est connecté à Chrome. Sur les vidéos compatibles, cherchez « Télécharger ». Vous pouvez fermer cette fenêtre.",
                                "The extension replied: ShowLef is connected to Chrome. Look for “Download” on supported videos. You can close this window.");
        main.Visible = false;
        close.SetBounds(228, 188, 104, 40); close.Visible = true; close.Text = SetupMain.T("סגירה", "Fermer", "Close");
    }
    protected override void Dispose(bool disposing) {
        if (disposing && guideTimer != null) guideTimer.Dispose();
        base.Dispose(disposing);
    }
    // Ouvre Chrome et met l'adresse dans le presse-papiers : seulement sur clic. Chrome ne sait pas ouvrir une page
    // « chrome:// » passee en ligne de commande (mesure 21/10 : onglet vide), donc l'adresse se colle.
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    static bool ChromeInFront() {
        try { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return Process.GetProcessById((int)pid).ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase); }
        catch (System.Exception) { return false; }
    }
    // Chrome ouvre la page des extensions tout seul : l'adresse « chrome:// » passee en ligne de commande est remplacee par un
    // onglet vide (mesure 21/10, profil neuf), donc on ouvre une fenetre Chrome puis on tape l'adresse a sa place (Ctrl+L,
    // Ctrl+V, Entree) - seulement si Chrome est bien la fenetre au premier plan, sinon rien n'est envoye nulle part.
    void OpenChrome(object s, EventArgs e) {
        try { Clipboard.SetText("chrome://extensions"); } catch (System.Exception) { }
        string chrome = SetupMain.ChromeExe();
        if (chrome == null) {
            if (status != null) status.Text = SetupMain.T("שולף לא מצאה את Google Chrome. אם אפשר לפתוח אותו, פתחו אותו והדביקו (Ctrl+V) בשורת הכתובת.", "ShowLef n'a pas trouvé Google Chrome. Si vous pouvez l'ouvrir, ouvrez-le et collez (Ctrl+V) dans la barre d'adresse.", "ShowLef could not find Google Chrome. If you can open it, do so and paste (Ctrl+V) in the address bar.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(chrome, "--new-window") { UseShellExecute = true }); } catch (System.Exception) { }
        if (status != null) status.Text = SetupMain.T("פותחים את כרום…", "Ouverture de Chrome…", "Opening Chrome…");
        int tries = 0;
        var tk = new System.Windows.Forms.Timer { Interval = 400 };
        tk.Tick += (s2, e2) => {
            tries++;
            if (ChromeInFront()) {
                tk.Stop();
                try { SendKeys.SendWait("^l"); Thread.Sleep(250); SendKeys.SendWait("^v"); Thread.Sleep(150); SendKeys.SendWait("{ENTER}"); } catch (System.Exception) { }
                if (status != null) status.Text = SetupMain.T("דף ההרחבות נפתח.", "La page des extensions est ouverte.", "The extensions page is open.");
            } else if (tries >= 15) {
                tk.Stop();
                if (status != null) status.Text = SetupMain.T("בכרום: הדביקו (Ctrl+V) בשורת הכתובת ולחצו Enter.", "Dans Chrome : collez (Ctrl+V) dans la barre d'adresse, puis Entrée.", "In Chrome: paste (Ctrl+V) in the address bar, then press Enter.");
            }
        };
        tk.Start();
    }

    void Done(string err) {
        if (err != null) {
            title.Text = SetupMain.T("ההתקנה לא הצליחה", "L'installation n'a pas abouti", "Installation failed");
            text.Text = err; close.Visible = true; close.Text = SetupMain.T("סגירה", "Fermer", "Close");
            return;
        }
        bar.Visible = false;
        // extension pas encore sur le Store et absente de Chrome : le guide (3 gestes) plutot que « revenez dans Chrome »
        if (needGuide) {
            try { Process.Start(new ProcessStartInfo(Path.Combine(SetupMain.Dir, "ShowLef.exe"), "--tray") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
            Guide();
            return;
        }
        title.Text = SetupMain.T("שולף מותקן", "ShowLef est installé", "ShowLef is installed");
        text.Text = SetupMain.T("חזרו לכרום: התוסף מתחבר לבד, ובכל סרטון יופיע כפתור „הורדה”.",
                                "Revenez dans Chrome : l'extension se connecte toute seule, et un bouton « Télécharger » apparaît sur chaque vidéo.",
                                "Go back to Chrome: the extension connects by itself, and a “Download” button appears on every video.");
        main.Text = SetupMain.T("פתיחת שולף", "Ouvrir ShowLef", "Open ShowLef"); main.Visible = true; close.Visible = true;
        main.Click += (s, e) => {
            try { Process.Start(new ProcessStartInfo(Path.Combine(SetupMain.Dir, "ShowLef.exe")) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
            Close();
        };
        // le programme demarre deja en tache de fond (icone pres de l'horloge) : l'extension le trouve sans attendre
        try { Process.Start(new ProcessStartInfo(Path.Combine(SetupMain.Dir, "ShowLef.exe"), "--tray") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

// Visible pendant la veille de l'etape 3, y compris dans la boucle modale de DoDragDrop.
class GuideSpinner : Control {
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 25 };
    int angle;
    public bool Running { get { return animation.Enabled; } set { Visible = value; animation.Enabled = value; Invalidate(); } }
    public GuideSpinner() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animation.Tick += (s, e) => { angle = (angle + 6) % 360; Invalidate(); };
        Visible = false;
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 2, Width - 5, Height - 5);
        using (var p = new Pen(Color.FromArgb(227, 230, 240), 2)) e.Graphics.DrawEllipse(p, r);
        using (var p = new Pen(ForeColor, 2.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) e.Graphics.DrawArc(p, r, angle, 100);
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
