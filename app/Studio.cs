// ShowLef - interface « Studio clair » (maquette v1 du 19/10/2026) : couleurs, polices embarquees,
// icones dessinees (traces SVG de la maquette), vignettes, et la surface peinte de la fenetre principale.
// C# 5 : pas de $"", pas de ?., pas de nameof.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// Jetons de couleur de v1 (variables CSS) et mise a l'echelle (1 px CSS = S px ecran).
static class St {
    public static float S = 1f;
    public static int P(float v) { return (int)Math.Round(v * S); }
    public static Color Hex(string h) { h = h.TrimStart('#'); return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16)); }
    public static readonly Color Bg = Hex("#eef0f7"), Surface = Color.White, Sunk = Hex("#f5f6fb"), Line = Hex("#e3e6f0");
    public static readonly Color Ink = Hex("#171a2c"), Ink2 = Hex("#4a5070"), Ink3 = Hex("#7a8099");
    public static readonly Color Accent = Hex("#4338ca"), Accent2 = Hex("#6d5efc"), AccentSoft = Hex("#eceafe"), AccentDeep = Hex("#3a30b4");
    public static readonly Color Ok = Hex("#0f9f6e"), OkSoft = Hex("#e3f7ef"), Bad = Hex("#d23a4b"), BadSoft = Hex("#fdecee");
    public static readonly Color NavHover = Hex("#e9ebf4"), Selected = Hex("#f1f0fe"), Lilac = Hex("#dcd8ff"), ToastLink = Hex("#b8b2ff");

    public static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static void Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
    public static void Stroke(Graphics g, RectangleF r, float rad, Color c) {
        var rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using (var p = Round(rr, rad)) using (var pen = new Pen(c, 1)) g.DrawPath(pen, p);
    }
    // Ombre douce (box-shadow) : couches arrondies de plus en plus larges et transparentes.
    public static void Shadow(Graphics g, RectangleF r, float rad, Color c, float dy, float blur, float spread, int alpha) {
        int n = Math.Max(4, (int)(blur / 2));
        for (int i = n; i >= 1; i--) {
            float grow = spread + blur * i / n;
            var rr = new RectangleF(r.X - grow, r.Y - grow + dy, r.Width + 2 * grow, r.Height + 2 * grow);
            if (rr.Width <= 0 || rr.Height <= 0) continue;
            int a = (int)(alpha * (1 - (float)i / (n + 1)) / n * 2.2f);
            Fill(g, rr, rad + Math.Max(0, grow), Color.FromArgb(Math.Max(0, Math.Min(255, a)), c));
        }
    }

    // Texte GDI (ClearType). align : 0 = debut de ligne (droite en hebreu), 1 = fin, 2 = centre.
    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, int align) {
        if (string.IsNullOrEmpty(s) || r.Width <= 0) return;
        var fl = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
               | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
        bool right = (align == 0 && Tr.Rtl) || (align == 1 && !Tr.Rtl);
        if (align == 2) fl |= TextFormatFlags.HorizontalCenter; else if (right) fl |= TextFormatFlags.Right;
        if (Tr.Rtl) fl |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, s, f, r, c, fl);
    }
    // Texte dont le sens est toujours de gauche a droite (chiffres, « 63% », durees).
    public static void TextLtr(Graphics g, string s, Font f, Rectangle r, Color c, bool right) {
        if (string.IsNullOrEmpty(s)) return;
        var fl = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
        if (right) fl |= TextFormatFlags.Right;
        TextRenderer.DrawText(g, s, f, r, c, fl);
    }
    public static int W(string s, Font f) {
        if (string.IsNullOrEmpty(s)) return 0;
        return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
    }
}

// Polices de v1 embarquees dans l'exe (licence OFL, app/fonts) : Rubik (texte, hebreu compris) et Fredoka (le grand %).
// Chargees deux fois : GDI+ (PrivateFontCollection) et GDI (AddFontMemResourceEx), sinon TextRenderer et TextBox ne les voient pas.
static class Fonts {
    [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pb, uint cb, IntPtr pdv, ref uint pc);
    static readonly PrivateFontCollection pfc = new PrivateFontCollection();
    static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
    static bool done;
    static string regular = "Segoe UI", medium = "Segoe UI Semibold", title = "Segoe UI Semibold";

    public static void Init() {
        if (done) return;
        done = true;
        foreach (var n in new[] { "Rubik-Regular.ttf", "Rubik-Medium.ttf", "Fredoka-Variable.ttf" }) {
            try {
                using (var s = typeof(Fonts).Assembly.GetManifestResourceStream(n)) {
                    if (s == null) continue;
                    var b = new byte[s.Length]; s.Read(b, 0, b.Length);
                    IntPtr mem = Marshal.AllocCoTaskMem(b.Length);   // jamais libere : les polices vivent autant que le programme
                    Marshal.Copy(b, 0, mem, b.Length);
                    pfc.AddMemoryFont(mem, b.Length);
                    uint cnt = 0; AddFontMemResourceEx(mem, (uint)b.Length, IntPtr.Zero, ref cnt);
                }
            } catch (System.Exception e) { App.Log("police " + n + " : " + e.Message); }
        }
        var names = pfc.Families.Select(f => f.Name).ToList();
        if (names.Contains("Rubik")) regular = "Rubik";
        if (names.Contains("Rubik Medium")) medium = "Rubik Medium";
        if (names.Contains("Fredoka Medium")) title = "Fredoka Medium"; else if (names.Contains("Fredoka")) title = "Fredoka";
        else if (medium == "Rubik Medium") title = medium;
        App.Log("polices : " + string.Join(", ", names));
    }
    static Font Get(string fam, float px) {
        string k = fam + "|" + px;
        Font f;
        if (cache.TryGetValue(k, out f)) return f;
        FontFamily ff = pfc.Families.FirstOrDefault(x => x.Name == fam);
        try { f = ff != null ? new Font(ff, px * St.S, FontStyle.Regular, GraphicsUnit.Pixel) : new Font(fam, px * St.S, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { f = new Font("Segoe UI", px * St.S, FontStyle.Regular, GraphicsUnit.Pixel); }
        cache[k] = f;
        return f;
    }
    public static Font R(float px) { return Get(regular, px); }
    public static Font M(float px) { return Get(medium, px); }
    public static Font T(float px) { return Get(title, px); }
}

// Icones de v1 : memes traces que le sprite SVG de la maquette (viewBox 24, trait 1,8 arrondi).
// Mini-lecteur de chemins SVG (M L H V C A Z, absolus et relatifs) ; « O cx cy r » = cercle.
static class Ico {
    static readonly Dictionary<string, string> D = new Dictionary<string, string> {
        { "dl", "M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19.5h14" },
        { "play", "M8 5.5v13l10.5-6.5z" },
        { "pause", "M9 6v12M15 6v12" },
        { "folder", "M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z" },
        { "trash", "M4.5 7h15M9.5 7V5h5v2M6.5 7l.8 12h9.4l.8-12M10.5 11v5M13.5 11v5" },
        { "gear", "O12 12 3M19 12a7 7 0 0 0-.1-1.2l2-1.5-2-3.4-2.3.9a7 7 0 0 0-2-1.2L14.2 3h-4.4l-.4 2.6a7 7 0 0 0-2 1.2l-2.3-.9-2 3.4 2 1.5A7 7 0 0 0 5 12c0 .4 0 .8.1 1.2l-2 1.5 2 3.4 2.3-.9c.6.5 1.3.9 2 1.2l.4 2.6h4.4l.4-2.6c.7-.3 1.4-.7 2-1.2l2.3.9 2-3.4-2-1.5c.1-.4.1-.8.1-1.2z" },
        { "film", "M6 5h12a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5h-12a2.5 2.5 0 0 1-2.5-2.5v-9a2.5 2.5 0 0 1 2.5-2.5zM3.5 9.5h17M8 5v4.5M16 5v4.5" },
        { "note", "M9 17.5V6l10-2v11.5O6.5 17.5 2.5O16.5 15.5 2.5" },
        { "x", "M6 6l12 12M18 6 6 18" },
        { "star", "M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8L3.5 9.7l5.9-.9z" },
        { "starf", "M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8L3.5 9.7l5.9-.9z" },
        { "bulb", "M9 18h6M10 21h4M12 3a6 6 0 0 0-3.6 10.8c.6.5 1 1.2 1 2V16h5.2v-.2c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z" },
        { "check", "m5 12.5 4.5 4.5L19 7.5" },
        { "alert", "M12 8v5M12 16.5v.5O12 12 9" },
        { "search", "O11 11 6.5M16 16l4 4" },
        { "link", "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" },
        { "clock", "O12 12 8.5M12 7.5V12l3 2" },
        { "list", "M8 6.5h12M8 12h12M8 17.5h12M4 6.5h.01M4 12h.01M4 17.5h.01" },
        { "user", "O12 8.5 3.5M5 20a7 7 0 0 1 14 0" },
        { "logout", "M14 4.5h3.5a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H14M10 16.5 5.5 12 10 7.5M5.5 12H15" },
        { "retry", "M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4" },
    };
    static readonly Dictionary<string, GraphicsPath> parsed = new Dictionary<string, GraphicsPath>();

    public static void Draw(Graphics g, string name, RectangleF r, Color c) { Draw(g, name, r, c, 1.8f); }
    public static void Draw(Graphics g, string name, RectangleF r, Color c, float stroke) {
        GraphicsPath src;
        if (!parsed.TryGetValue(name, out src)) { string d; if (!D.TryGetValue(name, out d)) return; parsed[name] = src = Parse(d); }
        using (var p = (GraphicsPath)src.Clone())
        using (var m = new Matrix()) {
            float k = r.Width / 24f;
            m.Translate(r.X, r.Y); m.Scale(k, k);
            p.Transform(m);
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (name == "play" || name == "starf") using (var b = new SolidBrush(c)) g.FillPath(b, p);
            else using (var pen = new Pen(c, stroke * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }
    }

    static GraphicsPath Parse(string d) {
        var toks = Regex.Matches(d, @"[MmLlHhVvCcAaZzO]|-?(?:\d+\.?\d*|\.\d+)").Cast<Match>().Select(x => x.Value).ToList();
        var p = new GraphicsPath();
        int i = 0; float cx = 0, cy = 0, sx = 0, sy = 0; char cmd = 'M';
        Func<float> num = () => float.Parse(toks[i++], CultureInfo.InvariantCulture);
        while (i < toks.Count) {
            string t = toks[i];
            if (char.IsLetter(t[0])) { cmd = t[0]; i++; }
            else if (cmd == 'M') cmd = 'L'; else if (cmd == 'm') cmd = 'l';
            bool rel = char.IsLower(cmd);
            switch (char.ToUpper(cmd)) {
                case 'M': { float x = num(), y = num(); if (rel) { x += cx; y += cy; } p.StartFigure(); cx = sx = x; cy = sy = y; break; }
                case 'L': { float x = num(), y = num(); if (rel) { x += cx; y += cy; } p.AddLine(cx, cy, x, y); cx = x; cy = y; break; }
                case 'H': { float x = num(); if (rel) x += cx; p.AddLine(cx, cy, x, cy); cx = x; break; }
                case 'V': { float y = num(); if (rel) y += cy; p.AddLine(cx, cy, cx, y); cy = y; break; }
                case 'C': {
                    float x1 = num(), y1 = num(), x2 = num(), y2 = num(), x = num(), y = num();
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    p.AddBezier(cx, cy, x1, y1, x2, y2, x, y); cx = x; cy = y; break;
                }
                case 'A': {
                    float rx = num(), ry = num(), rot = num(), la = num(), sw = num(), x = num(), y = num();
                    if (rel) { x += cx; y += cy; }
                    Arc(p, cx, cy, rx, ry, rot, la != 0, sw != 0, x, y); cx = x; cy = y; break;
                }
                case 'Z': p.CloseFigure(); cx = sx; cy = sy; break;
                case 'O': { float x = num(), y = num(), r = num(); p.StartFigure(); p.AddEllipse(x - r, y - r, 2 * r, 2 * r); p.StartFigure(); break; }
                default: i++; break;
            }
        }
        return p;
    }

    static double Ang(double ux, double uy, double vx, double vy) { return Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy); }
    // Arc elliptique SVG -> courbes de Bezier (parametrisation par le centre, segments de 90 degres au plus).
    static void Arc(GraphicsPath p, float x1, float y1, double rx, double ry, double rotDeg, bool large, bool sweep, float x2, float y2) {
        if (rx == 0 || ry == 0) { p.AddLine(x1, y1, x2, y2); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = rotDeg * Math.PI / 180, co = Math.Cos(phi), si = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double xp = co * dx + si * dy, yp = -si * dx + co * dy;
        double lam = xp * xp / (rx * rx) + yp * yp / (ry * ry);
        if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }
        double rxs = rx * rx, rys = ry * ry;
        double num = rxs * rys - rxs * yp * yp - rys * xp * xp, den = rxs * yp * yp + rys * xp * xp;
        double c = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (large == sweep) c = -c;
        double cxp = c * rx * yp / ry, cyp = -c * ry * xp / rx;
        double ccx = co * cxp - si * cyp + (x1 + x2) / 2.0, ccy = si * cxp + co * cyp + (y1 + y2) / 2.0;
        double t1 = Ang(1, 0, (xp - cxp) / rx, (yp - cyp) / ry);
        double dt = Ang((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2)));
        double d = dt / n, k = 4.0 / 3 * Math.Tan(d / 4), t = t1;
        Func<double, double, PointF> map = (ux, uy) => new PointF((float)(ccx + rx * ux * co - ry * uy * si), (float)(ccy + rx * ux * si + ry * uy * co));
        var prev = new PointF(x1, y1);
        for (int i = 0; i < n; i++) {
            double c1 = Math.Cos(t), s1 = Math.Sin(t), c2 = Math.Cos(t + d), s2 = Math.Sin(t + d);
            var e = map(c2, s2);
            p.AddBezier(prev, map(c1 - k * s1, s1 + k * c1), map(c2 + k * s2, s2 - k * c2), e);
            prev = e; t += d;
        }
    }
}

// Vignettes : image envoyee par l'extension (champ « thumb » de add / capture_begin), sinon image extraite du
// fichier video fini (ffmpeg, a 10 % de la duree). Cache disque %LOCALAPPDATA%\ShowLef\thumbs\<id>.jpg (208 x 117, 16:9).
// Duree (ffprobe) sur le fichier fini, gardee dans l'element. Une seule tache de fond ; un echec n'est pas retente.
static class Thumbs {
    public const int TW = 208, TH = 117;
    static readonly Dictionary<string, Bitmap> mem = new Dictionary<string, Bitmap>();
    static readonly HashSet<string> tried = new HashSet<string>(), probed = new HashSet<string>();
    static readonly Queue<Item> queue = new Queue<Item>();
    static readonly HashSet<string> queued = new HashSet<string>();
    static Thread worker;
    public static event Action Changed;
    public static string Dir { get { return Path.Combine(App.Dir, "thumbs"); } }
    static string FileOf(Item it) { return Path.Combine(Dir, it.Id + ".jpg"); }
    static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".wma" };
    static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi", ".flv", ".ts", ".3gp" };

    public static bool IsAudio(Item it) {
        string e = Path.GetExtension((it.Name ?? "").TrimStart('~')).ToLowerInvariant();
        return it.AudioOnly || AudioExt.Contains(e);
    }
    public static bool IsVideo(Item it) {
        if (IsAudio(it)) return false;
        string e = Path.GetExtension((it.Name ?? "").TrimStart('~')).ToLowerInvariant();
        return VideoExt.Contains(e) || it.Kind == "hls" || it.Kind == "dash" || it.Kind == "page" || it.Kind == "capture";
    }

    // Image deja prete (null sinon ; la fabrication part en arriere-plan si elle a un sens).
    public static Bitmap Get(Item it) {
        lock (mem) {
            Bitmap b;
            if (mem.TryGetValue(it.Id, out b)) return b;
            string f = FileOf(it);
            if (File.Exists(f)) {
                try { b = Load(f); mem[it.Id] = b; return b; } catch { try { File.Delete(f); } catch { } }
            }
        }
        bool want = false;
        lock (tried) {
            if (!tried.Contains(it.Id) && (!string.IsNullOrEmpty(it.Thumb) || (it.Status == "done" && IsVideo(it)))) want = true;
            if (it.Status == "done" && it.Duration <= 0 && !probed.Contains(it.Id) && (IsVideo(it) || IsAudio(it))) want = true;
        }
        if (want) Enqueue(it);
        return null;
    }

    static Bitmap Load(string f) {
        // en memoire : Image.FromFile verrouille le fichier
        using (var ms = new MemoryStream(File.ReadAllBytes(f))) using (var img = Image.FromStream(ms)) return new Bitmap(img);
    }

    static void Enqueue(Item it) {
        lock (queue) {
            if (queued.Contains(it.Id)) return;
            queued.Add(it.Id); queue.Enqueue(it);
            if (worker == null) { worker = new Thread(Work) { IsBackground = true, Name = "thumbs", Priority = ThreadPriority.BelowNormal }; worker.Start(); }
        }
    }

    static void Work() {
        while (true) {
            Item it;
            lock (queue) { if (queue.Count == 0) { worker = null; return; } it = queue.Dequeue(); queued.Remove(it.Id); }
            bool changed = false;
            try {
                bool hasFile = it.Status == "done" && File.Exists(it.FilePath);
                bool probe;
                lock (tried) { probe = hasFile && it.Duration <= 0 && !probed.Contains(it.Id); if (probe) probed.Add(it.Id); }
                if (probe) { double d = Probe(it.FilePath); if (d > 0) { it.Duration = d; changed = true; } }
                bool again;
                lock (tried) { again = tried.Contains(it.Id); }
                if (!File.Exists(FileOf(it)) && !again && (!string.IsNullOrEmpty(it.Thumb) || (hasFile && IsVideo(it)))) {
                    lock (tried) tried.Add(it.Id);
                    Directory.CreateDirectory(Dir);
                    if (!string.IsNullOrEmpty(it.Thumb) && FromUrl(it)) changed = true;
                    else if (hasFile && IsVideo(it) && FromVideo(it)) changed = true;
                    else if (!hasFile) lock (tried) tried.Remove(it.Id);   // pas encore fini : on retentera avec le fichier
                }
            } catch (System.Exception e) { App.Log("vignette " + it.Id + " : " + e.Message); }
            if (changed && Changed != null) try { Changed(); } catch { }
        }
    }

    static bool FromUrl(Item it) {
        try {
            var req = Net.Req(it, it.Thumb, -1, -1);
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.Accept = "image/avif,image/webp,image/*,*/*;q=0.8";
            byte[] data;
            using (var resp = Net.Resp(req))
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream()) {
                var buf = new byte[65536]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 4 * 1024 * 1024) throw new System.Exception("image trop grosse"); }
                data = ms.ToArray();
            }
            try {
                using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms)) { SaveCrop(img, FileOf(it)); return true; }
            } catch {
                // WebP / AVIF : GDI+ ne sait pas les lire, ffmpeg oui
                string ff = Tools.Ffmpeg; if (ff == null) return false;
                string tmp = Path.Combine(Dir, it.Id + ".src"), png = Path.Combine(Dir, it.Id + ".png");
                File.WriteAllBytes(tmp, data);
                try {
                    Run(ff, "-v error -y -i \"" + tmp + "\" -frames:v 1 \"" + png + "\"", 20000);
                    if (!File.Exists(png)) return false;
                    using (var img = Load(png)) SaveCrop(img, FileOf(it));
                    return true;
                } finally { try { File.Delete(tmp); } catch { } try { File.Delete(png); } catch { } }
            }
        } catch (System.Exception e) { App.Log("vignette (adresse) " + it.Id + " : " + e.Message); return false; }
    }

    static bool FromVideo(Item it) {
        string ff = Tools.Ffmpeg; if (ff == null) return false;
        double at = it.Duration > 0 ? it.Duration * 0.10 : 3;
        string png = Path.Combine(Dir, it.Id + ".png");
        try {
            Run(ff, "-v error -y -ss " + at.ToString("0.###", CultureInfo.InvariantCulture) + " -i \"" + it.FilePath + "\" -frames:v 1 -vf scale=" + TW + ":-2 \"" + png + "\"", 30000);
            if (!File.Exists(png)) Run(ff, "-v error -y -i \"" + it.FilePath + "\" -frames:v 1 -vf scale=" + TW + ":-2 \"" + png + "\"", 30000);
            if (!File.Exists(png)) return false;
            using (var img = Load(png)) SaveCrop(img, FileOf(it));
            return true;
        } finally { try { File.Delete(png); } catch { } }
    }

    static double Probe(string file) {
        string ff = Tools.Ffmpeg; if (ff == null) return 0;
        string fp = Path.Combine(Path.GetDirectoryName(ff), "ffprobe.exe");
        if (!File.Exists(fp)) fp = Tools.Find("ffprobe.exe", null);
        if (fp == null) return 0;
        string o = Run(fp, "-v error -show_entries format=duration -of default=nw=1:nk=1 \"" + file + "\"", 20000);
        double d; return double.TryParse((o ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    static string Run(string exe, string args, int ms) {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)) {
            p.ErrorDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } }
            return o;
        }
    }

    // Recadrage 16:9 au centre, 208 x 117.
    static void SaveCrop(Image img, string dst) {
        float ratio = (float)TW / TH;
        RectangleF src;
        if ((float)img.Width / img.Height > ratio) { float w = img.Height * ratio; src = new RectangleF((img.Width - w) / 2, 0, w, img.Height); }
        else { float h = img.Width / ratio; src = new RectangleF(0, (img.Height - h) / 2, img.Width, h); }
        using (var b = new Bitmap(TW, TH, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(b)) {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, new Rectangle(0, 0, TW, TH), src, GraphicsUnit.Pixel);
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
            var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
            string tmp = dst + ".tmp";
            b.Save(tmp, enc, ps);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }
    }
}

// Textes calcules pour une ligne (etat, type, qualite, « il y a 5 min », erreur lisible).
static class Ui {
    public static bool Active(Item it) { return it.Status == "downloading" || it.Status == "merging"; }
    public static bool InProgress(Item it) { return it.Status != "done" && it.Status != "error"; }
    static readonly string[] MediaExt = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi", ".ts", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".wma" };
    public static bool IsMedia(Item it) { return MediaExt.Contains(Path.GetExtension((it.Name ?? "").TrimStart('~')).ToLowerInvariant()); }

    public static string Chip(Item it, out Color fg, out Color bg) {
        switch (it.Status) {
            case "done": fg = St.Ok; bg = St.OkSoft; return Tr.T("c_done");
            case "error": fg = St.Bad; bg = St.BadSoft; return Tr.T("c_error");
            case "queued": fg = St.Accent; bg = St.AccentSoft; return Mgr.Held ? Tr.T("c_sched") : Tr.T("c_queued");
            case "paused": fg = St.Ink2; bg = St.Sunk; return Tr.T("c_paused");
            case "merging": fg = St.Accent; bg = St.AccentSoft; return Tr.T("c_merging");
            default: fg = St.Accent; bg = St.AccentSoft; return Tr.T("c_dl");
        }
    }
    public static string TypeText(Item it) { return Thumbs.IsAudio(it) ? Tr.T("t_audio") : Thumbs.IsVideo(it) ? Tr.T("t_video") : Tr.T("t_file"); }
    public static string QualSize(Item it) {
        string q = null;
        if (Thumbs.IsAudio(it)) { string e = Path.GetExtension((it.Name ?? "").TrimStart('~')).TrimStart('.').ToUpperInvariant(); if (e.Length > 0 && e.Length <= 4) q = e; }
        else if (!string.IsNullOrEmpty(it.Quality) && it.Quality.All(char.IsDigit)) q = it.Quality + "p";
        string sz = it.Total > 0 ? Fmt.Size(it.Total) : null;
        // en hebreu, la marque RLM empeche « MP3 · 1,45 » de former un seul bloc latin qui rejetterait l'unite a l'autre bout
        if (q != null && sz != null) return q + (Tr.Rtl ? "\u200F · " : " · ") + sz;
        return q ?? sz;
    }
    public static string Ago(DateTime t) {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return Tr.T("ago0");
        if (d.TotalMinutes < 2) return Tr.T("ago1m");
        if (d.TotalHours < 1) return string.Format(Tr.T("agoM"), (int)d.TotalMinutes);
        if (d.TotalHours < 2) return Tr.T("ago1h");
        if (d.TotalHours < 3 && Tr.Rtl) return Tr.T("ago2h");
        if (t.Date == DateTime.Today) return string.Format(Tr.T("agoH"), (int)d.TotalHours);
        if (t.Date == DateTime.Today.AddDays(-1)) return string.Format(Tr.T("yestAt"), t.ToString("HH:mm"));
        return t.ToString("dd/MM/yyyy");
    }
    public static string Eta(double s) {
        if (s <= 0 || double.IsInfinity(s) || double.IsNaN(s)) return null;
        if (s < 60) return string.Format(Tr.T("etaS"), (int)Math.Ceiling(s));
        return string.Format(Tr.T("etaM"), Fmt.Eta(s));
    }
    public static string Dur(double s) {
        if (s <= 0) return null;
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : t.Minutes + ":" + t.Seconds.ToString("00");
    }
    // Erreur lisible ; le texte technique reste dans l'infobulle. En hebreu : jamais de lettres latines.
    // Une phrase + un geste, par code (le meme code part a l'extension, qui le dit dans SA langue).
    public static string Error(Item it) {
        string code = ErrCode.Of(it.Error);
        if (code != null) return Tr.T("e_" + code);
        string e = it.Error ?? "";
        if (e.Length == 0 || (Tr.Rtl && Regex.IsMatch(e, "[A-Za-z]{3}"))) return Tr.T("e_generic");
        return e;
    }
    // Dossier affiche « Telechargements › ShowLef » (deux derniers niveaux).
    public static string FolderLabel(string path) {
        try {
            string p = (path ?? "").TrimEnd('\\');
            string n = Path.GetFileName(p), parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "");
            Func<string, string> loc = s => string.Equals(s, "Downloads", StringComparison.OrdinalIgnoreCase) ? Tr.T("downloadsDir") : string.Equals(s, App.Name, StringComparison.OrdinalIgnoreCase) ? Tr.T("appName") : s;
            if (string.IsNullOrEmpty(parent)) return loc(n);
            return loc(parent) + (Tr.Rtl ? " ‹ " : " › ") + loc(n);
        } catch { return path; }
    }
}

// Surface peinte de la fenetre principale : menu lateral, recherche, bande « en cours », liste par date, toast, barre d'etat.
// Tout est calcule de gauche a droite puis retourne en miroir pour l'hebreu par une seule fonction (Mir), utilisee
// par le dessin ET par les zones de clic : ce qu'on voit est exactement ce qu'on clique.
class Surface : Control {
    public Rectangle AccountRect;   // bouton du compte (le panneau se pose dessous)
    class Zone { public Rectangle R; public string Key; public Item It; public string Tip; }
    class Row { public Item It; public string Group; public int Y, H; }

    public Action<string, Item> OnAction;          // touche d'action, element (null = selection / general)
    public Action<Point> OnContext;
    public readonly TextBox Search = new TextBox();
    public readonly ExtensionStatusButton ExtensionStatus = new ExtensionStatusButton();
    public string Nav = "all";
    public List<Item> Items = new List<Item>();
    public readonly HashSet<string> Sel = new HashSet<string>();
    string anchor;
    public string ToastText, StatusLeft, StatusRight, StatusRightKey;
    public Color StatusRightColor = St.Ink3;
    public bool ToastUndo;
    readonly List<Zone> zones = new List<Zone>();
    readonly List<Row> rows = new List<Row>();
    Item bandItem; int bandOthers;
    Zone hover;
    int scroll, contentH, viewTop, viewBottom;
    bool dragging; int dragY0, dragScroll0;
    readonly ToolTip tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100 };
    string lastTip;
    Bitmap logo;
    long freeBytes = -1; DateTime freeAt;
    Dictionary<string, int> counts = new Dictionary<string, int>();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    public Surface() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.White;
        Search.BorderStyle = BorderStyle.None;
        Search.BackColor = St.Sunk; Search.ForeColor = St.Ink;
        Search.Font = Fonts.R(14);
        Search.GotFocus += (s, e) => { Search.BackColor = Color.White; Invalidate(); };
        Search.LostFocus += (s, e) => { Search.BackColor = St.Sunk; Invalidate(); };
        Search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Search.Text = ""; e.SuppressKeyPress = true; } if (e.KeyCode == Keys.Down) { Focus(); e.Handled = true; } };
        Search.TextChanged += (s, e) => { scroll = 0; Invalidate(); };
        // le texte d'invite se perd quand la poignee est recreee (changement de sens) : on le repose a chaque creation
        Search.HandleCreated += (s2, e2) => SendMessage(Search.Handle, 0x1501, (IntPtr)1, Tr.T("searchPh"));
        Controls.Add(Search);
        ExtensionStatus.Click += (s, e) => { if (OnAction != null) OnAction("extension", null); };
        Controls.Add(ExtensionStatus);
        logo = IcoImage();
        Thumbs.Changed += () => { try { if (IsHandleCreated) BeginInvoke((Action)Invalidate); } catch { } };
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Cue(); }

    // Plus grande image du .ico embarque (entrees PNG lues directement : Icon.ToBitmap les rend mal).
    public static Bitmap IcoImage() {
        try {
            byte[] b;
            using (var st = typeof(Surface).Assembly.GetManifestResourceStream("ShowLef.ico")) { if (st == null) return null; b = new byte[st.Length]; st.Read(b, 0, b.Length); }
            int n = BitConverter.ToUInt16(b, 4), best = -1, bestW = 0;
            for (int i = 0; i < n; i++) { int w = b[6 + 16 * i]; if (w == 0) w = 256; if (w > bestW) { bestW = w; best = i; } }
            int len = BitConverter.ToInt32(b, 6 + 16 * best + 8), off = BitConverter.ToInt32(b, 6 + 16 * best + 12);
            using (var ms = new MemoryStream(b, off, len)) using (var img = Image.FromStream(ms)) return new Bitmap(img);
        } catch { return null; }
    }
    static string Title(Item it) {
        string n = (it.Name ?? "").TrimStart('~');
        string e = Path.GetExtension(n);
        // titre sans extension (le type est dit dans la ligne meta), comme v1
        return e.Length >= 2 && e.Length <= 5 && n.Length > e.Length ? n.Substring(0, n.Length - e.Length) : n;
    }
    public void Cue() {
        Search.RightToLeft = Tr.Rtl ? RightToLeft.Yes : RightToLeft.No;
        if (Search.IsHandleCreated) SendMessage(Search.Handle, 0x1501, (IntPtr)1, Tr.T("searchPh"));
        Invalidate();
    }

    // ---------- donnees ----------
    public void SetData(List<Item> all) {
        Items = all;
        counts = new Dictionary<string, int> {
            { "all", all.Count }, { "active", all.Count(Ui.InProgress) }, { "done", all.Count(i => i.Status == "done") },
            { "error", all.Count(i => i.Status == "error") }, { "video", all.Count(Thumbs.IsVideo) }, { "audio", all.Count(Thumbs.IsAudio) },
            { "sched", Mgr.Held ? all.Count(i => i.Status == "queued") : 0 }
        };
        var act = all.Where(Ui.Active).ToList();
        bandItem = act.FirstOrDefault(); bandOthers = Math.Max(0, act.Count - 1);
        Invalidate();
    }
    bool Pass(Item it) {
        switch (Nav) {
            case "active": if (!Ui.InProgress(it)) return false; break;
            case "done": if (it.Status != "done") return false; break;
            case "error": if (it.Status != "error") return false; break;
            case "video": if (!Thumbs.IsVideo(it)) return false; break;
            case "audio": if (!Thumbs.IsAudio(it)) return false; break;
            case "sched": if (!(Mgr.Held && it.Status == "queued")) return false; break;
        }
        string f = Search.Text.Trim();
        if (f.Length > 0 && (it.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && (it.Title ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }
    public List<Item> VisibleItems() { return rows.Where(r => r.It != null).Select(r => r.It).ToList(); }
    public List<Item> Selected() {
        var r = rows.Where(x => x.It != null && Sel.Contains(x.It.Id)).Select(x => x.It).ToList();
        if (bandItem != null && Sel.Contains(bandItem.Id)) r.Insert(0, bandItem);
        return r;
    }
    public void SelectOnly(string id) {
        Sel.Clear(); Sel.Add(id); anchor = id;
        if (bandItem == null || bandItem.Id != id) {
            Layout2();
            if (!rows.Any(r => r.It != null && r.It.Id == id)) { Nav = "all"; Search.Text = ""; }
            EnsureVisible(id);
        }
        Invalidate();
    }
    public void EnsureVisible(string id) {
        Layout2();
        var r = rows.FirstOrDefault(x => x.It != null && x.It.Id == id); if (r == null) return;
        int vh = viewBottom - viewTop;
        if (r.Y < scroll) scroll = r.Y; else if (r.Y + r.H > scroll + vh) scroll = r.Y + r.H - vh;
        ClampScroll(); Invalidate();
    }

    // ---------- geometrie ----------
    int SideW { get { return St.P(210); } }
    Rectangle Mir(Rectangle r) { return Tr.Rtl ? new Rectangle(Width - r.Right, r.Y, r.Width, r.Height) : r; }
    Zone Add(Rectangle logical, string key, Item it, string tipText) {
        var z = new Zone { R = Mir(logical), Key = key, It = it, Tip = tipText }; zones.Add(z); return z;
    }
    bool Hot(string key, Item it) { return hover != null && hover.Key == key && hover.It == it; }

    int StatusH { get { return St.P(37); } }
    int ToastH { get { return ToastText != null ? St.P(4 + 40 + 18) : 0; } }
    int TopH { get { return St.P(64); } }
    int BandH { get { return bandItem != null ? St.P(16 + 94 + 6) : 0; } }
    const int RowH = 78, RowDoneH = 62, GroupH = 37;   // cartes terminees compactes (0.7.33) : la place va au titre et aux actifs

    // Lignes visibles, groupees par date (Aujourd'hui / Hier / Plus ancien).
    void Layout2() {
        rows.Clear();
        viewTop = TopH + BandH; viewBottom = Height - StatusH - ToastH;
        int y = 0; string g = null;
        foreach (var it in Items) {
            if (it == bandItem || !Pass(it)) continue;
            string grp = it.Added.Date == DateTime.Today ? "g_today" : it.Added.Date == DateTime.Today.AddDays(-1) ? "g_yest" : "g_older";
            if (grp != g) { rows.Add(new Row { It = null, Group = grp, Y = y, H = St.P(GroupH) }); y += St.P(GroupH); g = grp; }
            int rh = it.Status == "done" ? RowDoneH : RowH;
            rows.Add(new Row { It = it, Y = y, H = St.P(rh) }); y += St.P(rh);
        }
        contentH = y + St.P(14);
        ClampScroll();
    }
    void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, contentH - (viewBottom - viewTop)))); }

    // ---------- dessin ----------
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        zones.Clear();
        g.Clear(Color.White);
        Layout2();
        PaintSide(g);
        PaintTop(g);
        PaintBand(g);
        PaintList(g);
        PaintToast(g);
        PaintStatus(g);
    }

    void PaintSide(Graphics g) {
        int w = SideW;
        var side = Mir(new Rectangle(0, 0, w, Height));
        using (var b = new SolidBrush(St.Sunk)) g.FillRectangle(b, side);
        using (var p = new Pen(St.Line)) { int x = Tr.Rtl ? side.X : side.Right - 1; g.DrawLine(p, x, 0, x, Height); }
        int pad = St.P(12), y = St.P(16);
        // marque : logo + nom
        var lg = new Rectangle(pad + St.P(4), y, St.P(24), St.P(24));
        if (logo != null) { var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logo, Mir(lg)); g.InterpolationMode = im; }
        St.Text(g, Tr.T("appName"), Fonts.T(17), Mir(new Rectangle(lg.Right + St.P(9), y - St.P(1), w - lg.Right - St.P(20), St.P(26))), St.Ink, 0);
        y += St.P(24 + 16);
        // gros bouton « Ajouter un lien »
        var add = new Rectangle(pad, y, w - 2 * pad, St.P(40));
        var ad = Mir(add);
        St.Shadow(g, ad, St.P(11), St.Accent, St.P(6), St.P(10), -St.P(6), 120);
        St.Fill(g, ad, St.P(11), Hot("add", null) ? St.AccentDeep : St.Accent);
        string at = Tr.T("addLink"); var af = Fonts.M(14);
        int tw = St.W(at, af), iw = St.P(18), gap = St.P(8), total = iw + gap + tw;
        int sx = add.X + (add.Width - total) / 2;
        Ico.Draw(g, "link", Mir(new Rectangle(sx, add.Y + (add.Height - iw) / 2, iw, iw)), Color.White);
        St.Text(g, at, af, Mir(new Rectangle(sx + iw + gap, add.Y, tw + 2, add.Height)), Color.White, 0);
        Add(add, "add", null, null);
        y = add.Bottom + St.P(14);
        string[][] nav = { new[] { "all", "list", "f_all" }, new[] { "active", "dl", "f_active" }, new[] { "done", "check", "f_done" }, new[] { "error", "alert", "f_error" }, null,
                           new[] { "video", "film", "f_video" }, new[] { "audio", "note", "f_audio" }, new[] { "sched", "clock", "f_sched" } };
        foreach (var n in nav) {
            if (n == null) {
                y += St.P(12);
                using (var p = new Pen(St.Line)) { var l = Mir(new Rectangle(pad + St.P(4), y, w - 2 * pad - St.P(8), 1)); g.DrawLine(p, l.X, y, l.Right, y); }
                y += St.P(13); continue;
            }
            var r = new Rectangle(pad, y, w - 2 * pad, St.P(36));
            bool on = Nav == n[0], hot = Hot("nav:" + n[0], null);
            var rd = Mir(r);
            if (on) { St.Fill(g, new RectangleF(rd.X, rd.Y + 1, rd.Width, rd.Height), St.P(9), Color.FromArgb(22, St.Ink)); St.Fill(g, rd, St.P(9), Color.White); }
            else if (hot) St.Fill(g, rd, St.P(9), St.NavHover);
            Ico.Draw(g, n[1], Mir(new Rectangle(r.X + St.P(10), r.Y + St.P(9), St.P(18), St.P(18))), on ? St.Ink : St.Ink2);
            int c; counts.TryGetValue(n[0], out c);
            string cs = c.ToString(); var cf = Fonts.R(12); int cw = St.W(cs, cf);
            var crect = new Rectangle(r.Right - St.P(10) - cw, r.Y, cw + 1, r.Height);
            St.TextLtr(g, cs, cf, Mir(crect), on ? St.Accent : St.Ink3, false);
            St.Text(g, Tr.T(n[2]), on ? Fonts.M(14) : Fonts.R(14), Mir(new Rectangle(r.X + St.P(38), r.Y, crect.X - r.X - St.P(44), r.Height)), on ? St.Ink : St.Ink2, 0);
            Add(r, "nav:" + n[0], null, null);
            y += St.P(38);
        }
        // carte « dossier des telechargements » + espace libre reel (relu toutes les 10 s)
        if ((DateTime.Now - freeAt).TotalSeconds > 10) {
            freeAt = DateTime.Now;
            try { freeBytes = new DriveInfo(Path.GetPathRoot(Settings.Current.Folder)).AvailableFreeSpace; } catch { freeBytes = -1; }
        }
        ExtensionStatus.Bounds = Mir(new Rectangle(pad, y + St.P(8), w - 2 * pad, St.P(58)));
        y += St.P(66);
        int ch = St.P(10 + 19 + 17 + 17 + 10);
        var card = new Rectangle(pad, Math.Max(y + St.P(12), Height - ch - St.P(16)), w - 2 * pad, ch);
        // lien « Contact et avis » au-dessus de la carte du dossier
        var fb = new Rectangle(pad, card.Y - St.P(34), w - 2 * pad, St.P(28));
        if (fb.Y > y + St.P(12)) {
            var fbd = Mir(fb);
            bool fh = Hot("feedback", null);
            if (fh) St.Fill(g, fbd, St.P(8), St.Sunk);
            St.Text(g, "\u2605  " + Tr.T("feedbackLink"), Fonts.M(13), Mir(new Rectangle(fb.X + St.P(10), fb.Y, fb.Width - St.P(20), fb.Height)), fh ? St.Accent : St.Ink2, 0);
            Add(fb, "feedback", null, null);
        }
        if (card.Bottom < Height) {
            var cd = Mir(card);
            St.Fill(g, cd, St.P(10), Hot("folderCard", null) ? St.Sunk : Color.White);
            St.Stroke(g, cd, St.P(10), St.Line);
            int ix = card.X + St.P(10), iw2 = card.Width - St.P(20), cy = card.Y + St.P(10);
            St.Text(g, Tr.T("dlFolder"), Fonts.M(13), Mir(new Rectangle(ix, cy, iw2, St.P(19))), St.Ink, 0); cy += St.P(19);
            St.Text(g, Ui.FolderLabel(Settings.Current.Folder), Fonts.R(12), Mir(new Rectangle(ix, cy, iw2, St.P(17))), St.Ink3, 0); cy += St.P(17);
            if (freeBytes >= 0) St.Text(g, string.Format(Tr.T("freeSpace"), Fmt.Size(freeBytes)), Fonts.R(12), Mir(new Rectangle(ix, cy, iw2, St.P(17))), St.Ink3, 0);
            Add(card, "folderCard", null, Settings.Current.Folder);
        }
    }

    void PaintTop(Graphics g) {
        int x0 = SideW, w = Width - SideW;
        using (var p = new Pen(St.Line)) { var l = Mir(new Rectangle(x0, TopH - 1, w, 1)); g.DrawLine(p, l.X, l.Y, l.Right, l.Y); }
        int h = St.P(36), y = St.P(14);
        var gear = new Rectangle(x0 + w - St.P(18) - h, y, h, h);
        // compte (0.7.57, ShowF 25/10) : initiale du compte connecte ; clic = nom, adresse, deconnexion
        var acc = new Rectangle(gear.X - St.P(8) - h, y, h, h);
        var sr = new Rectangle(x0 + St.P(18), y, acc.X - St.P(10) - x0 - St.P(18), h);
        var sd = Mir(sr);
        bool focus = Search.Focused;
        St.Fill(g, sd, St.P(10), focus ? Color.White : St.Sunk);
        if (focus) using (var pth = St.Round(new RectangleF(sd.X + 0.5f, sd.Y + 0.5f, sd.Width - 1, sd.Height - 1), St.P(10))) using (var pen = new Pen(St.Accent2, Math.Max(1, St.S))) g.DrawPath(pen, pth);
        Ico.Draw(g, "search", Mir(new Rectangle(sr.X + St.P(12), y + St.P(9), St.P(18), St.P(18))), St.Ink3);
        var tb = new Rectangle(sr.X + St.P(12 + 18 + 8), y + (h - Search.Height) / 2, sr.Width - St.P(12 + 18 + 8 + 12), Search.Height);
        var tbd = Mir(tb);
        if (Search.Bounds != tbd) Search.Bounds = tbd;
        var gd = Mir(gear);
        bool gh = Hot("gear", null);
        St.Fill(g, gd, St.P(10), Color.White);
        St.Stroke(g, gd, St.P(10), gh ? Color.FromArgb(207, 210, 246) : St.Line);
        Ico.Draw(g, "gear", new Rectangle(gd.X + St.P(9), gd.Y + St.P(9), St.P(18), St.P(18)), gh ? St.Accent : St.Ink2);
        Add(gear, "gear", null, Tr.T("settings"));
        var acd = Mir(acc); AccountRect = acd;
        bool ah = Hot("account", null), signed = Gate.Open && !string.IsNullOrEmpty(Account.Name);
        if (signed) {
            Avatar.Draw(g, acd, Fonts.M(15));
            if (ah) using (var pen = new Pen(Color.FromArgb(207, 210, 246), St.P(2.5f))) g.DrawEllipse(pen, acd.X - St.P(2), acd.Y - St.P(2), acd.Width + St.P(4), acd.Height + St.P(4));
        } else {
            St.Fill(g, acd, St.P(10), Color.White);
            St.Stroke(g, acd, St.P(10), ah ? Color.FromArgb(207, 210, 246) : St.Line);
            Ico.Draw(g, "user", new Rectangle(acd.X + St.P(9), acd.Y + St.P(9), St.P(18), St.P(18)), ah ? St.Accent : St.Ink2);
        }
        Add(acc, "account", null, signed ? Account.Name : Tr.T("acc_tip"));
        Add(sr, "search", null, null);
    }

    void PaintBand(Graphics g) {
        var it = bandItem; if (it == null) return;
        var r = new Rectangle(SideW + St.P(18), TopH + St.P(16), Width - SideW - St.P(36), St.P(94));
        var rd = Mir(r);
        St.Shadow(g, rd, St.P(14), Color.FromArgb(59, 43, 214), St.P(14), St.P(16), -St.P(14), 200);
        using (var path = St.Round(rd, St.P(14)))
        using (var br = new LinearGradientBrush(new Rectangle(rd.X - 1, rd.Y - 1, rd.Width + 2, rd.Height + 2), St.Hex("#3b2bd6"), St.Hex("#8b7bff"), 20f)) {
            var cb = new ColorBlend(3); cb.Colors = new[] { St.Hex("#3b2bd6"), St.Hex("#6d5efc"), St.Hex("#8b7bff") }; cb.Positions = new[] { 0f, 0.6f, 1f };
            br.InterpolationColors = cb;
            g.FillPath(br, path);
        }
        int px = r.X + St.P(18), py = r.Y + St.P(16), inner = r.Width - St.P(36);
        // pourcentage geant (ou taille recue quand la taille totale est inconnue : HLS, yt-dlp)
        bool known = (it.Total > 0 && it.Status != "merging") || (it.Status == "merging" && it.MergePct > 0);
        double pct = !known ? 0 : it.Status == "merging" ? it.MergePct : Math.Min(100, it.Received * 100.0 / it.Total);
        string big = known ? ((int)pct).ToString() : it.Status == "merging" ? "…" : Fmt.Size(it.Received);
        var bf = known || it.Status == "merging" ? Fonts.T(40) : Fonts.T(24); var sf = Fonts.T(18);
        int bw = St.W(big, bf), pw = known ? St.W("%", sf) : 0;
        int pctW = Math.Max(St.P(92), bw + pw + St.P(2));
        var pr = new Rectangle(px, py, pctW, St.P(40));
        // « 63% » se lit toujours de gauche a droite ; en hebreu le bloc est colle au bord droit de sa case
        var prd = Mir(pr);
        int sx = Tr.Rtl ? prd.Right - (bw + pw) : prd.X;
        St.TextLtr(g, big, bf, new Rectangle(sx - St.P(4), pr.Y - St.P(3), bw + St.P(4), St.P(46)), Color.White, true);
        if (known) St.TextLtr(g, "%", sf, new Rectangle(sx + bw, pr.Y + St.P(12), pw + 1, St.P(26)), Color.FromArgb(205, Color.White), false);
        // boutons Pause / Annuler
        int ab = St.P(38), agap = St.P(8);
        var cancel = new Rectangle(px + inner - ab, py + St.P(1), ab, ab);
        var pause = new Rectangle(cancel.X - agap - ab, cancel.Y, ab, ab);
        if (it.Status != "merging") BandBtn(g, pause, "band:pause", "pause", Tr.T("pause"), it);
        BandBtn(g, cancel, "band:cancel", "x", Tr.T("cancelDl"), it);
        // titre + ligne meta
        int ix = pr.Right + St.P(16), iw = pause.X - St.P(16) - ix;
        St.Text(g, Title(it), Fonts.M(14), Mir(new Rectangle(ix, py + St.P(1), iw, St.P(20))), Color.White, 0);
        var meta = new List<string>();
        if (it.Status == "merging") meta.Add(Tr.T("c_merging"));
        else {
            if (known) meta.Add(string.Format(Tr.T("of"), Fmt.Size(it.Received), Fmt.Size(it.Total)));
            if (it.Speed > 0) meta.Add(Fmt.Size((long)it.Speed) + Tr.T("perSec"));
            string eta = Ui.Eta(it.Eta); if (eta != null) meta.Add(eta);
            int conns = 0; var segs = it.Segs; if (segs != null) lock (it) conns = segs.Count(s => s.Busy);
            if (conns > 1) meta.Add(string.Format(Tr.T("conns"), conns));
        }
        if (bandOthers > 0) meta.Add(string.Format(Tr.T("others"), bandOthers));
        Segments(g, meta, Fonts.R(12), ix, py + St.P(24), iw, St.P(16), St.Lilac, St.P(14));
        // barre blanche
        var bar = new Rectangle(px, r.Bottom - St.P(16) - St.P(6), inner, St.P(6));
        St.Fill(g, Mir(bar), St.P(3), Color.FromArgb(56, Color.White));
        if (known && pct > 0) {
            int fw = Math.Max(St.P(6), (int)(bar.Width * pct / 100));
            St.Fill(g, Mir(new Rectangle(bar.X, bar.Y, fw, bar.Height)), St.P(3), Color.White);
        }
    }
    void BandBtn(Graphics g, Rectangle r, string key, string icon, string tipText, Item it) {
        var d = Mir(r);
        St.Fill(g, d, St.P(10), Color.FromArgb(Hot(key, it) ? 71 : 41, Color.White));
        int s = St.P(18);
        Ico.Draw(g, icon, new Rectangle(d.X + (d.Width - s) / 2, d.Y + (d.Height - s) / 2, s, s), Color.White);
        Add(r, key, it, tipText);
    }

    // Morceaux de texte poses les uns apres les autres dans le sens de lecture (chacun dans son propre sens).
    void Segments(Graphics g, List<string> parts, Font f, int x, int y, int w, int h, Color c, int gap) {
        int cx = x;
        foreach (var s in parts) {
            int sw = St.W(s, f);
            if (cx + sw > x + w) { sw = x + w - cx; if (sw < St.P(30)) break; }
            St.Text(g, s, f, Mir(new Rectangle(cx, y, sw + 1, h)), c, 0);
            cx += sw + gap;
        }
    }

    // Premier lancement / liste vide (0.7.33) : une consigne, une action. Plus un simple texte gris.
    void PaintEmpty(Graphics g) {
        int ax = SideW, aw = Width - SideW, cy = viewTop + St.P(56);
        int s = St.P(64); var ic = new Rectangle(ax + (aw - s) / 2, cy, s, s);
        St.Fill(g, Mir(ic), St.P(20), St.AccentSoft);
        Ico.Draw(g, "dl", Mir(new Rectangle(ic.X + St.P(18), ic.Y + St.P(18), St.P(28), St.P(28))), St.Accent);
        cy += s + St.P(18);
        St.Text(g, Tr.T("empty_t"), Fonts.M(20), Mir(new Rectangle(ax + St.P(24), cy, aw - St.P(48), St.P(30))), St.Ink, 2); cy += St.P(38);
        St.Text(g, Tr.T("empty_b"), Fonts.R(14), Mir(new Rectangle(ax + St.P(24), cy, aw - St.P(48), St.P(22))), St.Ink3, 2); cy += St.P(24);
        St.Text(g, Tr.T("empty_b2"), Fonts.R(14), Mir(new Rectangle(ax + St.P(24), cy, aw - St.P(48), St.P(22))), St.Ink3, 2); cy += St.P(36);
        int bw = St.W(Tr.T("empty_btn"), Fonts.M(14)) + St.P(44), bh = St.P(42);
        var br = new Rectangle(ax + (aw - bw) / 2, cy, bw, bh);
        St.Fill(g, Mir(br), St.P(10), Hot("paste", null) ? St.AccentDeep : St.Accent);
        St.Text(g, Tr.T("empty_btn"), Fonts.M(14), Mir(br), Color.White, 2);
        Add(br, "paste", null, null);
    }

    void PaintList(Graphics g) {
        var clipR = Mir(new Rectangle(SideW, viewTop, Width - SideW, Math.Max(0, viewBottom - viewTop)));
        var oldClip = g.Clip;
        g.SetClip(clipR);
        int x0 = SideW + St.P(10), w = Width - SideW - St.P(20);
        foreach (var r in rows) {
            int y = viewTop + r.Y - scroll;
            if (y + r.H < viewTop || y > viewBottom) continue;
            if (r.It == null) {
                St.Text(g, Tr.T(r.Group), Fonts.R(12), Mir(new Rectangle(SideW + St.P(22), y + St.P(16), w, St.P(15))), St.Ink3, 0);
                continue;
            }
            PaintRow(g, r.It, new Rectangle(x0, y, w, r.H));
        }
        if (rows.Count == 0) {
            string msg = Items.Count == 0 ? Tr.T("empty") : bandItem != null && Nav == "all" && Search.Text.Trim().Length == 0 ? null : Tr.T("emptyFilter");
            if (msg != null && Items.Count == 0) PaintEmpty(g);
            else if (msg != null) St.Text(g, msg, Fonts.R(14), Mir(new Rectangle(SideW + St.P(24), viewTop + St.P(40), Width - SideW - St.P(48), St.P(24))), St.Ink3, 2);
        }
        g.Clip = oldClip;
        // barre de defilement fine
        int vh = viewBottom - viewTop;
        if (contentH > vh && vh > 0) {
            int th = Math.Max(St.P(30), vh * vh / contentH);
            int ty = viewTop + (int)((vh - th) * (double)scroll / (contentH - vh));
            St.Fill(g, Mir(new Rectangle(Width - St.P(9), ty + St.P(2), St.P(6), th - St.P(4))), St.P(3), Color.FromArgb(dragging || Hot("scroll", null) ? 120 : 70, St.Ink3));
            zones.Add(new Zone { R = Mir(new Rectangle(Width - St.P(14), ty, St.P(14), th)), Key = "scroll" });
        }
    }

    void PaintRow(Graphics g, Item it, Rectangle r) {
        bool rowHot = hover != null && hover.It == it && !hover.Key.StartsWith("band:");
        bool sel = Sel.Contains(it.Id);
        var rd = Mir(r);
        if (sel) St.Fill(g, rd, St.P(12), rowHot ? St.AccentSoft : St.Selected);
        else if (rowHot) St.Fill(g, rd, St.P(12), St.Sunk);
        Add(r, "row", it, (it.Url ?? "") + (string.IsNullOrEmpty(it.Error) ? "" : "\n" + it.Error));
        bool compactCard = it.Status == "done";
        int th = St.P(compactCard ? 46 : 58), tw = St.P(compactCard ? 82 : 104);
        var trc = new Rectangle(r.X + St.P(12), r.Y + (r.Height - th) / 2, tw, th);
        PaintThumb(g, it, trc, rowHot);
        // actions a la fin de la ligne
        var acts = new List<Tuple<string, string, string, bool>>();   // cle, icone, libelle (null = icone seule), principal
        if (it.Status == "done") {
            acts.Add(Tuple.Create("act:play", "play", Tr.T("play"), true));
            acts.Add(Tuple.Create("act:folder", "folder", (string)null, false));
            acts.Add(Tuple.Create("act:trash", "trash", (string)null, false));
        } else if (it.Status == "error") {
            acts.Add(Tuple.Create("act:retry", "retry", Tr.T("retry"), false));
            acts.Add(Tuple.Create("act:trash", "trash", (string)null, false));
        } else if (it.Status == "paused") {
            acts.Add(Tuple.Create("act:resume", "play", Tr.T("resume"), false));
            acts.Add(Tuple.Create("act:cancel", "x", (string)null, false));
        } else if (it.Status == "queued") {
            acts.Add(Tuple.Create("act:now", "play", Tr.T("q_now"), true));
            acts.Add(Tuple.Create("act:next", "clock", Tr.T("q_next"), false));
            acts.Add(Tuple.Create("act:cancel", "x", (string)null, false));
        } else {
            if (it.Status != "merging") acts.Add(Tuple.Create("act:pause", "pause", (string)null, false));
            acts.Add(Tuple.Create("act:cancel", "x", (string)null, false));
        }
        int bh = St.P(34), gap = St.P(4), ax = r.Right - St.P(12);
        var bf = Fonts.R(14); var bfm = Fonts.M(14);
        for (int i = acts.Count - 1; i >= 0; i--) {
            var a = acts[i];
            int bw = a.Item3 == null ? bh : St.P(10 + 18 + 6 + 10) + St.W(a.Item3, a.Item4 ? bfm : bf);
            var b = new Rectangle(ax - bw, r.Y + (r.Height - bh) / 2, bw, bh);
            ax = b.X - gap;
            var bd = Mir(b);
            bool hot = Hot(a.Item1, it);
            Color fg = St.Ink2;
            if (a.Item4) { St.Fill(g, bd, St.P(9), hot ? St.Accent : St.AccentSoft); fg = hot ? Color.White : St.Accent; }
            else if (hot) { St.Fill(g, bd, St.P(9), Color.White); St.Stroke(g, bd, St.P(9), St.Line); fg = a.Item1 == "act:trash" || a.Item1 == "act:cancel" ? St.Bad : St.Ink; }
            int s = St.P(18);
            if (a.Item3 == null) Ico.Draw(g, a.Item2, new Rectangle(bd.X + (bh - s) / 2, bd.Y + (bh - s) / 2, s, s), fg);
            else {
                Ico.Draw(g, a.Item2, Mir(new Rectangle(b.X + St.P(10), b.Y + (bh - s) / 2, s, s)), fg);
                St.Text(g, a.Item3, a.Item4 ? bfm : bf, Mir(new Rectangle(b.X + St.P(34), b.Y, b.Width - St.P(44) + 2, bh)), fg, 0);
            }
            string tipText = a.Item3 != null ? null : a.Item1 == "act:folder" ? Tr.T("folder") : a.Item1 == "act:trash" ? Tr.T("trash") : a.Item1 == "act:pause" ? Tr.T("pause") : Tr.T("cancelDl");
            Add(b, a.Item1, it, tipText);
        }
        // titre + meta
        int ix = trc.Right + St.P(14), iw = ax - St.P(10) - ix;
        bool prog = Ui.InProgress(it) && (Ui.Active(it) || it.Received > 0);
        int ty = r.Y + (r.Height - (prog ? St.P(55) : St.P(43))) / 2;
        St.Text(g, Title(it), Fonts.M(14), Mir(new Rectangle(ix, ty, iw, St.P(20))), St.Ink, 0);
        int my = ty + St.P(24);
        Color cf, cb; string chip = Ui.Chip(it, out cf, out cb);
        var chf = Fonts.R(11); int cw = St.W(chip, chf) + St.P(14);
        var chr = new Rectangle(ix, my, cw, St.P(19));
        var chd = Mir(chr);
        St.Fill(g, chd, St.P(6), cb);
        if (it.Status == "paused") St.Stroke(g, chd, St.P(6), St.Line);
        St.Text(g, chip, chf, chd, cf, 2);
        var meta = new List<string>();
        if (it.Status == "error") meta.Add(Ui.Error(it));
        else {
            meta.Add(Ui.TypeText(it));
            if (Ui.InProgress(it)) {
                if (it.Total > 0 && it.Received > 0) meta.Add(((int)Math.Min(100, it.Received * 100.0 / it.Total)) + "% · " + string.Format(Tr.T("of"), Fmt.Size(it.Received), Fmt.Size(it.Total)));
                else if (it.Received > 0) meta.Add(Fmt.Size(it.Received));
                if (it.Speed > 0) meta.Add(Fmt.Size((long)it.Speed) + Tr.T("perSec"));
            } else {
                string qs = Ui.QualSize(it); if (qs != null) meta.Add(qs);
                meta.Add(Ui.Ago(it.Finished > it.Added ? it.Finished : it.Added));
            }
        }
        Segments(g, meta, Fonts.R(12), chr.Right + St.P(6), my, iw - cw - St.P(6), St.P(19), it.Status == "error" ? St.Ink2 : St.Ink3, St.P(8));
        if (prog) {
            var mb = new Rectangle(ix, my + St.P(19 + 8), iw, St.P(4));
            St.Fill(g, Mir(mb), St.P(2), St.Line);
            if (it.Total > 0 && it.Received > 0) {
                int fw = Math.Max(St.P(4), (int)(mb.Width * Math.Min(1.0, (double)it.Received / it.Total)));
                St.Fill(g, Mir(new Rectangle(mb.X, mb.Y, fw, mb.Height)), St.P(2), it.Status == "paused" ? St.Ink3 : St.Accent);
            }
        }
    }

    void PaintThumb(Graphics g, Item it, Rectangle r, bool rowHot) {
        var d = Mir(r);
        float rad = St.P(9);
        var img = Thumbs.Get(it);
        bool audio = Thumbs.IsAudio(it), video = Thumbs.IsVideo(it);
        using (var path = St.Round(d, rad)) {
            if (img != null) {
                using (var tb = new TextureBrush(img, WrapMode.Clamp)) {
                    tb.ScaleTransform((float)d.Width / img.Width, (float)d.Height / img.Height, MatrixOrder.Append);
                    tb.TranslateTransform(d.X, d.Y, MatrixOrder.Append);
                    g.FillPath(tb, path);
                }
            } else {
                Color a = audio ? St.Hex("#0f766e") : it.Status == "error" ? St.Hex("#7c2d12") : video ? St.Hex("#3b2bd6") : St.Hex("#334155");
                Color b = audio ? St.Hex("#14b8a6") : it.Status == "error" ? St.Hex("#431407") : video ? St.Hex("#1e1b4b") : St.Hex("#0f172a");
                using (var br = new LinearGradientBrush(new Rectangle(d.X - 1, d.Y - 1, d.Width + 2, d.Height + 2), a, b, 45f)) g.FillPath(br, path);
                if (!audio) {
                    // halo doux (comme les radial-gradient de v1)
                    var hr = new RectangleF(d.X + d.Width * 0.35f, d.Y - d.Height * 0.25f, d.Width * 0.8f, d.Height * 0.9f);
                    using (var hp = new GraphicsPath()) {
                        hp.AddEllipse(hr);
                        using (var pg = new PathGradientBrush(hp) { CenterColor = Color.FromArgb(110, it.Status == "error" ? St.Hex("#fbbf24") : St.Hex("#f59e8b")), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } }) {
                            var oc = g.Clip; g.SetClip(path, CombineMode.Intersect); g.FillEllipse(pg, hr); g.Clip = oc;
                        }
                    }
                }
                if (!(rowHot && (it.Status == "done" || it.Status == "error"))) {
                    int s = St.P(26);
                    Ico.Draw(g, audio ? "note" : it.Status == "error" ? "alert" : video ? "film" : "dl", new Rectangle(d.X + (d.Width - s) / 2, d.Y + (d.Height - s) / 2, s, s), Color.FromArgb(235, Color.White));
                }
            }
        }
        string dur = Ui.Dur(it.Duration);
        if (dur != null) {
            var f = Fonts.R(11); int w = St.W(dur, f) + St.P(10), h = St.P(17);
            var dr = Mir(new Rectangle(r.X + St.P(6), r.Bottom - St.P(5) - h, w, h));
            St.Fill(g, dr, St.P(5), Color.FromArgb(178, 10, 12, 24));
            St.TextLtr(g, dur, f, new Rectangle(dr.X + St.P(5), dr.Y, dr.Width, dr.Height), Color.White, false);
        }
        if (rowHot && (it.Status == "done" || it.Status == "error")) {
            int s = St.P(34);
            var c = new Rectangle(d.X + (d.Width - s) / 2, d.Y + (d.Height - s) / 2, s, s);
            using (var b = new SolidBrush(Color.FromArgb(235, Color.White))) g.FillEllipse(b, c);
            int isz = St.P(18);
            Ico.Draw(g, it.Status == "done" ? "play" : "retry", new Rectangle(c.X + (s - isz) / 2 + (it.Status == "done" ? St.P(1) : 0), c.Y + (s - isz) / 2, isz, isz), St.Accent);
        }
        if (it.Status == "done" || it.Status == "error") Add(r, it.Status == "done" ? "act:play" : "act:retry", it, it.Status == "done" ? Tr.T("play") : Tr.T("retry"));
    }

    void PaintToast(Graphics g) {
        if (ToastText == null) return;
        var r = new Rectangle(SideW + St.P(18), Height - StatusH - St.P(18) - St.P(40), Width - SideW - St.P(36), St.P(40));
        var d = Mir(r);
        St.Shadow(g, d, St.P(12), St.Ink, St.P(8), St.P(12), -St.P(8), 90);
        St.Fill(g, d, St.P(12), St.Ink);
        Ico.Draw(g, "trash", Mir(new Rectangle(r.X + St.P(14), r.Y + St.P(11), St.P(18), St.P(18))), Color.White);
        int lw = 0;
        if (ToastUndo) {
            string u = Tr.T("undo"); var lf = Fonts.M(13); lw = St.W(u, lf) + St.P(20);
            var lr = new Rectangle(r.Right - St.P(4) - lw, r.Y + St.P(4), lw, r.Height - St.P(8));
            var ld = Mir(lr);
            if (Hot("toast:undo", null)) St.Fill(g, ld, St.P(8), Color.FromArgb(40, Color.White));
            St.Text(g, u, lf, ld, St.ToastLink, 2);
            Add(lr, "toast:undo", null, null);
        }
        St.Text(g, ToastText, Fonts.R(13), Mir(new Rectangle(r.X + St.P(14 + 18 + 12), r.Y, r.Width - St.P(14 + 18 + 12 + 14) - lw, r.Height)), Color.White, 0);
    }

    void PaintStatus(Graphics g) {
        int y = Height - StatusH;
        using (var p = new Pen(St.Line)) { var l = Mir(new Rectangle(SideW, y, Width - SideW, 1)); g.DrawLine(p, l.X, y, l.Right, y); }
        var f = Fonts.R(12);
        // version installee, en pastille discrete tout au bout de la barre (0.7.42) : « v0.7.42 »
        string ver = "v" + App.Version;
        var vf = Fonts.M(11);
        int vw = St.W(ver, vf) + St.P(18), vh = St.P(20);
        var vr = new Rectangle(Width - St.P(18) - vw, y + (StatusH - vh) / 2, vw, vh);
        St.Fill(g, Mir(vr), vh / 2, St.Line);
        St.TextLtr(g, ver, vf, Mir(new Rectangle(vr.X + St.P(9), vr.Y, vr.Width - St.P(18), vr.Height)), St.Ink3, false);
        // a gauche de la pastille : « Rechercher une mise a jour » (cliquable, 0.7.43) ; un clic sur la pastille fait pareil
        string vl = Tr.T("updCheck");
        int ll = St.W(vl, f) + 2;
        if (ll > 0) {
            var lrr = new Rectangle(vr.X - St.P(10) - ll, y + 1, ll, StatusH - 1);
            bool lh = Hot("st:check", null);
            St.Text(g, vl, lh ? Fonts.M(12) : f, Mir(lrr), lh ? St.Ink : St.Ink3, 1);
            Add(lrr, "st:check", null, null);
        }
        Add(vr, "st:check", null, null);
        int shift = vw + St.P(12) + (ll > 0 ? ll + St.P(14) : 0);
        int rw = string.IsNullOrEmpty(StatusRight) ? 0 : St.W(StatusRight, Fonts.M(12)) + 2;
        var left = new Rectangle(SideW + St.P(18), y + 1, Width - SideW - St.P(54) - rw - shift, StatusH - 1);
        if (!string.IsNullOrEmpty(StatusLeft)) St.Text(g, StatusLeft, f, Mir(left), St.Ink3, 0);
        if (rw > 0) {
            var rr = new Rectangle(Width - St.P(18) - shift - rw, y + 1, rw, StatusH - 1);
            bool hot = StatusRightKey != null && Hot(StatusRightKey, null);
            St.Text(g, StatusRight, hot ? Fonts.M(12) : f, Mir(rr), StatusRightColor, 1);
            if (StatusRightKey != null) Add(rr, StatusRightKey, null, null);
        }
    }

    // ---------- souris / clavier ----------
    Zone ZoneAt(Point p) {
        for (int i = zones.Count - 1; i >= 0; i--) {
            var z = zones[i];
            if (!z.R.Contains(p)) continue;
            if ((z.Key == "row" || (z.Key.StartsWith("act:") && z.It != null)) && (p.Y < viewTop || p.Y > viewBottom)) continue;
            return z;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if (dragging) {
            int vh = viewBottom - viewTop, th = Math.Max(St.P(30), vh * vh / Math.Max(1, contentH));
            scroll = dragScroll0 + (int)((e.Y - dragY0) * (double)(contentH - vh) / Math.Max(1, vh - th));
            ClampScroll(); Invalidate(); return;
        }
        var z = ZoneAt(e.Location);
        bool same = (z == null && hover == null) || (z != null && hover != null && z.Key == hover.Key && z.It == hover.It);
        hover = z;
        if (!same) Invalidate();
        Cursor = z != null && z.Key != "row" && z.Key != "search" ? Cursors.Hand : Cursors.Default;
        string t = z != null ? z.Tip : null;
        if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t ?? ""); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != null) { hover = null; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        var z = ZoneAt(e.Location);
        if (z == null || z.Key != "search") Focus(); else { Search.Focus(); return; }
        if (z != null && z.Key == "scroll" && e.Button == MouseButtons.Left) { dragging = true; dragY0 = e.Y; dragScroll0 = scroll; Capture = true; return; }
        bool onItem = z != null && z.It != null && (z.Key == "row" || e.Button == MouseButtons.Right);
        if (onItem) {
            string id = z.It.Id;
            if (e.Button == MouseButtons.Right) { if (!Sel.Contains(id)) { Sel.Clear(); Sel.Add(id); anchor = id; } }
            else if ((ModifierKeys & Keys.Control) != 0) { if (!Sel.Remove(id)) Sel.Add(id); anchor = id; }
            else if ((ModifierKeys & Keys.Shift) != 0 && anchor != null) {
                var vis = VisibleItems(); int a = vis.FindIndex(i => i.Id == anchor), b = vis.FindIndex(i => i.Id == id);
                if (a >= 0 && b >= 0) { Sel.Clear(); for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Sel.Add(vis[i].Id); }
            } else { Sel.Clear(); Sel.Add(id); anchor = id; }
            Invalidate();
        } else if (e.Button == MouseButtons.Left && z == null && e.Y > viewTop && e.Y < viewBottom) { Sel.Clear(); Invalidate(); }
        if (e.Button == MouseButtons.Right && OnContext != null && (z == null || z.It != null) && e.Y > viewTop && e.Y < viewBottom) OnContext(e.Location);
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (dragging) { dragging = false; Capture = false; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var z = ZoneAt(e.Location);
        if (z == null || z.Key == "row" || z.Key == "scroll" || z.Key == "search") return;
        if (z.Key.StartsWith("nav:")) { Nav = z.Key.Substring(4); scroll = 0; Invalidate(); return; }
        if (OnAction != null) OnAction(z.Key, z.It);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) {
        base.OnMouseDoubleClick(e);
        var z = ZoneAt(e.Location);
        if (e.Button == MouseButtons.Left && z != null && z.Key == "row" && z.It != null && OnAction != null) OnAction("open", z.It);
    }
    protected override void OnMouseWheel(MouseEventArgs e) {
        base.OnMouseWheel(e);
        scroll -= e.Delta * St.P(RowH) / 120; ClampScroll(); Invalidate();
    }
    protected override bool IsInputKey(Keys k) { var b = k & Keys.KeyCode; return b == Keys.Up || b == Keys.Down || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        var vis = VisibleItems();
        if (e.KeyCode == Keys.Delete && OnAction != null) OnAction("delete", null);
        else if (e.KeyCode == Keys.Enter && OnAction != null) OnAction("open", null);
        else if (e.Control && e.KeyCode == Keys.A) { foreach (var i in vis) Sel.Add(i.Id); Invalidate(); }
        else if (e.Control && e.KeyCode == Keys.F) Search.Focus();
        else if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && vis.Count > 0) {
            int cur = vis.FindIndex(i => i.Id == anchor);
            int n = cur < 0 ? 0 : Math.Max(0, Math.Min(vis.Count - 1, cur + (e.KeyCode == Keys.Down ? 1 : -1)));
            SelectOnly(vis[n].Id);
        }
    }
}
