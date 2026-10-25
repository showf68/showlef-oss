// ShowLef - programme Windows (interface) : liste des telechargements, reglages, outils, icone pres de l'horloge.
// Compile par Build-ShowLef.ps1 (csc du .NET Framework, C# 5) avec Common.cs, Engine.cs, Ipc.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}

static class Theme {
    // memes valeurs que l'extension (popup.css) : indigo du logo
    public static readonly Color Accent = Color.FromArgb(79, 70, 229);
    public static readonly Color Accent2 = Color.FromArgb(124, 58, 237);
    public static readonly Color AccentSoft = Color.FromArgb(238, 240, 255);
    public static readonly Color Ok = Color.FromArgb(15, 138, 98);
    public static readonly Color Warn = Color.FromArgb(180, 83, 9);
    public static readonly Color Bad = Color.FromArgb(197, 48, 48);
    public static readonly Color Ink = Color.FromArgb(21, 26, 45);
    public static readonly Color Muted = Color.FromArgb(90, 99, 128);
    public static readonly Color Faint = Color.FromArgb(138, 145, 168);
    public static readonly Color Line = Color.FromArgb(225, 228, 238);
    public static readonly Color Sunk = Color.FromArgb(236, 238, 245);
    public static readonly Color Bg = Color.FromArgb(244, 245, 249);
    public static Font UI = new Font("Segoe UI", 9f);
    public static Font UIBold = new Font("Segoe UI Semibold", 9f);
    public static Font UIMedium = new Font(HasFamily("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI", 9.5f);
    static bool HasFamily(string n) { using (var f = new InstalledFontCollection()) return f.Families.Any(x => x.Name == n); }
    // Icone de l'application : app/ShowLef.ico (logo A, multi-tailles) embarquee dans l'exe.
    public static Icon AppIcon() { return AppIcon(32); }
    public static Icon AppIcon(int size) {
        try {
            using (var s = typeof(Theme).Assembly.GetManifestResourceStream("ShowLef.ico"))
                if (s != null) return new Icon(s, new Size(size, size));
        } catch { }
        try { return Icon.ExtractAssociatedIcon(App.ExePath); } catch { return SystemIcons.Application; }
    }

    // Menus (clic droit, icone pres de l'horloge) aux couleurs de v1 : fond blanc, survol indigo pale, Rubik.
    public static void Menu(ContextMenuStrip m) {
        m.Font = Fonts.R(13); m.ShowImageMargin = false; m.Padding = new Padding(St.P(4));
        m.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        m.ItemAdded += (s, e) => { if (!(e.Item is ToolStripSeparator)) { e.Item.Padding = new Padding(St.P(6), St.P(5), St.P(6), St.P(5)); e.Item.ForeColor = St.Ink; } };
    }
    class MenuColors : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground { get { return St.Surface; } }
        public override Color MenuBorder { get { return St.Line; } }
        public override Color MenuItemSelected { get { return St.AccentSoft; } }
        public override Color MenuItemBorder { get { return St.AccentSoft; } }
        public override Color SeparatorDark { get { return St.Line; } }
        public override Color SeparatorLight { get { return St.Surface; } }
        public override Color ImageMarginGradientBegin { get { return St.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return St.Surface; } }
        public override Color ImageMarginGradientEnd { get { return St.Surface; } }
    }

    public static void Dark(Form f) { int v = 0; try { Native.DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } catch { } }
    public static void Rtl(Control c) {
        c.RightToLeft = Tr.Rtl ? RightToLeft.Yes : RightToLeft.No;
        var f = c as Form; if (f != null) f.RightToLeftLayout = Tr.Rtl;
    }
}

static class Program {
    public static MainForm Main_;

    [STAThread]
    static void Main(string[] args) {
        try { Native.SetProcessDPIAware(); } catch { }
        // --dir <dossier> : donnees a part (bancs d'essai) ; --lang he|fr|en et --scale 1.25 : affichage force, non enregistre
        string lang = null; float scale = 0;
        for (int i = 0; i + 1 < args.Length; i++) {
            if (args[i] == "--dir") App.Dir = args[i + 1];
            else if (args[i] == "--lang") lang = args[i + 1];
            else if (args[i] == "--tools-base" && args.Contains("--dir")) { ToolsSetup.Base = args[i + 1].TrimEnd('/'); App.ToolsDirBench = Path.Combine(args[Array.IndexOf(args, "--dir") + 1], "tools"); }
            // banc du filtre HTTPS simule (tests/filtre-tls.mjs) : son autorite compte comme validee par Windows
            else if (args[i] == "--trust-ca" && args.Contains("--dir")) TlsTrust.BenchCa = new System.Security.Cryptography.X509Certificates.X509Certificate2(args[i + 1]);
            else if (args[i] == "--scale") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale);
        }
        using (var sg = Graphics.FromHwnd(IntPtr.Zero)) St.S = scale > 0 ? scale : sg.DpiX / 96f;
        // --shot <fichier.png> [--page n] : dessine la fenetre des reglages hors ecran et quitte (rendu verifie sans rien ouvrir)
        int shotAt = Array.IndexOf(args, "--shot");
        if (shotAt >= 0 && shotAt + 1 < args.Length && args.Contains("--dir")) {
            Directory.CreateDirectory(App.Dir);
            Settings.Load(); Tr.Init(); Account.Load();   // compte du dossier (bouton compte de la fenetre principale)
            if (lang != null) { int sl = Array.IndexOf(Tr.Codes, lang); if (sl >= 0) Tr.Lang = sl; }
            Fonts.Init();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int pg = 0; int pa = Array.IndexOf(args, "--page"); if (pa >= 0 && pa + 1 < args.Length) int.TryParse(args[pa + 1], out pg);
            string shotKind = args.Contains("--what") && Array.IndexOf(args, "--what") + 1 < args.Length ? args[Array.IndexOf(args, "--what") + 1] : "settings";
            if (!string.IsNullOrEmpty(Account.Email)) { Avatar.Refresh(); for (int w = 0; w < 60 && Avatar.Img == null; w++) System.Threading.Thread.Sleep(100); }   // photo du compte (Gravatar) avant la capture
            using (Form f = shotKind == "connection" ? (Form)new Form { Text = App.Name, ClientSize = new Size(St.P(1100), St.P(700)), Controls = { new Surface { Dock = DockStyle.Fill } } } : shotKind == "feedback" ? (Form)FeedbackForm.Demo(pg) : shotKind == "signin" ? (Form)SignInForm.Demo(pg) : shotKind == "account" ? (Form)new AccountPanel(() => { }) : shotKind == "playlist" ? (Form)new PlaylistForm("Best of Avraham Fried", new List<PlRow> {
                new PlRow { Id = "a", Idx = 1, Title = "Aleh Katan (Live)" }, new PlRow { Id = "b", Idx = 2, Title = "Don't Hide From Me", Known = true },
                new PlRow { Id = "c", Idx = 3, Title = "ניגון חסידי - צמח צדק (מתוך פרויקט צמאה)" }, new PlRow { Id = "d", Idx = 4, Title = "הנשמה יורדת", Known = true },
                new PlRow { Id = "e", Idx = 5, Title = "Kol Haolam Kulo" } }) : new SettingsForm()) {
                f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-32000, -32000);
                f.Show(); if (f is SettingsForm) ((SettingsForm)f).SelectPage(pg);
                if (shotKind == "connection") ((Surface)f.Controls[0]).ExtensionStatus.SetState(pg == 1 ? ExtensionState.Present : ExtensionState.Missing, pg == 1);
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size)); bmp.Save(args[shotAt + 1], ImageFormat.Png); }
            }
            return;
        }
        Directory.CreateDirectory(App.Dir);
        bool created;
        var mutex = new Mutex(true, "Local\\showlef-" + Environment.UserName.ToLowerInvariant(), out created);
        string url = args.FirstOrDefault(a => a.StartsWith("http://") || a.StartsWith("https://"));
        if (!created) {
            // deja lance : on lui passe la main
            try {
                if (url != null) Ipc.Send(Json.Ser(new Dictionary<string, object> { { "cmd", "add" }, { "url", url }, { "kind", "auto" }, { "ask", true } }), 3000);
                else Ipc.Send("{\"cmd\":\"show\"}", 3000);
            } catch { }
            return;
        }
        Settings.Load();
        Tr.Init();
        if (lang != null) { int li = Array.IndexOf(Tr.Codes, lang); if (li >= 0) Tr.Lang = li; }
        Fonts.Init();
        Mgr.Load();
        Account.Load();
        Gate.Force = args.Contains("--gate");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Main_ = new MainForm(args.Contains("--tray"));
        Ipc.OnShow = () => Main_.BeginInvoke((Action)(() => Main_.ShowMain()));
        Gate.OnAsk = () => Main_.BeginInvoke((Action)(() => Main_.ShowSignIn()));
        Avatar.Changed += () => { try { Main_.BeginInvoke((Action)(() => Main_.view.Invalidate())); } catch (System.Exception) { } };
        Avatar.Refresh();
        Playlists.OnPick = (name, rows) => (List<string>)Main_.Invoke((Func<List<string>>)(() => { using (var pf = new PlaylistForm(name, rows)) return pf.ShowDialog() == DialogResult.OK ? pf.Selected : null; }));
        Ipc.OnPickFolder = site => (string)Main_.Invoke((Func<string>)(() => Main_.PickFolder(site)));
        Ipc.OnDuplicate = it => Main_.BeginInvoke((Action)(() => Main_.ShowDuplicate(it)));
        Ipc.OnOpen = what => Main_.BeginInvoke((Action)(() => Main_.OpenDialog(what)));
        Ipc.OnAddAsk = m => (string)Main_.Invoke((Func<string>)(() => Main_.AskAdd(m)));
        Ipc.Start();
        UsersPing.Start();
        Notices.Start();
        Mgr.Pump();
        // Plug and play : outils manquants (ou yt-dlp vieilli) installes tout seuls, sans rien demander.
        if (ToolsSetup.Needed()) ToolsSetup.Start(false, null, null);
        if (url != null) Main_.Load += (s, e) => Main_.BeginInvoke((Action)(() => Main_.AskAdd(new Dictionary<string, object> { { "url", url }, { "kind", "auto" } })));
        Application.Run(Main_);
        Mgr.Save();
        GC.KeepAlive(mutex);
    }
}

// Fenetre principale « Studio clair » (maquette v1) : toute la surface est peinte par Surface (Studio.cs).
class MainForm : Form {
    internal readonly Surface view = new Surface();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    bool startHidden, quitting;
    bool checkingExtension;
    ExtensionState extensionState = ExtensionState.Unknown;
    bool? lastConnected;
    ExtensionState? lastReportedState;
    DateTime connectionConfirmed = DateTime.MinValue;
    int tick;
    string lastClip = "", clipCandidate;
    Item lastDone;   // derniere notification « termine » : un clic dessus lit le fichier
    // toast « Supprime - Annuler » (non bloquant, 10 s)
    readonly System.Windows.Forms.Timer undoTimer = new System.Windows.Forms.Timer { Interval = 10000 };
    List<string> undoIds = new List<string>();

    public MainForm(bool hidden) {
        startHidden = hidden;
        Icon = Theme.AppIcon();
        Font = Fonts.R(13);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        // taille de depart bornee a l'ecran (a 150-175 % une hauteur fixe deborderait d'un ecran de 1080 px)
        var wa = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(St.P(1180), (int)(wa.Width * 0.92)), Math.Min(St.P(760), (int)(wa.Height * 0.9)));
        MinimumSize = new Size(Math.Min(St.P(860), wa.Width), Math.Min(St.P(520), wa.Height));
        AllowDrop = true;
        // la fenetre principale reste de gauche a droite : la surface fait son miroir elle-meme (sinon images inversees, clics decales)
        RightToLeft = RightToLeft.No; RightToLeftLayout = false;

        view.Dock = DockStyle.Fill;
        view.AllowDrop = true;
        view.OnAction = OnViewAction;
        foreach (var m in new[] { menu, trayMenu }) Theme.Menu(m);
        view.OnContext = p => menu.Show(view, p);
        menu.Opening += (s, e) => {
            var sel = view.Selected();
            bool any = sel.Count > 0;
            for (int i = 0; i < 12 && i < menu.Items.Count; i++) menu.Items[i].Enabled = any;
            if (any && menu.Items.Count > 8) {
                menu.Items[0].Enabled = sel.Any(i => i.Status == "done");
                menu.Items[3].Enabled = sel.Any(i => i.Status == "paused" || i.Status == "error");
                menu.Items[4].Enabled = sel.Any(i => i.Status == "downloading" || i.Status == "queued");
                menu.Items[8].Enabled = sel.Any(i => i.Status == "done");
            }
        };
        Controls.Add(view);

        tray.Icon = Icon; tray.Text = App.Name; tray.Visible = true;
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (s, e) => ShowMain();
        tray.BalloonTipClicked += (s, e) => {
            if (updOfferClick) { updOfferClick = false; CheckUpdate(); return; }
            if (clipCandidate != null) { string u = clipCandidate; clipCandidate = null; AskAdd(new Dictionary<string, object> { { "url", u }, { "kind", "auto" } }); }
            else if (lastDone != null) { var d = lastDone; lastDone = null; Act(Ui.IsMedia(d) ? "open" : "folder", new List<Item> { d }); }
            else ShowMain();
        };

        DragEventHandler enter = (s, e) => { if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent("UniformResourceLocatorW")) e.Effect = DragDropEffects.Copy; };
        DragEventHandler drop = (s, e) => {
            string u = (e.Data.GetData(DataFormats.Text) as string ?? "").Trim();
            if (u.StartsWith("http")) AskAdd(new Dictionary<string, object> { { "url", u.Split('\n')[0].Trim() }, { "kind", "auto" } });
        };
        DragEnter += enter; DragDrop += drop; view.DragEnter += enter; view.DragDrop += drop;

        Mgr.Finished += it => { try { BeginInvoke((Action)(() => Notify(it))); } catch { } };
        timer.Tick += (s, e) => OnTick();
        timer.Start();
        undoTimer.Tick += (s, e) => HideUndo();
        ApplyTexts();
        SyncStartup();
    }

    protected override void SetVisibleCore(bool value) {
        if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Dark(this); }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        if (!startHidden) { if (!Gate.Open) BeginInvoke((Action)(() => ShowSignIn())); CheckExtensionOnOpen(); }
    }

    // Menu du compte : nom + adresse du compte connecte, deconnexion (la fenetre d'activation revient aussitot :
    // c'est aussi la facon de changer de compte). Pas connecte : la fenetre d'activation directement.
    // Panneau du compte sous le bouton rond (photo Gravatar ou initiale, nom, adresse, se deconnecter). Se deconnecter
    // rouvre aussitot la fenetre d'activation (= changer de compte). Pas connecte : la fenetre d'activation directement.
    void ShowAccountMenu() {
        if (!Gate.Open || string.IsNullOrEmpty(Account.Name)) { ShowSignIn(); view.Invalidate(); return; }
        var f = new AccountPanel(() => {
            App.Log("compte : deconnexion");
            Account.Forget(); Avatar.Refresh(); view.Invalidate();
            BeginInvoke((Action)(() => { ShowSignIn(); view.Invalidate(); }));
        });
        var btn = view.RectangleToScreen(view.AccountRect);
        int x = Tr.Rtl ? btn.X : btn.Right - f.Width;
        f.Location = new Point(x, btn.Bottom + St.P(8));
        f.FormClosed += (o, e) => BeginInvoke((Action)(() => f.Dispose()));
        f.Show(this);
    }

    // Connexion Google (une fois). Une seule fenetre a la fois ; true si le compte est connecte.
    bool signInOpen;
    public bool ShowSignIn() {
        if (Gate.Open) return true;
        if (signInOpen) return false;
        signInOpen = true;
        try {
            using (var f = new SignInForm()) {
                bool hidden = !Visible;
                if (hidden) { f.StartPosition = FormStartPosition.CenterScreen; f.TopMost = true; }
                bool ok = f.ShowDialog(hidden ? null : this) == DialogResult.OK;
                if (ok) { Avatar.Refresh(); view.Invalidate(); }
                return ok;
            }
        } finally { signInOpen = false; }
    }

    protected override void OnActivated(EventArgs e) {
        base.OnActivated(e);
        CheckExtensionOnOpen();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!quitting && Settings.Current.CloseToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        tray.Visible = false;
        foreach (var it in Mgr.Items.ToList()) if (it.Status == "downloading" || it.Status == "merging") { Mgr.Pause(it); it.Status = "queued"; }
        Mgr.Save();
        base.OnFormClosing(e);
    }

    public void ShowMain(bool checkExtension = true) {
        startHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.SetForegroundWindow(Handle);
        if (checkExtension) CheckExtensionOnOpen();
        Notices.CheckNow();
    }

    void CheckExtensionOnOpen() {
        if (checkingExtension || IsDisposed || !IsHandleCreated || !Visible) return;
        checkingExtension = true;
        new Thread(() => {
            bool connected = ExtensionProbe.Check(Guid.NewGuid().ToString("N"), 600);
            var state = ExtensionPresence.Check();
            try { BeginInvoke((Action)(() => {
                checkingExtension = false;
                if (IsDisposed) return;
                extensionState = state;
                connectionConfirmed = connected ? DateTime.UtcNow : DateTime.MinValue;
                UpdateExtensionStatus();
            })); } catch (InvalidOperationException) { checkingExtension = false; }
        }) { IsBackground = true, Name = "extension-check" }.Start();
    }

    void UpdateExtensionStatus() {
        // Les anciennes extensions repondent par ping/attente, les recentes confirment aussi avant l'ecriture des preferences.
        bool connected = (DateTime.UtcNow - connectionConfirmed).TotalSeconds < 6 || (extensionState == ExtensionState.Present && Ipc.ExtensionConnected);
        view.ExtensionStatus.SetState(extensionState, connected);
        if (lastConnected != connected || lastReportedState != extensionState) {
            lastConnected = connected;
            lastReportedState = extensionState;
            App.Log("extension : indicateur sidebar " + (connected ? "connecte" : "deconnecte") + " (" + extensionState + ")");
        }
    }

    void OpenExtensionGuide() {
        string guide = Path.Combine(App.Dir, "ShowLef-Uninstall.exe");
        try {
            if (File.Exists(guide)) Process.Start(new ProcessStartInfo(guide, "--guide") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("https://ext.i-call.me/" + (Tr.Lang == 0 ? "he/" : Tr.Lang == 1 ? "fr/" : "") + "showlef/") { UseShellExecute = true });
        } catch (Exception ex) { App.Log("guide : " + ex.Message); }
    }

    public void OpenDialog(string what) {
        ShowMain(false);
        if (what == "settings") OpenSettings();
        else if (what == "tools") new ToolsForm().ShowDialog(this);
        else if (what == "add") AskAdd(null);
    }
    void OpenSettings() { if (new SettingsForm().ShowDialog(this) == DialogResult.OK) { ApplyTexts(); SyncStartup(); Mgr.Pump(); } }

    void Quit() { quitting = true; Close(); Application.Exit(); }

    public void ApplyTexts() {
        Text = Tr.T("appName");
        view.Cue();

        menu.Items.Clear();
        menu.Items.Add(Tr.T("open"), null, (s, e) => Act("open", view.Selected()));
        menu.Items.Add(Tr.T("folder"), null, (s, e) => Act("folder", view.Selected()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr.T("resume"), null, (s, e) => Act("resume", view.Selected()));
        menu.Items.Add(Tr.T("pause"), null, (s, e) => Act("pause", view.Selected()));
        menu.Items.Add(Tr.T("retry"), null, (s, e) => Act("retry", view.Selected()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr.T("copyUrl"), null, (s, e) => Act("copy", view.Selected()));
        menu.Items.Add(Tr.T("clip"), null, (s, e) => Act("clip", view.Selected()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr.T("remove"), null, (s, e) => Act("remove", view.Selected()));
        menu.Items.Add(Tr.T("trash"), null, (s, e) => Delete(view.Selected()));
        menu.Items.Add(new ToolStripSeparator());
        // actions de groupe (l'ancienne barre d'outils) : ici, et dans le menu de l'icone pres de l'horloge
        menu.Items.Add(Tr.T("resumeAll"), null, (s, e) => ResumeAll());
        menu.Items.Add(Tr.T("pauseAll"), null, (s, e) => PauseAll());
        menu.Items.Add(Tr.T("clearDone"), null, (s, e) => ClearDone());
        menu.Items.Add(Tr.T("tools"), null, (s, e) => new ToolsForm().ShowDialog(this));
        Theme.Rtl(menu);

        trayMenu.Items.Clear();
        trayMenu.Items.Add(Tr.T("showWin"), null, (s, e) => ShowMain());
        trayMenu.Items.Add(Tr.T("add"), null, (s, e) => AskAdd(null));
        trayMenu.Items.Add(Tr.T("tools"), null, (s, e) => OpenDialog("tools"));
        trayMenu.Items.Add(Tr.T("feedbackLink"), null, (s, e) => { using (var f = new FeedbackForm()) f.ShowDialog(); });
        trayMenu.Items.Add(Tr.T("lastFolder"), null, (s, e) => {
            Item d; lock (Mgr.Items) d = Mgr.Items.Where(i => i.Status == "done").OrderByDescending(i => i.Finished).FirstOrDefault();
            if (d != null) Ipc.OpenFolder(d); else try { Process.Start("explorer.exe", "\"" + Settings.Current.Folder + "\""); } catch { }
        });
        trayMenu.Items.Add(Tr.T("addClip"), null, (s, e) => { string u = ClipUrl(); AskAdd(u != null ? new Dictionary<string, object> { { "url", u }, { "kind", "auto" } } : null); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(Tr.T("pauseAll"), null, (s, e) => PauseAll());
        trayMenu.Items.Add(Tr.T("resumeAll"), null, (s, e) => ResumeAll());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(Tr.T("checkUpd"), null, (s, e) => CheckUpdate());
        // (0.7.17) tant que l'extension n'est pas sur le Store : le guide de l'installeur (meme fichier, --guide)
        trayMenu.Items.Add(Tr.T("addExt"), null, (s, e) => OpenExtensionGuide());
        trayMenu.Items.Add(Tr.T("quit"), null, (s, e) => Quit());
        Theme.Rtl(trayMenu);
        OnTick();
    }
    // (0.7.15, revue Codex) mise a jour SANS l'extension : si elle est desactivee ou retiree, le programme doit
    // pouvoir se reparer seul. Derniere version lue sur notre site, meme verification d'empreinte que pour l'extension.
    // Bouton « Rechercher une mise a jour » DANS la fenetre (0.7.43) : reverifie tout de suite et repond dans la barre d'etat
    // (« a jour » 6 s, ou le lien « Nouvelle version X · cliquer pour mettre a jour » qui lance la mise a jour).
    string updMsg; DateTime updMsgUntil; bool updChecking;
    void CheckUpdateInWindow() {
        if (updChecking) return;
        updChecking = true; updMsg = Tr.T("updChecking"); updMsgUntil = DateTime.MaxValue; OnTick();
        new Thread(() => {
            string msg;
            try { Notices.Once(); msg = Notices.UpdVersion != null ? null : Tr.T("updNone") + " (" + App.Version + ")"; }
            catch (System.Exception ex) { msg = Tr.T("updFail") + " : " + ex.Message; }
            try { BeginInvoke((Action)(() => { updChecking = false; updMsg = msg; updMsgUntil = DateTime.Now.AddSeconds(6); OnTick(); })); } catch { }
        }) { IsBackground = true }.Start();
    }
    void CheckUpdate() {
        new Thread(() => {
            string msg; ToolTipIcon icon = ToolTipIcon.Info;
            try {
                var l = SelfUpdate.Latest();
                string v = Json.Str(l, "version"), sha = Json.Str(l, "sha256");
                if (!SelfUpdate.Newer(v)) msg = Tr.T("updNone") + " (" + App.Version + ")";
                else {
                    var r = SelfUpdate.Run(sha, v);
                    if (Json.Bool(r, "ok")) msg = Tr.T("updGo") + " " + v;
                    else { icon = ToolTipIcon.Warning; msg = Json.Str(r, "error") == "busy" ? Tr.T("updBusy") : Tr.T("updFail") + " : " + Json.Str(r, "error"); }
                }
            } catch (System.Exception ex) { icon = ToolTipIcon.Warning; msg = Tr.T("updFail") + " : " + ex.Message; }
            try { BeginInvoke((Action)(() => tray.ShowBalloonTip(6000, App.Name, msg, icon))); } catch { }
        }) { IsBackground = true }.Start();
    }
    void ResumeAll() { foreach (var it in Mgr.Items.ToList()) Mgr.Resume(it); OnTick(); }
    void PauseAll() { foreach (var it in Mgr.Items.ToList()) Mgr.Pause(it); OnTick(); }
    void ClearDone() { foreach (var it in Mgr.Items.Where(i => i.Status == "done").ToList()) Mgr.Remove(it, false); OnTick(); }

    // Clics de la surface : boutons de ligne (sur CET element, pas sur la selection), bande, menu, toast, barre d'etat.
    void OnViewAction(string key, Item it) {
        var one = it != null ? new List<Item> { it } : view.Selected();
        switch (key) {
            case "extension": if (view.ExtensionStatus.Connected) CheckExtensionOnOpen(); else OpenExtensionGuide(); break;
            case "add": AskAdd(null); break;
            case "gear": OpenSettings(); break;
            case "account": ShowAccountMenu(); break;
            case "folderCard": try { Directory.CreateDirectory(Settings.Current.Folder); Process.Start("explorer.exe", "\"" + Settings.Current.Folder + "\""); } catch (System.Exception e) { App.Log("dossier : " + e.Message); } break;
            case "open": case "act:play": Act("open", one); break;
            case "act:folder": Act("folder", one); break;
            case "act:trash": case "delete": case "act:cancel": case "band:cancel": Delete(one); break;
            case "act:retry": Act("retry", one); break;
            case "act:pause": case "band:pause": Act("pause", one); break;
            case "paste": { string u = ClipUrl(); AskAdd(u != null ? new Dictionary<string, object> { { "url", u }, { "kind", "auto" } } : null); break; }
            case "feedback": using (var f = new FeedbackForm()) f.ShowDialog(this); break;
            case "act:resume": Act("resume", one); break;
            case "act:next": foreach (var q in one) Mgr.StartNext(q); OnTick(); break;
            case "act:now": foreach (var q in one) Mgr.StartNow(q); OnTick(); break;
            case "st:update": CheckUpdate(); break;
            case "st:check": CheckUpdateInWindow(); break;
            case "st:notice": if (Notices.NoticeText != null) new NoticeForm().Show(this); break;
            case "st:limit": Settings.Current.SpeedLimitKB = Settings.Current.SpeedLimitKB > 0 ? 0 : Settings.EcoKB; Settings.Save(); OnTick(); break;
            case "toast:undo": UndoDelete(); break;
            case "st:held": Mgr.Held = false; Mgr.Pump(); OnTick(); break;
            case "st:tools":
                if (!ToolsSetup.Busy && (Tools.Ffmpeg == null || Tools.Ytdlp == null)) ToolsSetup.Start(false, null, null);
                else new ToolsForm().ShowDialog(this);
                break;
        }
    }

    void Act(string a, List<Item> sel) {
        foreach (var it in sel) {
            try {
                switch (a) {
                    case "open": if (File.Exists(it.FilePath)) Process.Start(it.FilePath); else Ipc.OpenFolder(it); break;
                    case "folder": Ipc.OpenFolder(it); break;
                    case "resume": Mgr.Resume(it); break;
                    case "pause": Mgr.Pause(it); break;
                    case "retry": if (it.Status != "downloading") { it.Segs = null; it.Received = 0; it.Total = 0; it.Status = "paused"; Mgr.Resume(it); } break;
                    case "copy": Clipboard.SetText(it.Url); break;
                    case "clip": if (it.Status == "done" && File.Exists(it.FilePath)) AskClip(it); break;
                    case "remove": Mgr.Remove(it, false); break;
                }
            } catch (System.Exception e) { App.Log("action " + a + " : " + e.Message); }
            if (a == "open" || a == "copy" || a == "clip") break;
        }
        OnTick();
    }

    // Supprimer : fichier vers la corbeille, ligne retiree, toast « Supprime - Annuler » (jamais de confirmation).
    void Delete(List<Item> sel) {
        if (sel.Count == 0) return;
        var ids = new List<string>(); string last = null; int n = 0;
        foreach (var it in sel) {
            try { if (Mgr.Trash(it)) ids.Add(it.Id); last = it.Name.TrimStart('~'); n++; }
            catch (System.Exception e) { App.Log("corbeille " + it.FilePath + " : " + e.Message); MessageBoxEx(e.Message); }
        }
        OnTick();
        if (n == 0) return;
        undoIds = ids;
        view.ToastText = n == 1 ? Tr.T("deleted") + " " + last : string.Format(Tr.T("deletedN"), n);
        view.ToastUndo = ids.Count > 0;
        view.Invalidate();
        undoTimer.Stop(); undoTimer.Start();
    }
    void HideUndo() { undoTimer.Stop(); view.ToastText = null; view.Invalidate(); undoIds = new List<string>(); }
    void UndoDelete() {
        bool fail = false;
        foreach (var id in undoIds) if (!Mgr.Undo(id)) fail = true;
        HideUndo();
        OnTick();
        if (fail) tray.ShowBalloonTip(5000, App.Name, Tr.T("undoFail"), ToolTipIcon.Warning);
    }

    // Choix du dossier d'un site, demande depuis l'extension : fenetre au premier plan meme si ShowLef est cache.
    public string PickFolder(string site) {
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
        using (var d = new FolderBrowserDialog()) {
            owner.Show();
            Native.SetForegroundWindow(owner.Handle);
            string cur; Settings.Current.SiteFolders.TryGetValue(site, out cur);
            d.SelectedPath = !string.IsNullOrEmpty(cur) ? cur : Settings.Current.Folder;
            d.Description = Tr.T("siteFolderFor") + " " + site;
            d.ShowNewFolderButton = true;
            return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
        }
    }

    public void ShowDuplicate(Item it) {
        tray.ShowBalloonTip(4000, Tr.T("n_dup"), it.Name, ToolTipIcon.Info);
        if (Visible) { OnTick(); view.SelectOnly(it.Id); }
    }

    // « mm:ss » ou « h:mm:ss » -> secondes ; -1 si illisible
    static double ParseTime(string t) {
        double total = 0;
        foreach (var part in (t ?? "").Trim().Split(':')) {
            double v;
            if (!double.TryParse(part.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || v < 0) return -1;
            total = total * 60 + v;
        }
        return total;
    }

    void AskClip(Item it) {
        using (var f = new ClipForm()) {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            double a = ParseTime(f.From), b = ParseTime(f.To);
            if (a < 0 || b <= a) { tray.ShowBalloonTip(3000, App.Name, Tr.T("clip") + " : " + f.From + " - " + f.To, ToolTipIcon.Warning); return; }
            Clip.Create(it, a, b);
        }
    }

    void OnTick() {
        if (Visible) { if (tick % 6 == 0) CheckExtensionOnOpen(); UpdateExtensionStatus(); }
        if (++tick % 2 == 0) { Mgr.Tick(); if (tick % 20 == 0) Mgr.Save(); }
        List<Item> items;
        lock (Mgr.Items) items = Mgr.Items.ToList();
        view.SetData(items);

        int active = items.Count(Ui.Active);
        double sp = items.Sum(i => i.Speed);
        view.StatusLeft = active > 0 ? string.Format(Tr.T("stActive"), active) + "   ·   " + Fmt.Size((long)sp) + Tr.T("perSec") : string.Format(Tr.T("stTotal"), items.Count);
        // a droite : preparation des outils (plug and play), echec cliquable, ou file programmee
        bool busy = ToolsSetup.Busy, missing = !busy && (Tools.Ffmpeg == null || Tools.Ytdlp == null);
        if (busy) { view.StatusRight = Tr.T("preparing") + (ToolsSetup.Pct > 0 ? " " + ToolsSetup.Pct + "%" : ""); view.StatusRightKey = null; view.StatusRightColor = St.Accent; }
        else if (missing) { view.StatusRight = Tr.T("prepFail"); view.StatusRightKey = "st:tools"; view.StatusRightColor = Theme.Warn; }
        else if (updMsg != null && DateTime.Now < updMsgUntil) { view.StatusRight = updMsg; view.StatusRightKey = null; view.StatusRightColor = St.Accent; }
        else if (Notices.UpdVersion != null) { view.StatusRight = string.Format(Tr.T("st_upd"), Notices.UpdVersion); view.StatusRightKey = "st:update"; view.StatusRightColor = Theme.Warn; }
        else if (Notices.NoticeText != null) { view.StatusRight = Tr.T("st_notice"); view.StatusRightKey = "st:notice"; view.StatusRightColor = St.Accent; }
        else if (Mgr.Held) { view.StatusRight = Tr.T("held") + Settings.Current.ScheduleTime + " (" + Tr.T("startNow") + ")"; view.StatusRightKey = "st:held"; view.StatusRightColor = St.Accent; }
        else {
            // « Laisser Internet disponible » : un clic pour limiter le debit (~2 Mo/s), un clic pour revenir a la vitesse maximale
            bool eco = Settings.Current.SpeedLimitKB > 0;
            view.StatusRight = Tr.T(eco ? "st_limitOn" : "st_limitOff"); view.StatusRightKey = "st:limit"; view.StatusRightColor = eco ? St.Accent : St.Ink3;
        }

        tray.Text = active > 0 ? (App.Name + " - " + active + " " + Tr.T("active") + " - " + Fmt.Size((long)sp) + Tr.T("perSec")) : App.Name;
        if (tray.Text.Length > 63) tray.Text = tray.Text.Substring(0, 63);
        if (Settings.Current.Clipboard && tick % 2 == 0) WatchClipboard();
        PopNotices();
    }
    // Mise a jour obligatoire : fenetre qui ne se ferme pas sans mettre a jour (ou quitter). Message de l'editeur : une fois, dans la langue de l'utilisateur.
    bool critShown, noticeShown, updOfferClick;
    void PopNotices() {
        if (Notices.UpdCritical && Notices.UpdVersion != null && !critShown && Visible) {
            critShown = true;
            BeginInvoke((Action)(() => { using (var f = new UpdateForm(() => CheckUpdate())) f.ShowDialog(this); }));
        } else if (Notices.UpdVersion != null && !Notices.UpdCritical && Notices.OfferNew()) {
            // mise a jour facultative : proposee UNE fois par version (l'invite en bas de la fenetre reste ensuite)
            updOfferClick = true;
            tray.ShowBalloonTip(15000, App.Name, string.Format(Tr.T("updOffer"), Notices.UpdVersion), ToolTipIcon.Info);
        } else if (Notices.NoticeText != null && !noticeShown) {
            noticeShown = true;
            if (Visible) BeginInvoke((Action)(() => new NoticeForm().Show(this)));
            else tray.ShowBalloonTip(10000, Tr.T("notice_t"), Notices.NoticeText.Length > 120 ? Notices.NoticeText.Substring(0, 120) + "…" : Notices.NoticeText, ToolTipIcon.Info);
        }
    }

    void Notify(Item it) {
        if (it == null) { tray.ShowBalloonTip(10000, App.Name, Tr.T("n_shutdown"), ToolTipIcon.Warning); return; }
        if (!Settings.Current.Notify) return;
        clipCandidate = null;
        if (it.Status == "done") {
            // fin visible (idee prise chez Chrono) : petit son + bulle, un clic lit le fichier (sinon ouvre son dossier)
            lastDone = it;
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            tray.ShowBalloonTip(6000, Tr.T("n_done"), it.Name.TrimStart('~') + "\n" + Tr.T(Ui.IsMedia(it) ? "n_doneClick" : "n_doneFile"), ToolTipIcon.Info);
        } else if (it.Status == "error") {
            // une phrase + le geste, jamais le texte technique (revue Codex n° 22)
            lastDone = null;
            try { System.Media.SystemSounds.Hand.Play(); } catch { }
            tray.ShowBalloonTip(8000, Tr.T("n_err"), it.Name.TrimStart('~') + "\n" + Ui.Error(it), ToolTipIcon.Error);
        }
    }

    static string ClipUrl() {
        try {
            if (!Clipboard.ContainsText()) return null;
            string t = Clipboard.GetText().Trim();
            return (t.StartsWith("http://") || t.StartsWith("https://")) && !t.Contains(" ") && !t.Contains("\n") ? t : null;
        } catch { return null; }
    }

    void WatchClipboard() {
        string u = ClipUrl();
        if (u == null || u == lastClip) return;
        bool first = lastClip == "";
        lastClip = u;
        if (first) return;   // ce qui etait deja copie au lancement ne compte pas
        if (Mgr.GuessKind(u) == "page") return;
        clipCandidate = u;
        tray.ShowBalloonTip(6000, App.Name, Tr.T("n_clip") + "\n" + u, ToolTipIcon.Info);
    }

    // Fenetre d'ajout (bouton Ajouter, glisser-deposer, presse-papiers, ou demande de l'extension avec ask=true).
    // Renvoie l'id ajoute (l'extension suit alors la progression), null si annule ou refuse.
    public string AskAdd(Dictionary<string, object> m) {
        if (!Gate.Open && !ShowSignIn()) return null;
        using (var f = new AddForm(m)) {
            bool wasHidden = !Visible;
            if (wasHidden) { f.StartPosition = FormStartPosition.CenterScreen; f.TopMost = true; }
            if (f.ShowDialog(wasHidden ? null : this) != DialogResult.OK) return null;
            try { var id = Mgr.Add(f.Result).Id; OnTick(); return id; }
            catch (DuplicateException d) { ShowDuplicate(d.Existing); return d.Existing.Id; }
            catch (System.Exception e) { MessageBoxEx(e.Message); return null; }
        }
    }

    void MessageBoxEx(string s) { tray.ShowBalloonTip(5000, App.Name, s, ToolTipIcon.Error); }

    static void SyncStartup() {
        try {
            using (var k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true)) {
                if (Settings.Current.StartWithWindows) k.SetValue(App.Name, "\"" + App.ExePath + "\" --tray");
                else if (k.GetValue(App.Name) != null) k.DeleteValue(App.Name);
            }
        } catch (System.Exception e) { App.Log("startup : " + e.Message); }
    }
}

// Petite grille de formulaire : libelle a gauche, controle a droite.
class FormBase : Form {
    protected readonly TableLayoutPanel grid = new TableLayoutPanel();
    protected readonly FlowLayoutPanel buttons = new FlowLayoutPanel();
    protected bool CustomLayout;   // la fenetre fait sa propre mise en page (reglages)
    public FormBase(string title, int width) {
        // habillage v1 « Studio clair » : Rubik, encre et indigo de la fenetre principale
        Text = title; Font = Fonts.R(13); BackColor = St.Surface; ForeColor = St.Ink; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Theme.Rtl(this);
        grid.ColumnCount = 2; grid.AutoSize = true; grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        buttons.FlowDirection = FlowDirection.RightToLeft; buttons.AutoSize = true; buttons.WrapContents = false;
        // en hebreu la fenetre est en miroir (RightToLeftLayout) : meme sens de pose, le bouton principal finit a droite
        Controls.Add(buttons); Controls.Add(grid);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Dark(this); }
    // Mise en page a la main : une fenetre AutoSize ne grandit pas avec des enfants ancres (fenetre ecrasee a 136 px).
    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        if (CustomLayout) return;
        int pad = St.P(22);
        var gs = grid.PreferredSize; var bs = buttons.PreferredSize;
        int w = Math.Max(gs.Width, bs.Width);
        grid.Location = new Point(pad, pad); grid.Size = gs;
        buttons.Size = bs;
        buttons.Location = new Point(Tr.Rtl ? pad : pad + w - bs.Width, pad + gs.Height + St.P(18));
        ClientSize = new Size(w + 2 * pad, pad + gs.Height + St.P(18) + bs.Height + pad);
    }
    protected T Row<T>(string label, T c) where T : Control {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 7, 10, 3), ForeColor = St.Ink2 };
        c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 3, 0, 3);
        var tb = c as TextBox; if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;
        grid.Controls.Add(l); grid.Controls.Add(c);
        return c;
    }
    protected CheckBox Check(string label, bool v) {
        var c = new CheckBox { Text = label, Checked = v, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        grid.Controls.Add(new Label { AutoSize = true }); grid.Controls.Add(c);
        return c;
    }
    protected Button Button(string text, DialogResult r, bool primary) {
        var b = new Button { Text = text, DialogResult = r, AutoSize = true, MinimumSize = new Size(St.P(110), St.P(38)), FlatStyle = FlatStyle.Flat, Margin = new Padding(St.P(8), 0, 0, 0), Font = Fonts.M(13), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? St.Accent : St.Line;
        if (primary) { b.BackColor = St.Accent; b.ForeColor = Color.White; b.FlatAppearance.MouseOverBackColor = St.AccentDeep; AcceptButton = b; }
        else { b.BackColor = St.Surface; b.ForeColor = St.Ink; b.FlatAppearance.MouseOverBackColor = St.Sunk; }
        if (r == DialogResult.Cancel) CancelButton = b;
        buttons.Controls.Add(b);
        return b;
    }
    protected static ComboBox Combo(string[] keys, string[] labels, string val) {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(labels);
        int i = Array.IndexOf(keys, val); c.SelectedIndex = i >= 0 ? i : 0;
        c.Tag = keys;
        return c;
    }
    protected static string Val(ComboBox c) { return ((string[])c.Tag)[Math.Max(0, c.SelectedIndex)]; }
    protected Control FolderPicker(TextBox tb) {
        var p = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tb.Dock = DockStyle.Fill;
        tb.BorderStyle = BorderStyle.FixedSingle; tb.Margin = new Padding(0);
        var b = new Button { Text = Tr.T("browse"), AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(St.P(6), 0, 0, 0), BackColor = St.AccentSoft, ForeColor = St.Accent, Font = Fonts.M(12), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = St.Lilac;
        b.Click += (s, e) => { using (var d = new FolderBrowserDialog { SelectedPath = tb.Text }) if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath; };
        p.Controls.Add(tb); p.Controls.Add(b);
        return p;
    }
    // d'abord les choix en mots (comme l'extension), puis les hauteurs pour qui sait ce qu'il veut
    protected static readonly string[] QKeys = { "720", "480", "best", "2160", "1440", "1080", "360" };
    protected static string[] QLabels() { return new[] { Tr.T("q_normal"), Tr.T("q_small"), Tr.T("q_best"), "2160p (4K)", "1440p", "1080p", "360p" }; }
}

// Connexion Google (0.7.48, idee de ShowF : le code par mail est penible) : une fois, l'utilisateur choisit son
// compte Google dans le navigateur ; le serveur verifie aupres de Google et rend un jeton ShowLef durable (account.json).
// 0.7.59 (ShowF 25/10) : par une page de NOTRE site (API/showlef_login.php, client « Application Web ») au lieu du
// retour sur 127.0.0.1 : le programme ouvre la page avec une session tiree au hasard, puis demande au serveur
// « c'est fait ? » toutes les 2 s. Aucun secret dans le programme.
static class SiteSignIn {
    const string Url = "https://api.i-call.me/showlef_login.php";
    // Reponse du serveur ({ ok, email, name, ltoken } ou { ok:false, error }) ; null apres 5 minutes ou si stop().
    public static Dictionary<string, object> Run(Func<bool> stop) {
        string sid = NewSession();
        Process.Start(new ProcessStartInfo(PageUrl(sid)) { UseShellExecute = true });
        return Wait(sid, stop);
    }
    public static string NewSession() {
        var b = new byte[32]; using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public static string PageUrl(string sid) { return Url + "?s=" + sid + "&i=" + UsersPing.InstallId() + "&l=" + Tr.Codes[Math.Max(0, Math.Min(2, Tr.Lang))]; }
    public static Dictionary<string, object> Wait(string sid, Func<bool> stop) {
        var until = DateTime.Now.AddMinutes(5);
        while (DateTime.Now < until && !stop()) {
            System.Threading.Thread.Sleep(2000);
            try {
                var r = UsersPing.PostJson(Url, new Dictionary<string, object> { { "poll", sid } });
                if (r != null && !Json.Bool(r, "pending")) return r;
            } catch (System.Exception e) { App.Log("connexion site : " + e.Message); }
        }
        return null;
    }
}

// Adresse prouvee (Google ou code) et son jeton durable : account.json du dossier ShowLef.
static class Account {
    static string F { get { return Path.Combine(App.Dir, "account.json"); } }
    public static string Email, Ltoken, Name;
    public static void Load() {
        try { if (File.Exists(F)) { var o = Json.Obj(File.ReadAllText(F, System.Text.Encoding.UTF8)); Email = Json.Str(o, "email"); Ltoken = Json.Str(o, "ltoken"); Name = Json.Str(o, "name"); } } catch (System.Exception) { Email = Ltoken = Name = null; }
    }
    public static void Save(string email, string lt, string name = null) {
        if (!string.IsNullOrEmpty(name)) Name = name;
        else if (!string.Equals(email, Email, StringComparison.OrdinalIgnoreCase)) Name = null;   // autre compte : l'ancien nom ne lui appartient pas
        Email = email; Ltoken = lt;
        try { File.WriteAllText(F, Json.Ser(new Dictionary<string, object> { { "email", email }, { "ltoken", lt }, { "name", Name } }), new System.Text.UTF8Encoding(false)); } catch (System.Exception) { }
    }
    public static void Forget() { Email = Ltoken = Name = null; try { File.Delete(F); } catch (System.Exception) { } }
}

// Photo du compte : Gravatar (ShowF 25/10). Empreinte MD5 de l'adresse, image gardee 7 jours dans le dossier de
// l'appli ; pas de Gravatar (404) ou site filtre = l'initiale sur degrade, comme avant. Telechargee hors de l'interface.
static class Avatar {
    public static Bitmap Img;
    public static Action Changed;
    static string forEmail;
    public static void Refresh() {
        string em = (Account.Email ?? "").Trim().ToLowerInvariant();
        if (em == forEmail) return;
        forEmail = em; Img = null;
        if (em.Length == 0) { var c0 = Changed; if (c0 != null) c0(); return; }
        new System.Threading.Thread(() => {
            Bitmap bmp = null;
            try {
                string h;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    h = BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(em))).Replace("-", "").ToLowerInvariant();
                string f = Path.Combine(App.Dir, "avatar-" + h.Substring(0, 12) + ".png"), none = f + ".none";
                bool fresh = File.Exists(f) && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays < 7;
                bool noneFresh = File.Exists(none) && (DateTime.Now - File.GetLastWriteTime(none)).TotalDays < 7;
                if (!fresh && !noneFresh) {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://www.gravatar.com/avatar/" + h + "?s=160&d=404");
                    rq.Timeout = 8000; rq.UserAgent = App.Name + "/" + App.Version;
                    try {
                        using (var rs = (System.Net.HttpWebResponse)rq.GetResponse()) using (var st = rs.GetResponseStream()) using (var ms = new MemoryStream()) {
                            st.CopyTo(ms);
                            if (ms.Length > 200) { File.WriteAllBytes(f, ms.ToArray()); try { File.Delete(none); } catch { } }
                        }
                    } catch (System.Net.WebException we) {
                        var r = we.Response as System.Net.HttpWebResponse;
                        if (r != null && (int)r.StatusCode == 404) File.WriteAllText(none, "");   // pas de Gravatar : on ne redemande pas avant 7 jours
                        else App.Log("gravatar : " + we.Message);
                    }
                }
                if (File.Exists(f)) using (var src = new Bitmap(f)) bmp = new Bitmap(src);
            } catch (System.Exception e) { App.Log("gravatar : " + e.Message); }
            if (em != forEmail) { if (bmp != null) bmp.Dispose(); return; }
            Img = bmp;
            var c = Changed; if (c != null) c();
        }) { IsBackground = true }.Start();
    }
    // rond : photo si elle existe, sinon l'initiale sur le degrade indigo -> violet
    public static void Draw(Graphics g, Rectangle r, Font f) {
        var img = Img;
        if (img != null) {
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, r);   // photo a fond transparent : fond blanc + filet
            using (var p = new GraphicsPath()) {
                p.AddEllipse(r);
                var st = g.Save(); g.SetClip(p); var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, r); g.InterpolationMode = im; g.Restore(st);
            }
            using (var pen = new Pen(St.Line, Math.Max(1f, St.P(1f)))) g.DrawEllipse(pen, r);
            return;
        }
        using (var br = new LinearGradientBrush(r, St.Hex("#4f46e5"), St.Hex("#8b5cf6"), 45f)) g.FillEllipse(br, r);
        string n = (Account.Name ?? Account.Email ?? "?").Trim();
        St.Text(g, n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?", f, r, Color.White, 2);
    }
}

// Panneau du compte (refonte du menu, ShowF 25/10) : carte compacte posee sous le bouton : photo, nom, adresse,
// puis « Se deconnecter ». Se ferme au clic dehors ou par Echap.
class AccountPanel : Form {
    readonly Action onLogout;
    Rectangle hover, outR;
    int W { get { return St.P(276); } }
    public AccountPanel(Action logout) {
        onLogout = logout;
        Fonts.Init();
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.White; DoubleBuffered = true; KeyPreview = true; TopMost = true;
        if (Tr.Rtl) RightToLeft = RightToLeft.Yes;
        Size = new Size(W, St.P(124));   // sans bordure : taille = zone cliente (ClientSize etait converti avec une barre de titre)
        KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += (o, e) => Close();
        Avatar.Changed += Repaint;
    }
    void Repaint() { try { if (IsHandleCreated) BeginInvoke((Action)Invalidate); } catch { } }
    protected override void Dispose(bool disposing) { Avatar.Changed -= Repaint; base.Dispose(disposing); }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }   // ombre de Windows
    // coins arrondis recalcules a chaque changement de taille (0.7.58 : region figee a la creation = panneau rogne)
    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        using (var p = St.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), St.P(12))) Region = new Region(p);
    }
    Rectangle R(int x, int y, int w, int h) { return new Rectangle(Tr.Rtl ? W - x - w : x, y, w, h); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int p = St.P(14), H = ClientSize.Height, top = St.P(76);
        // pied gris tres pale pour l'action, haut blanc pour l'identite
        using (var b = new SolidBrush(St.Hex("#f7f8fc"))) g.FillRectangle(b, 0, top, W, H - top);
        using (var pen = new Pen(St.Hex("#eceef5"))) g.DrawLine(pen, 0, top, W, top);
        St.Stroke(g, new RectangleF(0.5f, 0.5f, W - 1.5f, H - 1.5f), St.P(12), St.Line);
        // photo cerclee d'un anneau indigo -> violet, point vert « connecte »
        int a = St.P(46), ring = St.P(2);
        var ar = R(p + ring, St.P(15), a, a);
        using (var br = new LinearGradientBrush(new Rectangle(ar.X - ring * 2, ar.Y - ring * 2, a + ring * 4, a + ring * 4), St.Hex("#4f46e5"), St.Hex("#a78bfa"), 45f))
            g.FillEllipse(br, ar.X - ring * 2, ar.Y - ring * 2, a + ring * 4, a + ring * 4);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, ar.X - ring, ar.Y - ring, a + ring * 2, a + ring * 2);
        Avatar.Draw(g, ar, Fonts.M(19));
        int d = St.P(12);
        var dot = new Rectangle(Tr.Rtl ? ar.X - St.P(1) : ar.Right - d + St.P(1), ar.Bottom - d + St.P(1), d, d);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, dot.X - St.P(2), dot.Y - St.P(2), d + St.P(4), d + St.P(4));
        using (var b = new SolidBrush(St.Hex("#22c55e"))) g.FillEllipse(b, dot);
        int tx = p + a + ring * 2 + St.P(12), tw = W - tx - p;
        St.Text(g, Account.Name ?? "", Fonts.M(15.5f), R(tx, St.P(16), tw, St.P(24)), St.Ink, 0);
        St.TextLtr(g, Account.Email ?? "", Fonts.R(12.5f), R(tx, St.P(40), tw, St.P(20)), St.Ink3, Tr.Rtl);
        // action : pastille d'icone + texte, ligne entiere cliquable
        outR = new Rectangle(St.P(6), top + St.P(6), W - St.P(12), St.P(36));
        bool hv = outR == hover;
        if (hv) St.Fill(g, outR, St.P(9), St.Hex("#fdeeee"));
        Color oc = hv ? St.Hex("#b4231b") : St.Ink2;
        var ib = R(St.P(12), outR.Y + St.P(5), St.P(26), St.P(26));
        St.Fill(g, ib, St.P(7), hv ? Color.White : St.Hex("#eceef5"));
        Ico.Draw(g, "logout", new RectangleF(ib.X + St.P(5), ib.Y + St.P(5), St.P(16), St.P(16)), oc);
        St.Text(g, Tr.T("acc_out"), Fonts.M(13.5f), R(St.P(48), outR.Y, W - St.P(60), outR.Height), oc, 0);
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        var h = outR.Contains(e.Location) ? outR : Rectangle.Empty;
        Cursor = h.IsEmpty ? Cursors.Default : Cursors.Hand;
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!hover.IsEmpty) { hover = Rectangle.Empty; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        if (outR.Contains(e.Location)) { Close(); onLogout(); }
    }
}

// Porte d'entree (0.7.53, decision de ShowF 25/10) : pour utiliser ShowLef, une connexion Google, UNE seule fois
// (nom + adresse, avec l'accord de l'utilisateur ; aucune adresse n'est plus lue dans Chrome). Les telechargements demandes
// par l'extension sont refuses tant qu'elle n'est pas faite ; la fenetre de connexion s'ouvre alors toute seule.
// Banc d'essai (--dir) : porte ouverte, sauf avec --gate (test de la porte elle-meme).
static class Gate {
    public static bool Force;
    public static Action OnAsk;
    public static bool Open {
        get {
            if (!Force && !string.Equals(App.Dir, App.DefaultDir, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(Account.Ltoken) && !string.IsNullOrEmpty(Account.Name);
        }
    }
    public static void Ask() { var a = OnAsk; if (a != null) a(); }
}

// Activation (connexion Google une fois). Refonte 0.7.56 v3 : en-tete en degrade, tuile du logo, bouton Google en pilule, sans « plus tard ».
// Premiere refonte (ShowF 25/10 : « pas joli », texte vague : « pour activer,
// veuillez vous authentifier », rien sur ce qu'on recoit). Dessinee a la main comme « Contact et avis ».
class SignInForm : Form {
    const string Url = "https://api.i-call.me/showlef_feedback.php";
    readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
    Rectangle hover;
    string note; bool noteBad, busy;
    readonly Bitmap logo;
    int W { get { return St.P(440); } }
    int Pd { get { return St.P(36); } }
    public SignInForm() {
        Fonts.Init();
        Text = App.Name + " - " + Tr.T("login_t");
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.White; Icon = Theme.AppIcon();
        DoubleBuffered = true; KeyPreview = true;
        if (Tr.Rtl) RightToLeft = RightToLeft.Yes;
        logo = Surface.IcoImage();   // la plus grande image de l'icone (Icon.ToBitmap brouille le PNG 256)
        // pas de « plus tard » (ShowF 25/10) : s'activer est la seule option ; la croix de Windows reste (ne pas enfermer)
        ClientSize = new Size(W, St.P(440));
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Dark(this); }
    protected override void Dispose(bool disposing) { if (disposing && logo != null) logo.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        hits.Clear();
        int p = Pd, inner = W - 2 * p, hh = St.P(150);
        // en-tete : degrade indigo -> violet, bord bas legerement bombe
        using (var path = new GraphicsPath()) {
            path.AddLine(0, 0, W, 0); path.AddLine(W, 0, W, hh - St.P(22));
            path.AddBezier(W, hh - St.P(22), W * 0.66f, hh + St.P(10), W * 0.33f, hh + St.P(10), 0, hh - St.P(22));
            path.CloseFigure();
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, hh), St.Hex("#4f46e5"), St.Hex("#8b5cf6"), 25f)) g.FillPath(b, path);
            // reflet doux en haut (lumiere, pas un motif)
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, hh / 2 + 1), Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f)) g.FillRectangle(b, 0, 0, W, hh / 2);
        }
        // tuile du logo, a cheval sur le bas de l'en-tete
        int ts = St.P(96);
        var tile = new Rectangle((W - ts) / 2, hh - ts / 2 - St.P(8), ts, ts);
        St.Shadow(g, tile, St.P(26), St.Hex("#312e81"), St.P(10), St.P(22), 0, 60);
        St.Fill(g, tile, St.P(26), Color.White);
        if (logo != null) { int ls = ts - St.P(12); g.DrawImage(logo, new Rectangle(tile.X + (ts - ls) / 2, tile.Y + (ts - ls) / 2, ls, ls)); }

        int y = tile.Bottom + St.P(22);
        St.Text(g, Tr.T("login_h"), Fonts.T(25), new Rectangle(p, y, inner, St.P(38)), Theme.Ink, 2);
        y += St.P(46);
        TextRenderer.DrawText(g, Tr.T("login_b"), Fonts.R(14.5f), new Rectangle(p + St.P(6), y, inner - St.P(12), St.P(48)), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | (Tr.Rtl ? TextFormatFlags.RightToLeft : 0));
        y += St.P(62);

        // bouton Google : pilule blanche, ombre douce, bord fin (la forme que tout le monde reconnait)
        int bh = St.P(54), bw = inner - St.P(16);
        var gb = new Rectangle((W - bw) / 2, y, bw, bh);
        bool hov = !busy && gb == hover;
        St.Shadow(g, gb, bh / 2, St.Hex("#4f46e5"), St.P(hov ? 8 : 5), St.P(hov ? 20 : 14), 0, busy ? 0 : hov ? 60 : 38);
        St.Fill(g, gb, bh / 2, busy ? St.Hex("#f4f5f9") : hov ? St.Hex("#f7f7ff") : Color.White);
        St.Stroke(g, gb, bh / 2, hov ? St.Hex("#c7c9f5") : St.Hex("#dfe1ea"));
        string gt = busy ? Tr.T("login_busy") : Tr.T("login_go");
        Font gf = Fonts.M(16);
        int tw = St.W(gt, gf), gs = St.P(22), gap = St.P(14);
        int off = (gb.Width - (gs + gap + tw)) / 2;
        var gr = new Rectangle(Tr.Rtl ? gb.Right - off - gs : gb.X + off, gb.Y + (bh - gs) / 2, gs, gs);
        FeedbackForm.GoogleG(g, gr);
        var tr = Tr.Rtl ? new Rectangle(gr.X - gap - tw - St.P(4), gb.Y, tw + St.P(4), bh) : new Rectangle(gr.Right + gap, gb.Y, tw + St.P(4), bh);
        St.Text(g, gt, gf, tr, busy ? Theme.Muted : Theme.Ink, 0);
        if (!busy) Hit(gb, Start);
        y += bh + St.P(20);

        // ligne du bas : l'etat (attente / echec) seulement
        if (!string.IsNullOrEmpty(note))
            St.Text(g, note, Fonts.R(13), new Rectangle(p, y, inner, St.P(22)), noteBad ? St.Hex("#b4231b") : Theme.Muted, 2);
    }

    void Hit(Rectangle r, Action a) { hits.Add(new KeyValuePair<Rectangle, Action>(r, a)); }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        var h = hits.LastOrDefault(x => x.Key.Contains(e.Location)).Key;
        Cursor = h.IsEmpty ? Cursors.Default : Cursors.Hand;
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!hover.IsEmpty) { hover = Rectangle.Empty; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        var h = hits.LastOrDefault(x => x.Key.Contains(e.Location));
        if (h.Value != null) h.Value();
    }
    // --shot ... --what signin --page n : 0 accueil · 1 attente · 2 echec
    public static SignInForm Demo(int st) {
        var f = new SignInForm();
        if (st == 1) { f.busy = true; f.note = Tr.T("login_wait"); }
        if (st == 2) { f.note = Tr.T("login_fail"); f.noteBad = true; }
        return f;
    }

    void Start() {
        if (busy) return;
        busy = true; note = Tr.T("login_wait"); noteBad = false; Invalidate();
        new System.Threading.Thread(() => {
            Dictionary<string, object> r = null;
            try {
                r = SiteSignIn.Run(() => IsDisposed);
            } catch (System.Exception ex) { App.Log("connexion google : " + ex.Message); }
            try {
                BeginInvoke((Action)(() => {
                    busy = false;
                    if (r != null && Json.Bool(r, "ok")) {
                        string em = Json.Str(r, "email"), nm = Json.Str(r, "name");
                        Account.Save(em, Json.Str(r, "ltoken"), string.IsNullOrEmpty(nm) ? em.Split('@')[0] : nm);
                        DialogResult = DialogResult.OK; Close();
                    } else { note = Tr.T("login_fail"); noteBad = true; Invalidate(); }
                }));
            } catch (System.InvalidOperationException) { }   // fenetre fermee entre-temps
        }) { IsBackground = true }.Start();
    }
}

// « Contact et avis » : refonte 0.7.48 (maquette v1 « Conversation » choisie par ShowF le 25/10,
// ICALL/DOCS/maquettes/2026-10-24_showlef_contact). Fenetre dessinee a la main (memes polices et icones que la
// fenetre principale) ; seuls les champs de saisie sont des controles Windows. Adresse prouvee par Google (une fois)
// ou par code envoye dans la boite. Serveur : ICALL/API/showlef_feedback.php.
class FeedbackForm : Form {
    const string Url = "https://api.i-call.me/showlef_feedback.php";
    static string L(string he, string fr, string en) { return new[] { he, fr, en }[Math.Max(0, Math.Min(2, Tr.Lang))]; }
    static readonly string[] Kinds = { "review", "idea", "problem" };
    static readonly Color KindAccent0 = Theme.Accent, KindAccent1 = St.Hex("#7c3aed"), KindAccent2 = St.Hex("#c2410c");
    static readonly Color Soft0 = Theme.AccentSoft, Soft1 = St.Hex("#f3edff"), Soft2 = St.Hex("#fff3ec");
    static readonly Color Band = St.Hex("#f4f5f9"), Star = St.Hex("#f5a524"), StarOff = St.Hex("#cfd3e6");

    // etat
    int kind, rating; bool pub, codeMode, sent, busy;
    string token, note; bool noteBad;
    // champs (les seuls controles Windows : la saisie)
    readonly TextBox msg = new TextBox { Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None, AcceptsReturn = true, WordWrap = true };
    readonly TextBox email = new TextBox { BorderStyle = BorderStyle.None };
    readonly TextBox code = new TextBox { BorderStyle = BorderStyle.None, MaxLength = 6, TextAlign = HorizontalAlignment.Center };
    readonly Label hint = new Label { AutoSize = false, BackColor = Color.White, ForeColor = Theme.Faint, Cursor = Cursors.IBeam };
    // zones cliquables du dernier dessin
    readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
    Rectangle hover;
    int W { get { return St.P(460); } }
    int Pd { get { return St.P(24); } }

    public FeedbackForm() {
        Fonts.Init();
        Text = App.Name + " - " + L("יצירת קשר", "Contact", "Contact");
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.White; Icon = Theme.AppIcon();
        DoubleBuffered = true; KeyPreview = true;
        if (Tr.Rtl) { RightToLeft = RightToLeft.Yes; }
        msg.Font = Fonts.R(15); email.Font = Fonts.R(14); code.Font = Fonts.M(17); hint.Font = Fonts.R(15);
        foreach (var t in new[] { msg, email, code }) { t.BackColor = Color.White; t.ForeColor = Theme.Ink; Controls.Add(t); }
        email.RightToLeft = code.RightToLeft = RightToLeft.No;   // adresses et chiffres : toujours de gauche a droite
        msg.Controls.Add(hint);   // DANS le champ : une etiquette posee a cote passe derriere lui (ordre de dessin)
        hint.Click += (o, e) => msg.Focus();
        msg.TextChanged += (o, e) => { hint.Visible = msg.Text.Length == 0; Invalidate(); };
        code.TextChanged += (o, e) => Invalidate();
        email.TextChanged += (o, e) => { if (token != null) { token = null; code.Text = ""; } Invalidate(); };
        foreach (var t in new[] { msg, email, code }) { t.GotFocus += (o, e) => Invalidate(); t.LostFocus += (o, e) => Invalidate(); }

        Account.Load();
        email.Text = Account.Email ?? "";
        // un telechargement en echec dans la liste : on ouvre sur « probleme »
        try { lock (Mgr.Items) if (Mgr.Items.Any(i => i.Status == "error" && !i.Removed)) kind = 2; } catch (System.Exception) { }
        KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Relayout();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Dark(this); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); if (!sent) msg.Focus(); }

    bool Linked { get { return !string.IsNullOrEmpty(Account.Ltoken) && string.Equals(Account.Email, email.Text.Trim(), StringComparison.OrdinalIgnoreCase); } }
    bool CanSend { get { return !busy && (msg.Text.Trim().Length > 0 || rating > 0) && (Linked || (token != null && code.Text.Trim().Length == 6)); } }
    Color K { get { return kind == 1 ? KindAccent1 : kind == 2 ? KindAccent2 : KindAccent0; } }
    Color KSoft { get { return kind == 1 ? Soft1 : kind == 2 ? Soft2 : Soft0; } }
    string Placeholder { get {
        return kind == 0 ? L("מה אהבתם? מה חסר לכם?", "Qu'avez-vous aimé ? Que vous manque-t-il ?", "What did you like? What is missing?")
             : kind == 1 ? L("איזה רעיון יש לכם? מה הוא יעשה בשבילכם?", "Quelle idée avez-vous ? Que ferait-elle pour vous ?", "What is your idea? What would it do for you?")
             : L("מה קרה? באיזה אתר? מה מופיע על הכפתור?", "Que s'est-il passé ? Sur quel site ? Qu'affiche le bouton ?", "What happened? On which site? What does the button show?"); } }

    // Miroir hebreu : x mesure depuis le DEBUT de la ligne (droite en hebreu).
    Rectangle R(int x, int y, int w, int h) { return new Rectangle(Tr.Rtl ? W - x - w : x, y, w, h); }

    // geometrie (recalculee a chaque changement d'etat)
    int yKinds, yStars, yMsg, hMsg, yPub, yWho, hWho, yNote, yFoot, total;
    void Relayout() {
        int p = Pd, y = St.P(22);
        y += St.P(34) + St.P(24);                 // titre + phrase
        yKinds = y; y += St.P(78) + St.P(18);
        yStars = y; if (kind == 0 && !sent) y += St.P(32) + St.P(12);
        yMsg = y; hMsg = St.P(132); y += hMsg;
        yPub = y + St.P(12); if (kind == 0) y += St.P(12) + St.P(22);
        y += St.P(20);
        yWho = y; hWho = Linked ? St.P(66) : codeMode ? St.P(token != null ? 150 : 112) : St.P(96); y += hWho;
        yNote = y + St.P(10); if (!string.IsNullOrEmpty(note)) y += St.P(10) + St.P(40);
        yFoot = y + St.P(16); y = yFoot + St.P(42) + St.P(20);
        total = sent ? St.P(300) : y;
        ClientSize = new Size(W, total);

        bool form = !sent;
        msg.Visible = form; hint.Visible = form && msg.Text.Length == 0;
        if (form) {
            var mr = R(p, yMsg, W - 2 * p, hMsg);
            msg.Bounds = new Rectangle(mr.X + St.P(14), mr.Y + St.P(11), mr.Width - St.P(28), mr.Height - St.P(22));
            hint.Bounds = new Rectangle(0, 0, msg.Width, St.P(24)); hint.TextAlign = Tr.Rtl ? ContentAlignment.TopRight : ContentAlignment.TopLeft;
            hint.Text = Placeholder; hint.RightToLeft = Tr.Rtl ? RightToLeft.Yes : RightToLeft.No;
        }
        bool showEmail = form && !Linked && codeMode;
        email.Visible = showEmail; code.Visible = showEmail && token != null;
        if (showEmail) {
            var er = R(p + St.P(2), yWho + St.P(48), St.P(250), St.P(34));
            email.Bounds = new Rectangle(er.X + St.P(10), er.Y + St.P(8), er.Width - St.P(20), St.P(20));
            var cr = R(p + St.P(2), yWho + St.P(96), St.P(120), St.P(36));
            code.Bounds = new Rectangle(cr.X + St.P(10), cr.Y + St.P(8), cr.Width - St.P(20), St.P(22));
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        hits.Clear();
        int p = Pd, inner = W - 2 * p;
        if (sent) { PaintSent(g); return; }
        St.Text(g, L("דברו איתנו", "Écrivez-nous", "Write to us"), Fonts.T(24), R(p, St.P(18), inner, St.P(36)), Theme.Ink, 0);
        St.Text(g, L("כל הודעה נקראת על ידי הצוות של שולף.", "Chaque message est lu par l'équipe ShowLef.", "Every message is read by the ShowLef team."), Fonts.R(14), R(p, St.P(54), inner, St.P(22)), Theme.Muted, 0);

        // sujets : trois cases, la choisie prend sa couleur
        string[] names = { L("חוות דעת", "Un avis", "A review"), L("רעיון", "Une idée", "An idea"), L("בעיה", "Un problème", "A problem") };
        string[] icons = { "star", "bulb", "alert" };
        int gap = St.P(8), cw = (inner - 2 * gap) / 3, ch = St.P(78);
        for (int i = 0; i < 3; i++) {
            var r = R(p + i * (cw + gap), yKinds, cw, ch);
            bool on = i == kind, hov = !on && r == hover;
            Color ac = i == 1 ? KindAccent1 : i == 2 ? KindAccent2 : KindAccent0;
            St.Fill(g, r, St.P(12), on ? (i == 1 ? Soft1 : i == 2 ? Soft2 : Soft0) : Color.White);
            using (var pen = new Pen(on ? ac : hov ? St.Hex("#c9cdf6") : Theme.Line, St.P(1.5f))) using (var path = St.Round(new RectangleF(r.X + .75f, r.Y + .75f, r.Width - 1.5f, r.Height - 1.5f), St.P(12))) g.DrawPath(pen, path);
            int isz = St.P(24);
            Ico.Draw(g, icons[i], new RectangleF(r.X + (r.Width - isz) / 2f, r.Y + St.P(14), isz, isz), on ? ac : Theme.Muted);
            St.Text(g, names[i], Fonts.M(14), new Rectangle(r.X, r.Y + St.P(46), r.Width, St.P(22)), Theme.Ink, 2);
            int k = i; Hit(r, () => { kind = k; Relayout(); });
        }

        // note (avis seulement)
        if (kind == 0) {
            St.Text(g, L("כמה שולף עוזר לכם?", "ShowLef vous aide-t-il ?", "How much does ShowLef help you?"), Fonts.R(14), R(p, yStars, inner - St.P(170), St.P(32)), Theme.Muted, 0);
            int s = St.P(30);
            for (int i = 0; i < 5; i++) {
                // etoiles toujours de gauche a droite, a la FIN de la ligne
                int x = Tr.Rtl ? p + i * s : W - p - (5 - i) * s;
                var r = new Rectangle(x, yStars + St.P(1), s, s);
                Ico.Draw(g, i < rating ? "starf" : "star", new RectangleF(r.X + St.P(3), r.Y + St.P(3), s - St.P(6), s - St.P(6)), i < rating ? Star : (r == hover ? St.Hex("#b5b9d4") : StarOff));
                int n = i + 1; Hit(r, () => { rating = rating == n ? 0 : n; Invalidate(); });
            }
        }

        // message : cadre arrondi, couleur du sujet quand il a le focus
        var mr = R(p, yMsg, inner, hMsg);
        if (msg.Focused) St.Fill(g, new RectangleF(mr.X - St.P(3), mr.Y - St.P(3), mr.Width + St.P(6), mr.Height + St.P(6)), St.P(14), KSoft);
        St.Fill(g, mr, St.P(12), Color.White);
        using (var pen = new Pen(msg.Focused ? K : Theme.Line, St.P(1.5f))) using (var path = St.Round(new RectangleF(mr.X + .75f, mr.Y + .75f, mr.Width - 1.5f, mr.Height - 1.5f), St.P(12))) g.DrawPath(pen, path);

        if (kind == 0) {
            var sw = R(p, yPub, St.P(36), St.P(20));
            St.Fill(g, sw, St.P(10), pub ? Theme.Accent : St.Hex("#d5d8e5"));
            int kx = pub == Tr.Rtl ? sw.X + St.P(2) : sw.Right - St.P(18);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, sw.Y + St.P(2), St.P(16), St.P(16));
            var tr = R(p + St.P(46), yPub - St.P(1), inner - St.P(46), St.P(22));
            St.Text(g, L("אפשר להציג את חוות הדעת בדף של שולף, אחרי אישור שלנו", "Visible sur la page ShowLef, après notre accord", "Shown on the ShowLef page, after our approval"), Fonts.R(13.5f), tr, Theme.Muted, 0);
            Hit(Rectangle.Union(sw, tr), () => { pub = !pub; Invalidate(); });
        }

        PaintWho(g);

        if (!string.IsNullOrEmpty(note)) {
            var nr = R(p, yNote, inner, St.P(40));
            St.Fill(g, nr, St.P(10), noteBad ? St.Hex("#fdeeee") : St.Hex("#e8f6f0"));
            St.Text(g, note, Fonts.R(13.5f), new Rectangle(nr.X + St.P(12), nr.Y, nr.Width - St.P(24), nr.Height), noteBad ? St.Hex("#9b2424") : St.Hex("#0b6b4c"), 0);
        }

        // pied : envoyer (principal), fermer, copier pour le support (lien discret au debut)
        int bh = St.P(42);
        string sendT = busy ? L("שולחים…", "Envoi…", "Sending…") : L("שליחה", "Envoyer", "Send");
        int sw2 = Math.Max(St.P(104), St.W(sendT, Fonts.M(15)) + St.P(44));
        var send = R(W - p - sw2, yFoot, sw2, bh);
        bool can = CanSend;
        if (can) St.Shadow(g, send, St.P(10), Theme.Accent, St.P(4), St.P(10), 0, 60);
        St.Fill(g, send, St.P(10), can ? (send == hover ? St.Hex("#4338ca") : Theme.Accent) : St.Hex("#c7c9e8"));
        St.Text(g, sendT, Fonts.M(15), send, Color.White, 2);
        if (can) Hit(send, Send);
        string closeT = L("סגירה", "Fermer", "Close");
        int cw2 = St.W(closeT, Fonts.M(15)) + St.P(40);
        var close = R(W - p - sw2 - St.P(10) - cw2, yFoot, cw2, bh);
        St.Fill(g, close, St.P(10), close == hover ? St.Hex("#e1e4ef") : Theme.Sunk);
        St.Text(g, closeT, Fonts.M(15), close, Theme.Ink, 2);
        Hit(close, Close);
        string cpT = L("העתקת מידע לתמיכה", "Copier pour le support", "Copy for support");
        var cp = R(p, yFoot, St.W(cpT, Fonts.R(12.5f)) + St.P(4), bh);
        St.Text(g, cpT, Fonts.R(12.5f), cp, cp == hover ? Theme.Ink : Theme.Muted, 0);
        using (var pen = new Pen(cp == hover ? Theme.Ink : Theme.Faint, 1)) g.DrawLine(pen, cp.X, cp.Y + bh / 2 + St.P(9), cp.Right - St.P(4), cp.Y + bh / 2 + St.P(9));
        Hit(cp, () => { try { Clipboard.SetText(UsersPing.SupportInfo()); Note(L("המידע הועתק. אפשר להדביק אותו בהודעה.", "Informations copiées : collez-les dans votre message.", "Information copied: paste it in your message."), false); } catch (System.Exception) { } });
    }

    void PaintWho(Graphics g) {
        int p = Pd, inner = W - 2 * p;
        var band = new Rectangle(0, yWho, W, hWho);
        using (var b = new SolidBrush(Band)) g.FillRectangle(b, band);
        using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, 0, yWho, W, yWho);
        if (Linked) {
            int a = St.P(36);
            var av = R(p, yWho + (hWho - a) / 2, a, a);
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, av);
            string ini = Account.Email.Substring(0, 1).ToUpperInvariant();
            St.Text(g, ini, Fonts.T(16), av, Color.White, 2);
            var tr = R(p + a + St.P(12), yWho + St.P(13), inner - a - St.P(140), St.P(18));
            St.Text(g, L("התשובה תגיע אל", "Notre réponse arrivera à", "Our reply goes to"), Fonts.R(12.5f), tr, Theme.Muted, 0);
            var er = R(p + a + St.P(12), yWho + St.P(32), inner - a - St.P(140), St.P(22));
            St.TextLtr(g, Account.Email, Fonts.M(14), er, Theme.Ink, Tr.Rtl);
            string sw = L("החלפת חשבון", "Changer de compte", "Switch account");
            var lr = R(W - p - St.W(sw, Fonts.R(13)) - St.P(4), yWho, St.W(sw, Fonts.R(13)) + St.P(4), hWho);
            Link(g, sw, lr);
            Hit(lr, () => { Account.Forget(); codeMode = false; Relayout(); });
            return;
        }
        if (!codeMode) {
            string gt = L("התחברות עם גוגל", "Se connecter avec Google", "Sign in with Google");
            int gw = St.W(gt, Fonts.M(14)) + St.P(58);
            var gb = R(p, yWho + St.P(16), gw, St.P(40));
            St.Fill(g, gb, St.P(10), Color.White);
            St.Stroke(g, gb, St.P(10), gb == hover ? St.Hex("#b9bee0") : Theme.Line);
            var logo = new Rectangle(Tr.Rtl ? gb.Right - St.P(32) : gb.X + St.P(14), yWho + St.P(27), St.P(18), St.P(18));
            GoogleG(g, logo);
            var gtr = Tr.Rtl ? new Rectangle(gb.X + St.P(14), gb.Y, gb.Width - St.P(52), gb.Height) : new Rectangle(gb.X + St.P(40), gb.Y, gb.Width - St.P(52), gb.Height);
            St.Text(g, gt, Fonts.M(14), gtr, Theme.Ink, 0);
            if (!busy) Hit(gb, SignIn);
            var help = R(p + gw + St.P(12), yWho + St.P(16), inner - gw - St.P(12), St.P(40));
            St.Text(g, L("פעם אחת, ואז שולחים בלי קוד", "Une fois, puis sans code", "Once, then no code"), Fonts.R(13), help, Theme.Muted, 0);
            string q = L("אין לכם חשבון גוגל?", "Pas de compte Google ?", "No Google account?");
            string lk = L("קבלו קוד במייל", "Recevoir un code par e-mail", "Get a code by e-mail");
            int qw = St.W(q, Fonts.R(12.5f)) + St.P(6);
            St.Text(g, q, Fonts.R(12.5f), R(p, yWho + St.P(64), qw, St.P(20)), Theme.Muted, 0);
            var lr = R(p + qw, yWho + St.P(64), St.W(lk, Fonts.R(12.5f)) + St.P(4), St.P(20));
            Link(g, lk, lr);
            Hit(lr, () => { codeMode = true; Relayout(); email.Focus(); });
            return;
        }
        // code par mail : adresse, envoi du code, puis le code
        St.Text(g, L("כתובת המייל שלכם", "Votre adresse e-mail", "Your e-mail address"), Fonts.R(12.5f), R(p, yWho + St.P(14), inner, St.P(28)), Theme.Muted, 0);
        var er2 = R(p + St.P(2), yWho + St.P(48), St.P(250), St.P(34));
        St.Fill(g, er2, St.P(8), Color.White); St.Stroke(g, er2, St.P(8), email.Focused ? Theme.Accent : Theme.Line);
        string bt = token == null ? L("שליחת קוד", "Envoyer le code", "Send the code") : L("קוד חדש", "Nouveau code", "New code");
        int bw = St.W(bt, Fonts.M(13.5f)) + St.P(28);
        var bc = R(p + St.P(262), yWho + St.P(48), bw, St.P(34));
        St.Fill(g, bc, St.P(8), bc == hover ? St.Hex("#e2e5ff") : Theme.AccentSoft);
        St.Text(g, bt, Fonts.M(13.5f), bc, Theme.Accent, 2);
        if (!busy) Hit(bc, AskCode);
        if (token != null) {
            var cr = R(p + St.P(2), yWho + St.P(96), St.P(120), St.P(36));
            St.Fill(g, cr, St.P(8), Color.White); St.Stroke(g, cr, St.P(8), code.Focused ? Theme.Accent : Theme.Line);
            St.Text(g, L("הקלידו את הקוד שקיבלתם (בתוקף 10 דקות)", "Tapez le code reçu (valable 10 minutes)", "Type the code you received (valid 10 minutes)"), Fonts.R(12.5f), R(p + St.P(134), yWho + St.P(96), inner - St.P(134), St.P(36)), Theme.Muted, 0);
        }
        string back = L("חזרה להתחברות עם גוגל", "Revenir à la connexion Google", "Back to Google sign-in");
        var br = R(W - p - St.W(back, Fonts.R(12.5f)) - St.P(4), yWho + St.P(14), St.W(back, Fonts.R(12.5f)) + St.P(4), St.P(28));
        Link(g, back, br);
        Hit(br, () => { codeMode = false; token = null; code.Text = ""; Relayout(); });
    }

    void PaintSent(Graphics g) {
        int c = St.P(68), cx = (W - c) / 2, y = St.P(46);
        using (var b = new SolidBrush(St.Hex("#e8f6f0"))) g.FillEllipse(b, cx, y, c, c);
        Ico.Draw(g, "check", new RectangleF(cx + St.P(18), y + St.P(18), c - St.P(36), c - St.P(36)), Theme.Ok, 2.4f);
        St.Text(g, L("תודה, ההודעה נשלחה", "Merci, votre message est envoyé", "Thank you, your message was sent"), Fonts.T(22), new Rectangle(0, y + c + St.P(16), W, St.P(32)), Theme.Ink, 2);
        string who = Account.Email ?? email.Text.Trim();
        St.Text(g, L("נענה לכם במייל", "Nous vous répondrons à", "We will reply to"), Fonts.R(14), new Rectangle(0, y + c + St.P(52), W, St.P(22)), Theme.Muted, 2);
        TextRenderer.DrawText(g, who, Fonts.M(14), new Rectangle(0, y + c + St.P(74), W, St.P(22)), Theme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);   // adresse : gauche a droite, centree
        string closeT = L("סגירה", "Fermer", "Close");
        int cw = St.W(closeT, Fonts.M(15)) + St.P(48);
        var close = new Rectangle((W - cw) / 2, y + c + St.P(116), cw, St.P(42));
        St.Fill(g, close, St.P(10), close == hover ? St.Hex("#e1e4ef") : Theme.Sunk);
        St.Text(g, closeT, Fonts.M(15), close, Theme.Ink, 2);
        Hit(close, Close);
    }

    // lien souligne (le soulignement suit la longueur reelle du texte, dans les deux sens)
    internal static void Link(Graphics g, string s, Rectangle r) {
        St.Text(g, s, Fonts.R(12.5f), r, Theme.Accent, 0);
        int w = St.W(s, Fonts.R(12.5f));
        int x0 = Tr.Rtl ? r.Right - w : r.X;
        using (var pen = new Pen(Color.FromArgb(140, Theme.Accent), 1)) g.DrawLine(pen, x0, r.Y + r.Height / 2 + St.P(9), x0 + w, r.Y + r.Height / 2 + St.P(9));
    }
    // « G » de Google : quatre arcs de couleur (pas d'image a embarquer)
    internal static void GoogleG(Graphics g, Rectangle r) {
        float w = r.Width * 0.2f; var rr = new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
        Action<Color, float, float> arc = (c, a0, sweep) => { using (var pen = new Pen(c, w)) g.DrawArc(pen, rr, a0, sweep); };
        arc(St.Hex("#EA4335"), 200, 110); arc(St.Hex("#FBBC05"), 135, 70); arc(St.Hex("#34A853"), 45, 95); arc(St.Hex("#4285F4"), -20, 65);
        using (var b = new SolidBrush(St.Hex("#4285F4"))) g.FillRectangle(b, rr.X + rr.Width / 2, rr.Y + rr.Height / 2 - w / 2, rr.Width / 2 + w / 2, w);
    }

    void Hit(Rectangle r, Action a) { hits.Add(new KeyValuePair<Rectangle, Action>(r, a)); }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        var h = hits.LastOrDefault(x => x.Key.Contains(e.Location)).Key;
        Cursor = h.IsEmpty ? Cursors.Default : Cursors.Hand;
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!hover.IsEmpty) { hover = Rectangle.Empty; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        var h = hits.LastOrDefault(x => x.Key.Contains(e.Location));
        if (h.Value != null) h.Value();
    }
    void Note(string s, bool bad) { note = s; noteBad = bad; Relayout(); }
    // --shot ... --what feedback --page n : etats de la fenetre pour verifier le rendu sans rien envoyer
    // 0 connecte (account.json d'exemple) · 1 pas connecte · 2 code par mail · 3 probleme + erreur · 4 envoye
    public static FeedbackForm Demo(int st) {
        if (st == 0 || st == 3 || st == 4) Account.Save("moshe.levi@gmail.com", "demo"); else Account.Forget();
        var f = new FeedbackForm();
        f.email.Text = "moshe.levi@gmail.com";
        if (st == 0) { f.kind = 0; f.rating = 4; f.pub = true; f.msg.Text = "תוכנה מעולה, חוסך לי המון זמן עם השיעורים."; }
        if (st == 2) { f.codeMode = true; f.token = "demo"; f.code.Text = "4821"; }
        if (st == 3) { f.kind = 2; f.msg.Text = "בשיעור של אתמול כתוב לי שההורדה נכשלה."; f.note = "לא הצלחנו לשלוח. בדקו את החיבור לאינטרנט ונסו שוב."; f.noteBad = true; }
        if (st == 4) f.sent = true;
        f.Relayout();
        return f;
    }
    string Lang() { return Tr.Codes[Math.Max(0, Math.Min(2, Tr.Lang))]; }

    void SignIn() {
        busy = true; Note(L("נפתח דף של גוגל בדפדפן: בחרו את החשבון שלכם.", "Une page Google s'ouvre dans le navigateur : choisissez votre compte.", "A Google page opens in your browser: choose your account."), false);
        new System.Threading.Thread(() => {
            Dictionary<string, object> r = null; string err = null;
            try {
                r = SiteSignIn.Run(() => IsDisposed);
                if (r == null || Json.Str(r, "error") == "cancel") { err = "cancel"; r = null; }
            } catch (System.Exception ex) { err = ex.GetType().Name; App.Log("connexion google : " + ex.Message); }
            BeginInvoke((Action)(() => {
                busy = false;
                if (r != null && Json.Bool(r, "ok")) {
                    Account.Save(Json.Str(r, "email"), Json.Str(r, "ltoken"), Json.Str(r, "name"));
                    email.Text = Account.Email; codeMode = false;
                    Note(L("מחובר! מעכשיו שולחים בלי קוד.", "Connecté ! Vous envoyez maintenant sans code.", "Signed in! You now send without a code."), false);
                    Activate(); msg.Focus();
                } else Note(err == "cancel" ? L("החיבור בוטל. אפשר לנסות שוב או לקבל קוד במייל.", "Connexion annulée : réessayez, ou recevez un code par e-mail.", "Sign-in cancelled: try again, or get a code by e-mail.")
                    : L("החיבור לגוגל לא הצליח. אפשר לקבל קוד במייל.", "La connexion Google n'a pas marché : recevez un code par e-mail.", "Google sign-in did not work: get a code by e-mail."), true);
            }));
        }) { IsBackground = true }.Start();
    }

    void AskCode() {
        string e = email.Text.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { Note(L("כתובת המייל לא תקינה.", "Cette adresse e-mail n'est pas valide.", "This e-mail address is not valid."), true); return; }
        busy = true; Note(L("שולחים קוד…", "Envoi du code…", "Sending the code…"), false);
        new System.Threading.Thread(() => {
            Dictionary<string, object> r = null;
            try { r = UsersPing.PostJson(Url, new Dictionary<string, object> { { "action", "code" }, { "email", e }, { "l", Lang() } }); } catch (System.Exception) { }
            BeginInvoke((Action)(() => {
                busy = false;
                if (r != null && Json.Bool(r, "ok")) { token = Json.Str(r, "token"); Note(L("שלחנו קוד אל " + e, "Code envoyé à " + e, "Code sent to " + e), false); code.Focus(); }
                else if (r != null && Json.Str(r, "error") == "limit") Note(L("יותר מדי נסיונות. נסו שוב בעוד שעה.", "Trop d'essais. Réessayez dans une heure.", "Too many tries. Try again in an hour."), true);
                else Note(L("לא הצלחנו לשלוח קוד. בדקו את האינטרנט והכתובת.", "Impossible d'envoyer le code. Vérifiez internet et l'adresse.", "Could not send the code. Check the internet and the address."), true);
            }));
        }) { IsBackground = true }.Start();
    }

    void Send() {
        if (!CanSend) return;
        busy = true; note = null; Relayout();
        var body = new Dictionary<string, object> {
            { "action", "send" }, { "token", token }, { "code", code.Text.Trim() }, { "install", UsersPing.InstallId() }, { "email", email.Text.Trim().ToLowerInvariant() },
            { "kind", Kinds[kind] }, { "rating", kind == 0 ? rating : 0 }, { "message", msg.Text.Trim() }, { "public", kind == 0 && pub }, { "v", App.Version }, { "l", Lang() } };
        if (Linked) body["ltoken"] = Account.Ltoken;
        // 0.7.47 : les informations techniques (les memes que « copier pour le support », deja anonymisees) partent avec
        // le message : le support voit la cause sans prise en main a distance (cas Motti / NetFree, 24/10/2026).
        // Plafond 24 000 caracteres (le serveur coupe au meme endroit ; un filtre comme Techloq bloque les envois > 64 Ko).
        try { string si = UsersPing.SupportInfo(); body["support"] = si.Length > 24000 ? si.Substring(si.Length - 24000) : si; } catch (System.Exception) { }
        new System.Threading.Thread(() => {
            Dictionary<string, object> r = null;
            try { r = UsersPing.PostJson(Url, body); } catch (System.Exception) { }
            BeginInvoke((Action)(() => {
                busy = false;
                if (r != null && Json.Bool(r, "ok")) {
                    // adresse prouvee : gardee, plus de code la prochaine fois
                    if (!string.IsNullOrEmpty(Json.Str(r, "ltoken"))) Account.Save(email.Text.Trim().ToLowerInvariant(), Json.Str(r, "ltoken"));
                    sent = true; msg.Text = ""; code.Text = ""; token = null; note = null; Relayout(); return;
                }
                string er = r != null ? Json.Str(r, "error") : null;
                Note(er == "wrong" ? L("הקוד שגוי. בדקו את המייל ונסו שוב.", "Code incorrect. Regardez votre e-mail et réessayez.", "Wrong code. Check your e-mail and try again.")
                    : er == "expired" ? L("הקוד פג תוקף. בקשו קוד חדש.", "Code expiré. Demandez-en un nouveau.", "Code expired. Ask for a new one.")
                    : er == "limit" ? L("יותר מדי הודעות היום.", "Trop de messages aujourd'hui.", "Too many messages today.")
                    : L("לא הצלחנו לשלוח. בדקו את החיבור לאינטרנט ונסו שוב.", "Envoi impossible : vérifiez internet puis réessayez.", "Could not send: check the internet and try again."), true);
            }));
        }) { IsBackground = true }.Start();
    }
}

// Mise a jour obligatoire (0.7.30) : pas de croix ; « Mettre a jour » ou « Quitter ».
class UpdateForm : FormBase {
    readonly Action update;
    public UpdateForm(Action update) : base(App.Name + " - " + Tr.T("upd_t"), 380) {
        this.update = update;
        ControlBox = false;
        grid.Controls.Add(new Label { AutoSize = true }); grid.Controls.Add(new Label { Text = Tr.T("upd_b"), AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = St.Ink });
        var go = Button(Tr.T("upd_now"), DialogResult.None, true);
        var quit = Button(Tr.T("upd_quit"), DialogResult.None, false);
        go.Click += (s, e) => this.update();
        quit.Click += (s, e) => Environment.Exit(0);
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; base.OnFormClosing(e); }
}

// Message de l'editeur (0.7.30) : une seule fois, texte deja choisi dans la langue de l'utilisateur ; lien https facultatif.
class NoticeForm : FormBase {
    public NoticeForm() : base(App.Name + " - " + Tr.T("notice_t"), 380) {
        grid.Controls.Add(new Label { AutoSize = true });
        grid.Controls.Add(new Label { Text = Notices.NoticeText ?? "", AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = St.Ink });
        string url = Notices.NoticeUrl;
        if (url != null) { var go = Button(Tr.T("notice_go"), DialogResult.None, true); go.Click += (s, e) => { try { Process.Start(url); } catch (System.Exception) { } }; }
        Button(Tr.T("notice_ok"), DialogResult.Cancel, url == null);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); Notices.MarkSeen(); }
}

class ClipForm : FormBase {
    readonly TextBox from = new TextBox { Text = "0:00" }, to = new TextBox { Text = "1:00" };
    public string From { get { return from.Text; } }
    public string To { get { return to.Text; } }
    public ClipForm() : base(App.Name + " - " + Tr.T("clip").TrimEnd('.'), 160) {
        Row(Tr.T("clipFrom"), from);
        Row(Tr.T("clipTo"), to);
        Button(Tr.T("cancel"), DialogResult.Cancel, false);
        Button(Tr.T("ok"), DialogResult.OK, true);
    }
}

class AddForm : FormBase {
    public Dictionary<string, object> Result;
    readonly TextBox url = new TextBox(), name = new TextBox(), folder = new TextBox();
    readonly ComboBox kind, quality, afmt;
    readonly CheckBox audio;

    public AddForm(Dictionary<string, object> m) : base(App.Name + " - " + Tr.T("add"), 460) {
        m = m ?? new Dictionary<string, object>();
        string u = Json.Str(m, "url");
        if (string.IsNullOrEmpty(u)) {
            try { string c = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; if (c.StartsWith("http") && !c.Contains(" ")) u = c; } catch { }
        }
        url.Text = u ?? "";
        Row(Tr.T("url"), url);
        string[] kk = { "auto", "file", "hls", "dash", "page" };
        kind = Row(Tr.T("kind"), Combo(kk, kk.Select(k => Tr.T("k_" + k)).ToArray(), Json.Str(m, "kind") ?? "auto"));
        quality = Row(Tr.T("quality"), Combo(QKeys, QLabels(), Json.Str(m, "quality") ?? Settings.Current.Quality));
        audio = Check(Tr.T("audioOnly"), Json.Bool(m, "audioOnly"));
        afmt = Row(Tr.T("audioFmt"), Combo(new[] { "mp3", "m4a", "original" }, new[] { "MP3", "M4A", Tr.T("audioOrig") }, Json.Str(m, "audioFormat") ?? Settings.Current.AudioFormat));
        name.Text = Json.Str(m, "filename") ?? "";
        Row(Tr.T("fileName"), name);
        folder.Text = "";
        Row(Tr.T("saveTo"), FolderPicker(folder));
        Button(Tr.T("cancel"), DialogResult.Cancel, false);
        var ok = Button(Tr.T("download"), DialogResult.OK, true);
        ok.Click += (s, e) => {
            if (!url.Text.Trim().StartsWith("http")) { DialogResult = DialogResult.None; url.Focus(); return; }
            Result = new Dictionary<string, object>(m);
            Result["url"] = url.Text.Trim();
            Result["kind"] = Val(kind);
            Result["quality"] = Val(quality);
            Result["audioOnly"] = audio.Checked;
            Result["audioFormat"] = Val(afmt);
            if (name.Text.Trim().Length > 0) Result["filename"] = name.Text.Trim(); else Result.Remove("filename");
            if (folder.Text.Trim().Length > 0) Result["folder"] = folder.Text.Trim();
        };
    }
}

// Playlist (0.7.35) : cases a cocher, « Tout cocher » / « Seulement les nouveaux » / « Aucune ».
class PlaylistForm : FormBase {
    static string L(string he, string fr, string en) { return new[] { he, fr, en }[Math.Max(0, Math.Min(2, Tr.Lang))]; }
    readonly ListView lv = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = St.Ink, Font = Fonts.R(12), MultiSelect = false };
    readonly Button go;
    readonly List<PlRow> rows;
    public List<string> Selected = new List<string>();

    Button Small(string text, Action act) {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, St.P(8), 0), BackColor = St.AccentSoft, ForeColor = St.Accent, Font = Fonts.M(12), Cursor = Cursors.Hand, MinimumSize = new Size(0, St.P(36)) };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = St.Lilac;
        b.Click += (o, e) => { act(); Refresh2(); };
        return b;
    }
    void Refresh2() {
        int n = lv.CheckedItems.Count;
        go.Text = L("הורדה", "Télécharger", "Download") + " (" + n + ")"; go.Enabled = n > 0;
    }
    public PlaylistForm(string name, List<PlRow> rows) : base(App.Name + " - " + name, 380) {
        CustomLayout = true; this.rows = rows;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        Controls.Clear();
        ClientSize = new Size(St.P(760), St.P(600));
        int known = rows.Count(r => r.Known);

        var head = new Panel { Dock = DockStyle.Top, Height = St.P(96), BackColor = St.Surface, Padding = new Padding(St.P(28), St.P(16), St.P(28), 0) };
        var sub = new Label { Dock = DockStyle.Fill, Font = Fonts.R(12), ForeColor = St.Ink3, Text = rows.Count + " " + L("סרטונים", "vidéos", "videos") + (known > 0 ? " · " + known + " " + L("כבר הורדו", "déjà téléchargées", "already downloaded") : "") + " · " + L("יישמרו בתיקייה", "dossier", "folder") + " « " + name + " »", TextAlign = ContentAlignment.TopLeft };
        var tt = new Label { Dock = DockStyle.Top, Height = St.P(44), Font = Fonts.M(19), ForeColor = St.Ink, Text = L("בחירת סרטונים", "Choisissez les vidéos", "Choose the videos"), TextAlign = ContentAlignment.MiddleLeft };
        head.Controls.Add(sub); head.Controls.Add(tt);
        if (Tr.Rtl) { tt.RightToLeft = sub.RightToLeft = RightToLeft.Yes; tt.TextAlign = ContentAlignment.MiddleRight; sub.TextAlign = ContentAlignment.TopRight; }

        lv.Columns.Add("", St.P(680));
        lv.ItemChecked += (o, e) => Refresh2();
        foreach (var r in rows) {
            var li = new ListViewItem(r.Idx.ToString().PadLeft(2, '0') + "   " + (string.IsNullOrEmpty(r.Title) ? r.Id : r.Title) + (r.Known ? "   ✓" : "")) { Checked = !r.Known, Tag = r };
            if (r.Known) li.ForeColor = St.Ink3;
            lv.Items.Add(li);
        }
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(St.P(28), 0, St.P(28), St.P(8)), BackColor = St.Surface };
        listHost.Controls.Add(lv);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = St.P(68), BackColor = St.Sunk };
        bar.Paint += (o, e) => { using (var pen = new Pen(St.Line)) e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0); };
        buttons.Dock = DockStyle.Right; buttons.Padding = new Padding(St.P(24), St.P(14), St.P(24), 0); buttons.AutoSize = true;
        go = Button(L("הורדה", "Télécharger", "Download"), DialogResult.OK, true);
        Button(Tr.T("cancel"), DialogResult.Cancel, false);
        var quick = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(St.P(24), St.P(16), 0, 0), WrapContents = false };
        quick.Controls.Add(Small(L("סמן הכול", "Tout cocher", "Check all"), () => { foreach (ListViewItem x in lv.Items) x.Checked = true; }));
        quick.Controls.Add(Small(L("רק חדשים", "Seulement les nouveaux", "Only new ones"), () => { foreach (ListViewItem x in lv.Items) x.Checked = !((PlRow)x.Tag).Known; }));
        quick.Controls.Add(Small(L("ללא", "Aucune", "None"), () => { foreach (ListViewItem x in lv.Items) x.Checked = false; }));
        bar.Controls.Add(buttons); bar.Controls.Add(quick);

        Controls.Add(listHost); Controls.Add(bar); Controls.Add(head);
        Refresh2();
        go.Click += (o, e) => { Selected = lv.CheckedItems.Cast<ListViewItem>().Select(x => ((PlRow)x.Tag).Id).ToList(); };
    }
}

// Installation / mise a jour de ffmpeg et yt-dlp dans %LOCALAPPDATA%\ShowLef\tools.
// Installation des outils annexes (ffmpeg, yt-dlp, deno) dans le dossier tools de ShowLef.
// Au lancement, sans rien demander : ceux qui manquent, et yt-dlp s'il a plus de 7 jours (les sites
// changent souvent, un yt-dlp vieilli finit par echouer). Bouton Outils : tout (mise a jour).
static class ToolsSetup {
    const string YtUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    // Empreintes publiees par chaque projet a cote de son fichier (revue Codex n° 5 : un outil etait installe sur sa
    // seule longueur ; une page HTML de 150 Ko pouvait devenir yt-dlp.exe). Fichier qui ne correspond pas = refuse,
    // l'ancien outil reste en place.
    const string YtSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    const string FfSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";

    static string ExpectedHash(string sumsUrl, string fileName) {
        string txt = Net.Text(new Item(), sumsUrl);
        var all = System.Text.RegularExpressions.Regex.Matches(txt, @"\b[0-9a-fA-F]{64}\b");
        if (all.Count == 1) return all[0].Value.ToLowerInvariant();   // un seul fichier decrit (deno)
        foreach (string line in txt.Split('\n')) {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$");
            if (m.Success && m.Groups[2].Value == fileName) return m.Groups[1].Value.ToLowerInvariant();
        }
        throw new Exception("empreinte introuvable pour " + fileName);
    }
    static string Sha256(string file) {
        using (var sha = System.Security.Cryptography.SHA256.Create()) using (var f = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
    class HashFail : Exception { public HashFail(string m) : base(m) { } }
    // banc d'essai seulement (--dir + --tools-base) : outils servis par un serveur local a la place de github.com
    public static string Base = "https://github.com";
    static string U(string url) { return Base + url.Substring("https://github.com".Length); }
    static int busy;
    public static bool Busy { get { return busy != 0; } }
    public static volatile int Pct;      // avancement de l'installation (barre d'etat)
    public static string Status = "";

    // yt-dlp installe par ShowLef et vieux de plus de 7 jours (un yt-dlp choisi par l'utilisateur n'est pas touche).
    static bool YtStale() {
        string yt = Tools.Ytdlp, local = Path.Combine(App.ToolsDir, "yt-dlp.exe");
        return yt != null && string.Equals(yt, local, StringComparison.OrdinalIgnoreCase)
            && (DateTime.Now - File.GetLastWriteTime(local)).TotalDays > 7;
    }

    public static bool Needed() {
        return Tools.Ffmpeg == null || Tools.Ytdlp == null || Tools.Find("deno.exe", null) == null || YtStale();
    }

    // Lance l'installation sur un fil a part ; done(err) est appele a la fin (err null = succes).
    // Renvoie false si une installation est deja en cours.
    public static bool Start(bool all, Action<int> progress, Action<string> done) {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        Status = Tr.T("t_working");
        Pct = 0;
        new Thread(() => {
            string err = null;
            try { Run(all, v => { Pct = v; if (progress != null) progress(v); }); }
            catch (Exception e) { err = e.Message; App.Log("outils : " + e); }
            Status = err == null ? Tr.T("t_done") : Tr.T("st_error") + " : " + err;
            Interlocked.Exchange(ref busy, 0);
            if (done != null) done(err);
        }) { IsBackground = true }.Start();
        return true;
    }

    static void Run(bool all, Action<int> progress) {
        Directory.CreateDirectory(App.ToolsDir);
        if (all || Tools.Ytdlp == null || YtStale()) Get(U(YtUrl), Path.Combine(App.ToolsDir, "yt-dlp.exe"), 0, 10, progress, U(YtSums), "yt-dlp.exe");
        if (all || Tools.Ffmpeg == null) {
            string zip = Path.Combine(App.ToolsDir, "ffmpeg.zip");
            Get(U(FfUrl), zip, 10, 75, progress, U(FfSums), "ffmpeg-master-latest-win64-gpl.zip");
            using (var z = ZipFile.OpenRead(zip)) {
                foreach (var en in z.Entries) {
                    string n = en.FullName.Replace('\\', '/');
                    if (n.EndsWith("/bin/ffmpeg.exe") || n.EndsWith("/bin/ffprobe.exe")) {
                        string dst = Path.Combine(App.ToolsDir, Path.GetFileName(n));
                        if (File.Exists(dst)) File.Delete(dst);
                        en.ExtractToFile(dst);
                    }
                }
            }
            File.Delete(zip);
        }
        if (all || Tools.Find("deno.exe", null) == null) {
            string dz = Path.Combine(App.ToolsDir, "deno.zip");
            Get(U(DenoUrl), dz, 75, 98, progress, U(DenoUrl) + ".sha256sum", "deno-x86_64-pc-windows-msvc.zip");
            using (var z = ZipFile.OpenRead(dz)) {
                var en = z.Entries.FirstOrDefault(x => x.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                if (en == null) throw new Exception("deno.exe absent du zip");
                string dst = Path.Combine(App.ToolsDir, "deno.exe");
                if (File.Exists(dst)) File.Delete(dst);
                en.ExtractToFile(dst);
            }
            File.Delete(dz);
        }
        progress(100);
    }

    // Telechargement d'un outil : reprise par Range apres une coupure, et un chien de garde qui coupe
    // la connexion si plus rien n'arrive pendant 30 s (une lecture bloquee ne rend jamais la main seule).
    static void Get(string url, string dst, int from, int to, Action<int> progress, string sumsUrl, string sumName) {
        string tmp = dst + ".tmp";
        string expected = ExpectedHash(sumsUrl, sumName);   // avant le telechargement : pas d'empreinte = pas d'installation
        if (File.Exists(tmp)) File.Delete(tmp);
        long total = -1; string last = null; int badHash = 0;
        for (int attempt = 1; attempt <= 6; attempt++) {
            long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            var req = Net.Req(new Item(), url, have > 0 ? have : -1, -1);
            req.Timeout = 60000;
            long lastTick = DateTime.UtcNow.Ticks; bool over = false;
            var dog = new System.Threading.Timer(_ => {
                if (!over && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastTick) > TimeSpan.FromSeconds(30).Ticks) { try { req.Abort(); } catch { } }
            }, null, 5000, 5000);
            try {
                using (var resp = Net.Resp(req)) {
                    bool resumed = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!resumed) have = 0;
                    if (resp.ContentLength > 0) total = have + resp.ContentLength;
                    using (var st = resp.GetResponseStream())
                    using (var fs = new FileStream(tmp, resumed ? FileMode.Append : FileMode.Create)) {
                        long got = have; var buf = new byte[131072]; int n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0) {
                            fs.Write(buf, 0, n); got += n;
                            Interlocked.Exchange(ref lastTick, DateTime.UtcNow.Ticks);
                            Status = Tr.T("t_working") + " " + Fmt.Size(got) + (total > 0 ? " / " + Fmt.Size(total) : "");
                            if (total > 0) progress(from + (int)((to - from) * got / total));
                        }
                    }
                }
                long len = new FileInfo(tmp).Length;
                if (total > 0 && len < total) throw new Exception("incomplete " + len + "/" + total);
                if (len < 100000) throw new Exception("file too small: " + url);
                over = true; dog.Dispose();
                string got2 = Sha256(tmp);
                if (got2 != expected) { File.Delete(tmp); throw new HashFail("empreinte differente pour " + sumName + " (" + got2.Substring(0, 12) + " au lieu de " + expected.Substring(0, 12) + ")"); }
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                return;
            } catch (Exception e) {
                over = true; dog.Dispose();
                // empreinte fausse deux fois : ce n'est pas une coupure, c'est un mauvais fichier -> on arrete (pas 6 essais de 100 Mo)
                if (e is HashFail && ++badHash >= 2) throw;
                last = e.Message; App.Log("outils : essai " + attempt + " " + url + " : " + e.Message);
                Thread.Sleep(2000 * attempt);
            }
        }
        throw new Exception(last ?? "download failed");
    }
}

class ToolsForm : FormBase {
    readonly Label info = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
    readonly Label ffl = new Label { AutoSize = true }, ytl = new Label { AutoSize = true }, dnl = new Label { AutoSize = true }, state = new Label { AutoSize = true, ForeColor = Theme.Muted };
    readonly ProgressBar bar = new ProgressBar { Height = 14, Width = 400 };
    readonly Button go;

    public ToolsForm() : base(App.Name + " - " + Tr.T("t_title"), 420) {
        info.Text = Tr.T("t_info");
        Row("", info);
        Row("ffmpeg", ffl); Row("yt-dlp", ytl); Row("deno", dnl);
        Row("", bar); Row("", state);
        Button(Tr.T("ok"), DialogResult.Cancel, false);
        go = Button(Tr.T("t_install"), DialogResult.None, true);
        go.Click += (s, e) => Install();
        Refresh2();
        if (ToolsSetup.Busy) { go.Enabled = false; state.Text = ToolsSetup.Status; }
    }

    void Refresh2() {
        ffl.Text = Tools.Ffmpeg ?? Tr.T("ffNo"); ffl.ForeColor = Tools.Ffmpeg != null ? Theme.Ok : Theme.Warn;
        ytl.Text = Tools.Ytdlp ?? Tr.T("ytNo"); ytl.ForeColor = Tools.Ytdlp != null ? Theme.Ok : Theme.Warn;
        string dn = Tools.Find("deno.exe", null);
        dnl.Text = dn ?? "-"; dnl.ForeColor = dn != null ? Theme.Ok : Theme.Warn;
    }

    void Install() {
        go.Enabled = false; state.Text = Tr.T("t_working");
        bool started = ToolsSetup.Start(true,
            v => { if (IsHandleCreated) BeginInvoke((Action)(() => { bar.Value = Math.Min(100, v); state.Text = ToolsSetup.Status; })); },
            err => { if (IsHandleCreated) BeginInvoke((Action)(() => {
                if (err == null) bar.Value = 100;
                state.Text = ToolsSetup.Status;
                state.ForeColor = err == null ? Theme.Ok : Theme.Bad;
                go.Enabled = true; Refresh2();
            })); });
        if (!started) state.Text = ToolsSetup.Status;
    }
}
