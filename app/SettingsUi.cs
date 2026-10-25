using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// Reprend le rail a languette et les tuiles activables de Vmail Plus, dans les couleurs de ShowLef.
// 0.7.48 (skill /design, « regarde vue par vue, ameliore a fond ») : meme disposition, controles Windows gris remplaces
// par des controles dessines (choix en pastilles, compteur −/+, interrupteur sur les tuiles), textes poses avec le bon
// sens d'ecriture (le point de fin de phrase partait a gauche en hebreu).
static class PrefStyle {
    public static readonly Color Rail = St.Hex("#31246b"), RailText = St.Hex("#dcd7f6");
    public static string L(string he, string fr, string en) { return new[] { he, fr, en }[Math.Max(0, Math.Min(2, Tr.Lang))]; }
    public static void Lines(Graphics g, string text, Font font, Rectangle r, Color color) {
        var f = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (Tr.Rtl) f |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, text, font, r, color, f);
    }
    // interrupteur (tuiles) : piste arrondie, pastille blanche qui glisse vers la fin quand c'est actif
    public static void Switch(Graphics g, Rectangle r, bool on, bool enabled) {
        St.Fill(g, r, r.Height / 2f, !enabled ? St.Line : on ? St.Accent : St.Hex("#d3d6e4"));
        int k = r.Height - St.P(4);
        int x = (on != Tr.Rtl) ? r.Right - k - St.P(2) : r.X + St.P(2);
        using (var sh = new SolidBrush(Color.FromArgb(40, 21, 26, 45))) g.FillEllipse(sh, x, r.Y + St.P(3), k, k);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, r.Y + St.P(2), k, k);
    }
}

// Bouton arrondi dessine (pied de page, « choisir un dossier ») : primaire, discret ou doux ; grise quand inactif.
class PrefButton : Button {
    public int Kind;   // 0 primaire, 1 contour, 2 doux (accent pale)
    bool hover, down;
    public PrefButton(string text, int kind) {
        Text = text; Kind = kind; Font = Fonts.M(13.5f); Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Height = St.P(42); Width = Math.Max(St.P(112), St.W(text, Font) + St.P(40)); Margin = new Padding(St.P(10), 0, 0, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent != null ? Parent.BackColor : Color.White);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Color fill, ink;
        if (!Enabled) { fill = Kind == 0 ? St.Hex("#d6d4f3") : St.Sunk; ink = Kind == 0 ? Color.White : St.Ink3; }
        else if (Kind == 0) { fill = down ? St.AccentDeep : hover ? St.Hex("#3d32c4") : St.Accent; ink = Color.White; }
        else if (Kind == 2) { fill = hover ? St.Lilac : St.AccentSoft; ink = St.Accent; }
        else { fill = hover ? St.Sunk : Color.White; ink = St.Ink; }
        if (Kind == 0 && Enabled) St.Shadow(g, new RectangleF(2, 2, Width - 5, Height - 6), St.P(10), St.Accent, St.P(2), St.P(6), 0, 40);
        St.Fill(g, r, St.P(10), fill);
        if (Kind == 1) St.Stroke(g, new RectangleF(0, 0, Width, Height), St.P(10), hover ? St.Hex("#cfd3e3") : St.Line);
        St.Text(g, Text, Font, new Rectangle(0, 0, Width, Height), ink, 2);
        if (Focused && ShowFocusCues) St.Stroke(g, new RectangleF(2, 2, Width - 4, Height - 4), St.P(8), Kind == 0 ? Color.White : St.Accent);
    }
}

// En-tete de page : pastille avec l'icone de la page, titre, phrase. Donne un repere a chaque vue.
class PrefHeader : Control {
    public string Glyph, Title, Sub;
    public PrefHeader() {
        Dock = DockStyle.Top; Height = St.P(92); BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    public void Set(string glyph, string title, string sub) { Glyph = glyph; Title = title; Sub = sub; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int t = St.P(52), x = Tr.Rtl ? Width - t : 0;
        St.Fill(g, new RectangleF(x, St.P(4), t, t), St.P(16), St.AccentSoft);
        if (Glyph != null) Ico.Draw(g, Glyph, new RectangleF(x + St.P(13), St.P(17), St.P(26), St.P(26)), St.Accent, 2f);
        int tx = Tr.Rtl ? 0 : t + St.P(16), tw = Width - t - St.P(16);
        PrefStyle.Lines(g, Title, Fonts.T(25), new Rectangle(tx, 0, tw, St.P(38)), St.Ink);
        PrefStyle.Lines(g, Sub, Fonts.R(13), new Rectangle(tx, St.P(38), tw, St.P(24)), St.Ink2);
    }
}

// Titre de groupe dans une page (« הרגלים », « קצב ») : petit texte et un filet qui file jusqu'au bout.
class PrefSection : Control {
    public PrefSection(string text) {
        Text = text; Dock = DockStyle.Fill; Height = St.P(28); Margin = new Padding(0, St.P(2), St.P(10), St.P(6)); BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        int w = St.W(Text, Fonts.M(12.5f)) + St.P(4);
        var r = new Rectangle(Tr.Rtl ? Width - w : 0, 0, w, Height);
        St.Text(g, Text, Fonts.M(12.5f), r, St.Accent, 0);
        using (var pen = new Pen(St.Line)) g.DrawLine(pen, Tr.Rtl ? 0 : w + St.P(10), Height / 2, Tr.Rtl ? Width - w - St.P(10) : Width, Height / 2);
    }
}

// Texte peint (titre, sous-titre, libelles) : TextRenderer avec le sens hebreu, sans les defauts du Label
class PrefText : Control {
    readonly Func<Font> font; readonly Color color;
    public PrefText(string text, Func<Font> font, Color color, int height) {
        Text = text; this.font = font; this.color = color; Height = height; Dock = DockStyle.Top;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.White;
    }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaint(PaintEventArgs e) { PrefStyle.Lines(e.Graphics, Text, font(), new Rectangle(0, 0, Width, Height), color); }
}

class PrefTab : Button {
    public bool Selected;
    public string Glyph;
    bool hover;
    public PrefTab(string text, string glyph) {
        Text = text; Glyph = glyph; Height = St.P(82); Dock = DockStyle.Top;
        Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        AccessibleRole = AccessibleRole.PageTab; AccessibleName = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(PrefStyle.Rail);
        var r = new RectangleF(Tr.Rtl ? -St.P(18) : St.P(10), St.P(3), Width + (Tr.Rtl ? St.P(8) : 0), Height - St.P(6));
        if (Selected) St.Fill(g, r, St.P(18), Color.White);
        else if (hover) St.Fill(g, new RectangleF(St.P(10), St.P(3), Width - St.P(18), Height - St.P(6)), St.P(18), St.Hex("#443580"));
        int cx = Width / 2 + (Tr.Rtl ? -St.P(4) : St.P(4));
        St.Fill(g, new RectangleF(cx - St.P(19), St.P(10), St.P(38), St.P(38)), St.P(19), Selected ? St.Accent : St.Hex("#4a3c80"));
        Ico.Draw(g, Glyph, new RectangleF(cx - St.P(10), St.P(19), St.P(20), St.P(20)), Color.White);
        St.Text(g, Text, Fonts.M(12), new Rectangle(St.P(12), St.P(51), Width - St.P(18), St.P(23)), Selected ? St.Accent : PrefStyle.RailText, 2);
        // anneau de focus : clavier seulement, et jamais sur la languette deja choisie (elle se voit assez)
        if (Focused && ShowFocusCues && !Selected) St.Stroke(g, new RectangleF(St.P(13), St.P(5), Width - St.P(18), Height - St.P(10)), St.P(15), St.Hex("#8f84d8"));
    }
}

class PrefTile : CheckBox {
    public string Detail, Glyph;
    bool hover;
    public PrefTile(string text, string detail, string glyph, bool value) {
        Text = text; Detail = detail; Glyph = glyph; Checked = value;
        AutoSize = false; Height = St.P(78); Dock = DockStyle.Fill; Cursor = Cursors.Hand;
        Font = Fonts.M(14); AccessibleName = text; AccessibleDescription = detail;
        Margin = new Padding(0, 0, St.P(10), St.P(10));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.White);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        St.Fill(g, r, St.P(14), Checked ? St.Selected : hover ? St.Sunk : Color.White);
        St.Stroke(g, r, St.P(14), Checked ? St.Lilac : hover ? St.Hex("#d6d9e8") : St.Line);
        // icone a son debut de ligne, interrupteur a la fin : une tuile = un reglage marche/arret (plus de rond « radio »)
        int ic = St.P(34), x = Tr.Rtl ? Width - St.P(16) - ic : St.P(16);
        int top = (Height - ic) / 2;
        St.Fill(g, new RectangleF(x, top, ic, ic), St.P(10), Checked ? St.Accent : St.Sunk);
        Ico.Draw(g, Glyph, new RectangleF(x + St.P(8), top + St.P(8), St.P(18), St.P(18)), Checked ? Color.White : St.Ink2);
        var sw = new Rectangle(Tr.Rtl ? St.P(16) : Width - St.P(16) - St.P(38), (Height - St.P(22)) / 2, St.P(38), St.P(22));
        PrefStyle.Switch(g, sw, Checked, Enabled);
        int tx = St.P(16) + (Tr.Rtl ? St.P(48) : ic + St.P(12)), tw = Width - 2 * St.P(16) - ic - St.P(12) - St.P(48);
        PrefStyle.Lines(g, Text, Fonts.M(13.5f), new Rectangle(tx, St.P(16), tw, St.P(20)), Enabled ? St.Ink : St.Ink3);
        PrefStyle.Lines(g, Detail, Fonts.R(11.5f), new Rectangle(tx, St.P(38), tw, Height - St.P(40)), St.Ink2);
        if (Focused && ShowFocusCues) St.Stroke(g, new RectangleF(4, 4, Width - 9, Height - 9), St.P(11), St.Accent);
    }
}

// Choix parmi quelques valeurs : pastilles cliquables (passent a la ligne si besoin), fleches du clavier.
class PrefChoice : Control {
    readonly string[] keys, labels;
    int sel, hover = -1;
    readonly List<Rectangle> boxes = new List<Rectangle>();
    public bool LtrLabels;   // valeurs qui se lisent de gauche a droite (x1.5, MP3)
    public event EventHandler Changed;
    public PrefChoice(string[] keys, string[] labels, string value) {
        this.keys = keys; this.labels = labels; sel = Math.Max(0, Array.IndexOf(keys, value));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Cursor = Cursors.Hand; BackColor = Color.White; Height = St.P(40);
    }
    public string Value { get { return keys[sel]; } }
    void Layout2() {
        boxes.Clear();
        if (Width < St.P(200)) return;   // largeur pas encore connue : sinon une pastille par ligne et une page trop haute
        int x = 0, y = 0, h = St.P(36), gap = St.P(8);
        foreach (var l in labels) {
            int w = St.W(l, Fonts.M(13)) + St.P(30);
            if (x > 0 && x + w > Width) { x = 0; y += h + gap; }
            boxes.Add(new Rectangle(Tr.Rtl ? Width - x - w : x, y, w, h));
            x += w + gap;
        }
        int need = y + h + St.P(2);
        if (Height != need) Height = need;
    }
    protected override void OnResize(EventArgs e) { Layout2(); base.OnResize(e); }
    protected override void OnPaint(PaintEventArgs e) {
        if (boxes.Count != labels.Length) Layout2();
        if (boxes.Count != labels.Length) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < labels.Length; i++) {
            var r = boxes[i]; bool on = i == sel;
            St.Fill(g, r, r.Height / 2f, on ? St.Accent : i == hover ? St.NavHover : St.Sunk);
            if (LtrLabels) TextRenderer.DrawText(g, labels[i], Fonts.M(13), r, on ? Color.White : St.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            else St.Text(g, labels[i], Fonts.M(13), r, on ? Color.White : St.Ink, 2);
            if (on && Focused && ShowFocusCues) St.Stroke(g, new RectangleF(r.X - St.P(3), r.Y - St.P(3), r.Width + St.P(6), r.Height + St.P(6)), r.Height / 2f + St.P(3), St.Accent);
        }
    }
    void Set(int i) { if (i < 0 || i >= keys.Length || i == sel) return; sel = i; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    protected override void OnMouseMove(MouseEventArgs e) { int h = boxes.FindIndex(b => b.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Set(boxes.FindIndex(b => b.Contains(e.Location))); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        int d = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 : 0;
        if (Tr.Rtl) d = -d;
        if (d != 0) Set(sel + d);
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

// Nombre (ou heure) : − valeur +, molette et fleches du clavier. Remplace les NumericUpDown gris.
class PrefStepper : Control {
    public int Min, Max, Step; int val; bool wrap;
    readonly Func<int, string> fmt;
    int hover;   // 0 rien, -1 moins, 1 plus
    public PrefStepper(int min, int max, int step, int value, Func<int, string> fmt, bool wrap) {
        Min = min; Max = max; Step = step; this.fmt = fmt; this.wrap = wrap; val = Math.Max(min, Math.Min(max, value));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; BackColor = Color.White; Height = St.P(42);
    }
    public event EventHandler Changed;
    public int Value { get { return val; } set { int v = Math.Max(Min, Math.Min(Max, value)); if (v == val) return; val = v; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); } }
    Rectangle Minus { get { return new Rectangle(0, 0, St.P(42), Height); } }
    Rectangle Plus { get { return new Rectangle(Width - St.P(42), 0, St.P(42), Height); } }
    void Bump(int d) {
        int v = val + d * Step;
        if (wrap) { int span = Max - Min + Step; v = ((v - Min) % span + span) % span + Min; }
        Value = v;
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        St.Fill(g, r, St.P(10), Enabled ? Color.White : St.Sunk);
        St.Stroke(g, new RectangleF(0, 0, Width, Height), St.P(10), Focused ? St.Accent : St.Line);
        foreach (var side in new[] { -1, 1 }) {
            var b = side < 0 ? Minus : Plus;
            bool can = Enabled && (wrap || (side < 0 ? val > Min : val < Max));
            if (can && hover == side) St.Fill(g, new RectangleF(b.X + St.P(4), b.Y + St.P(4), b.Width - St.P(8), b.Height - St.P(8)), St.P(8), St.AccentSoft);
            Color c = can ? St.Accent : St.Ink3;
            using (var pen = new Pen(c, St.P(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                int cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, k = St.P(6);
                g.DrawLine(pen, cx - k, cy, cx + k, cy);
                if (side > 0) g.DrawLine(pen, cx, cy - k, cx, cy + k);
            }
        }
        TextRenderer.DrawText(g, fmt(val), Fonts.M(15), new Rectangle(St.P(42), 0, Width - St.P(84), Height), Enabled ? St.Ink : St.Ink3,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | (Tr.Rtl ? TextFormatFlags.RightToLeft : 0));
    }
    protected override void OnMouseMove(MouseEventArgs e) { int h = Minus.Contains(e.Location) ? -1 : Plus.Contains(e.Location) ? 1 : 0; Cursor = h != 0 ? Cursors.Hand : Cursors.Default; if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (Minus.Contains(e.Location)) Bump(-1); else if (Plus.Contains(e.Location)) Bump(1); base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (Focused) Bump(e.Delta > 0 ? 1 : -1); base.OnMouseWheel(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) Bump(1);
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) Bump(-1);
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

class PrefField : Panel {
    public PrefField() {
        BackColor = Color.White; Padding = new Padding(St.P(12), St.P(10), St.P(12), St.P(7));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        St.Stroke(e.Graphics, new RectangleF(0, 0, Width, Height), St.P(10), ContainsFocus ? St.Accent : St.Line);
    }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }
}

class SettingsForm : FormBase {
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    readonly List<PrefTab> tabs = new List<PrefTab>();
    readonly List<TableLayoutPanel> pages = new List<TableLayoutPanel>();
    readonly List<string> descriptions = new List<string>();
    readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    readonly PrefHeader header = new PrefHeader();
    readonly List<string> glyphs = new List<string>();
    static string L(string he, string fr, string en) { return PrefStyle.L(he, fr, en); }
    TableLayoutPanel Page(string name, string detail, string icon) {
        var p = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Visible = false, Tag = name, BackColor = Color.White, Margin = new Padding(0) };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        p.RightToLeft = Tr.Rtl ? RightToLeft.Yes : RightToLeft.No;
        var tab = new PrefTab(name, icon); int index = tabs.Count;
        tab.Click += (o, e) => SelectPage(index);
        tabs.Add(tab); pages.Add(p); descriptions.Add(detail); glyphs.Add(icon); body.Controls.Add(p); return p;
    }
    public void SelectPage(int index) {
        index = Math.Max(0, Math.Min(pages.Count - 1, index));
        for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; tabs[i].Selected = i == index; tabs[i].Invalidate(); }
        header.Set(glyphs[index], (string)pages[index].Tag, descriptions[index]); body.AutoScrollPosition = Point.Empty;
        pages[index].PerformLayout(); body.PerformLayout();
    }
    // Une ligne de reglage : libelle (+ une phrase d'aide facultative) puis le controle. Les controles dessines portent
    // leur propre cadre ; un champ de saisie est pose dans un cadre arrondi.
    T Field<T>(TableLayoutPanel page, string name, string help, T control, bool path, bool half = false) where T : Control {
        var row = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, St.P(10), St.P(12)) };
        var label = new PrefText(name, () => Fonts.M(13.5f), St.Ink, St.P(24));
        var helpT = help != null ? new PrefText(help, () => Fonts.R(11.5f), St.Ink3, St.P(22)) : null;
        control.AccessibleName = name;
        Control box = control;
        if (control is TextBox || control is Panel) {
            var field = new PrefField { Dock = DockStyle.Top, Height = St.P(44) };
            control.Font = Fonts.R(14); control.Dock = DockStyle.Fill; control.Margin = new Padding(0);
            var tb = control as TextBox; if (tb != null) tb.BorderStyle = BorderStyle.None;
            if (path) control.RightToLeft = RightToLeft.No;
            field.Controls.Add(control); box = field;
        } else control.Dock = DockStyle.Top;
        var gap = new Panel { Dock = DockStyle.Top, Height = St.P(8) };
        row.Controls.Add(box); row.Controls.Add(gap); if (helpT != null) row.Controls.Add(helpT); row.Controls.Add(label);
        Action fit = () => row.Height = label.Height + (helpT != null ? helpT.Height : 0) + gap.Height + box.Height + St.P(2);
        box.SizeChanged += (o, e) => fit(); fit();
        page.Controls.Add(row); page.SetColumnSpan(row, half ? 1 : 2); return control;   // half : deux compteurs par ligne
    }
    void Section(TableLayoutPanel page, string text) { var c = new PrefSection(text); page.Controls.Add(c); page.SetColumnSpan(c, 2); }
    PrefTile Tile(TableLayoutPanel page, string text, string detail, string icon, bool value) {
        var tile = new PrefTile(text, detail, icon, value); page.Controls.Add(tile); return tile;
    }
    // chemin d'un outil : vide = automatique ; on montre ce qui est trouve (ou que ce sera installe)
    TextBox ToolPath(TableLayoutPanel page, string tool, string value, Func<string> found) {
        string f = null; try { f = found(); } catch { }
        string help = f != null ? L("נמצא אוטומטית: ", "Trouvé automatiquement : ", "Found automatically: ") + f
                                : L("לא נמצא: יותקן אוטומטית בהורדה הראשונה", "Introuvable : installé automatiquement au premier téléchargement", "Not found: installed automatically on the first download");
        var tb = Field(page, L("מיקום " + tool, "Emplacement de " + tool, tool + " location"), help, new TextBox { Text = value }, true);
        tb.HandleCreated += (o, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, L("אוטומטי (מומלץ)", "Automatique (recommandé)", "Automatic (recommended)"));
        return tb;
    }
    public SettingsForm() : base(App.Name + " - " + Tr.T("settings"), 380) {
        CustomLayout = true; Controls.Clear(); RightToLeft = RightToLeft.No; RightToLeftLayout = false;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(St.P(860), wa.Width - St.P(32)), Math.Min(St.P(690), wa.Height - St.P(70)));
        var s = Settings.Current;
        var general = Page(L("כללי", "Général", "General"), L("המקום שלכם, ההרגלים שלכם.", "Vos fichiers, vos habitudes.", "Your files. Your preferences."), "gear");
        var downloads = Page(L("הורדות", "Téléchargements", "Downloads"), L("האיכות והקצב שמתאימים לכם.", "La qualité et le rythme qui vous conviennent.", "Quality and speed that suit you."), "dl");
        var audio = Page(L("שמע", "Audio", "Audio"), L("קובצי שמע מוכנים להאזנה.", "Des fichiers prêts à écouter, comme vous aimez.", "Audio ready to listen to, just the way you like it."), "note");
        var schedule = Page(L("תזמון", "Programmation", "Schedule"), L("בחרו מתי ההורדות מתחילות.", "Choisissez le bon moment pour télécharger.", "Choose when your downloads start."), "clock");
        var advanced = Page(L("מתקדם", "Avancé", "Advanced"), L("הכלים מוכנים אוטומטית. אפשר לבחור מיקום אחר.", "Les outils sont préparés automatiquement. Vous pouvez choisir un autre emplacement.", "Tools are set up automatically. You can choose another location."), "list");

        // dossier : chemin + bouton « choisir » dans le meme cadre
        var folder = new TextBox { Text = s.Folder, BorderStyle = BorderStyle.None, Font = Fonts.R(14), RightToLeft = RightToLeft.No };
        var pick = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var choose = new PrefButton(L("בחירת תיקייה", "Choisir", "Choose"), 2) { Dock = Tr.Rtl ? DockStyle.Left : DockStyle.Right };
        choose.Font = Fonts.M(12.5f); choose.Width = St.W(choose.Text, choose.Font) + St.P(28);
        choose.Click += (o, e) => { using (var d = new FolderBrowserDialog { SelectedPath = folder.Text }) if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath; };
        folder.Dock = DockStyle.Fill; pick.Controls.Add(folder); pick.Controls.Add(new Panel { Dock = choose.Dock, Width = St.P(10) }); pick.Controls.Add(choose);
        Field(general, Tr.T("saveTo"), null, pick, true);
        var lang = Field(general, Tr.T("s_lang"), null, new PrefChoice(new[] { "", "he", "fr", "en" }, new[] { L("שפת המחשב", "Langue de Windows", "Windows language"), "עברית", "Français", "English" }, s.Lang ?? ""), false);
        Section(general, L("הרגלים", "Habitudes", "Habits"));
        var sort = Tile(general, L("סידור לפי סוג", "Classer par type", "Organize by type"), L("שמע וסרטונים בתיקיות נפרדות", "Audio et vidéos dans leurs dossiers", "Separate folders for audio and video"), "folder", s.SortByType);
        var start = Tile(general, L("הפעלה עם המחשב", "Démarrer avec Windows", "Start with Windows"), L("מוכנה מיד כשהמחשב נדלק", "ShowLef prête dès le démarrage", "Ready when your computer starts"), "play", s.StartWithWindows);
        var tray = Tile(general, L("להישאר ברקע", "Rester en arrière-plan", "Keep running in background"), L("סגירת החלון לא מפסיקה הורדה", "Fermer la fenêtre garde ShowLef active", "Closing the window keeps ShowLef running"), "list", s.CloseToTray);
        var notify = Tile(general, L("הודעה בסיום", "M’avertir à la fin", "Notify me when done"), L("הודעה כשהקובץ מוכן", "Une notification quand le fichier est prêt", "A notification when your file is ready"), "check", s.Notify);
        var clip = Tile(general, L("זיהוי קישור שהועתק", "Repérer les liens copiés", "Detect copied links"), L("להציע הורדה מקישור שהעתקתם", "Proposer de télécharger le lien copié", "Offer to download a copied link"), "link", s.Clipboard);
        general.SetColumnSpan(clip, 2);   // 5 tuiles : la derniere prend toute la ligne au lieu de laisser un trou

        var q = Field(downloads, Tr.T("quality"), L("מה שיורד כשלוחצים על ״הורדה״.", "Ce qui est pris quand vous cliquez « Télécharger ».", "What you get when you click Download."), new PrefChoice(QKeys, QLabels(), s.Quality), false);
        Section(downloads, L("קצב", "Rythme", "Pace"));
        var conn = Field(downloads, Tr.T("s_conn"), L("יותר חיבורים = הורדה מהירה יותר של קובץ גדול.", "Plus de connexions = un gros fichier arrive plus vite.", "More connections = a big file arrives faster."), new PrefStepper(1, 32, 1, s.Connections, v => v.ToString(), false), false, true);
        var par = Field(downloads, Tr.T("s_parallel"), L("כמה קבצים יורדים בבת אחת.", "Combien de fichiers en même temps.", "How many files at the same time."), new PrefStepper(1, 10, 1, s.MaxParallel, v => v.ToString(), false), false, true);
        var limit = Field(downloads, L("הגבלת מהירות", "Vitesse maximale", "Speed limit"), L("כדי להשאיר אינטרנט לשאר הבית.", "Pour laisser de l'internet au reste de la maison.", "To leave internet for everyone else."),
            new PrefStepper(0, 100000, 256, s.SpeedLimitKB, v => v == 0 ? L("ללא הגבלה", "Sans limite", "No limit") : v >= 1024 ? (v / 1024.0).ToString("0.#") + " " + L("מ״ב/ש", "Mo/s", "MB/s") : v + " " + L("ק״ב/ש", "Ko/s", "KB/s"), false), false, true);
        var retries = Field(downloads, Tr.T("s_retries"), L("אם החיבור נקטע, כמה פעמים לנסות שוב.", "Si la connexion coupe, combien de nouveaux essais.", "If the connection drops, how many retries."), new PrefStepper(1, 100, 1, s.Retries, v => v.ToString(), false), false, true);
        var af = Field(audio, Tr.T("audioFmt"), null, new PrefChoice(new[] { "mp3", "m4a", "original" }, new[] { "MP3", "M4A", Tr.T("audioOrig") }, s.AudioFormat), false);
        var speed = Field(audio, L("מהירות השמע בקובץ", "Vitesse enregistrée dans le fichier", "Speed saved in the file"), L("לשמוע שיעור מהר יותר, בכל נגן.", "Écouter un cours plus vite, dans n'importe quel lecteur.", "Listen faster, in any player."),
            new PrefChoice(new[] { "1", "1.25", "1.5", "1.75", "2" }, new[] { "x1", "x1.25", "x1.5", "x1.75", "x2" }, s.AudioSpeed ?? "1") { LtrLabels = true }, false);
        Section(audio, L("צליל", "Son", "Sound"));
        var loud = Tile(audio, L("עוצמה אחידה", "Volume régulier", "Even volume"), L("לצמצם הבדלי עוצמה בשמע", "Réduire les écarts de volume", "Reduce differences in volume"), "note", s.Loudnorm); audio.SetColumnSpan(loud, 2);
        DateTime t0; int mins = DateTime.TryParse(s.ScheduleTime, out t0) ? (int)t0.TimeOfDay.TotalMinutes / 15 * 15 : 120;
        var time = Field(schedule, L("שעת התחלה", "Heure de démarrage", "Start time"), L("ההורדות מתחילות לבד בשעה הזאת.", "Les téléchargements partent seuls à cette heure.", "Downloads start by themselves at this time."),
            new PrefStepper(0, 1425, 15, mins, v => (v / 60).ToString("00") + ":" + (v % 60).ToString("00"), true), false);
        Section(schedule, L("מה קורה", "Ce qui se passe", "What happens"));
        var sched = Tile(schedule, L("התחלה מתוזמנת", "Programmer le départ", "Schedule downloads"), L("ההורדות יחכו לשעה שבחרתם", "Attendre l’heure choisie pour démarrer", "Wait until the time you choose"), "clock", s.ScheduleOn);
        var shut = Tile(schedule, L("כיבוי בסיום", "Éteindre à la fin", "Shut down when done"), L("לכבות את המחשב בסיום כל ההורדות", "Éteindre le PC après toutes les tâches", "Shut down the PC after all tasks"), "pause", s.ShutdownWhenDone);
        time.Enabled = sched.Checked; sched.CheckedChanged += (o, e) => time.Enabled = sched.Checked;
        var ff = ToolPath(advanced, "ffmpeg", s.FfmpegPath, () => Tools.Ffmpeg);
        var yt = ToolPath(advanced, "yt-dlp", s.YtdlpPath, () => Tools.Ytdlp);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(St.P(30), St.P(26), St.P(20), St.P(12)), BackColor = Color.White };
        var head = new Panel { Dock = DockStyle.Top, Height = St.P(92) };
        head.Controls.Add(header); content.Controls.Add(body); content.Controls.Add(head);
        var foot = new Panel { Dock = DockStyle.Bottom, Height = St.P(70), Padding = new Padding(St.P(24), St.P(15), St.P(24), 0), BackColor = Color.White };
        foot.Paint += (o, e) => { using (var pen = new Pen(St.Line)) e.Graphics.DrawLine(pen, St.P(24), 0, foot.Width - St.P(24), 0); };
        buttons.Dock = Tr.Rtl ? DockStyle.Left : DockStyle.Right; buttons.FlowDirection = Tr.Rtl ? FlowDirection.LeftToRight : FlowDirection.RightToLeft; foot.Controls.Add(buttons);
        var ok = new PrefButton(L("שמירת השינויים", "Enregistrer", "Save changes"), 0) { DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new PrefButton(Tr.T("cancel"), 1) { DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
        // rien n'a change : « enregistrer » reste grise (on voit d'un coup d'oeil s'il y a quelque chose a garder)
        EventHandler dirty = (o, e) => ok.Enabled = true;
        foreach (var tl in new[] { sort, start, tray, notify, clip, loud, sched, shut }) tl.CheckedChanged += dirty;
        foreach (var ch in new[] { lang, q, af, speed }) ch.Changed += dirty;
        foreach (var st in new[] { conn, par, limit, retries, time }) st.Changed += dirty;
        foreach (var tb in new[] { folder, ff, yt }) tb.TextChanged += dirty;
        var main = new Panel { Dock = DockStyle.Fill }; main.Controls.Add(content); main.Controls.Add(foot);
        var rail = new Panel { Dock = Tr.Rtl ? DockStyle.Right : DockStyle.Left, Width = St.P(154), BackColor = PrefStyle.Rail, AutoScroll = true };
        var nav = new Panel { Dock = DockStyle.Top, Height = tabs.Count * St.P(82) };
        for (int i = tabs.Count - 1; i >= 0; i--) nav.Controls.Add(tabs[i]);
        var brand = new Panel { Dock = DockStyle.Top, Height = St.P(110) };
        brand.Paint += (o, e) => {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var icon = Theme.AppIcon(48)) g.DrawIcon(icon, new Rectangle((brand.Width - St.P(42)) / 2, St.P(17), St.P(42), St.P(42)));
            St.Text(g, Tr.T("appName"), Fonts.T(21), new Rectangle(0, St.P(65), brand.Width, St.P(32)), Color.White, 2);
        };
        var version = new Label { Dock = DockStyle.Bottom, Height = St.P(36), Text = "v" + App.Version, Font = Fonts.R(11), ForeColor = PrefStyle.RailText, TextAlign = ContentAlignment.MiddleCenter };
        rail.Controls.Add(nav); rail.Controls.Add(brand); rail.Controls.Add(version);
        Controls.Add(main); Controls.Add(rail); SelectPage(0);
        // focus sur la page, pas sur une languette : plus de cadre de focus parasite autour de « כללי » a l'ouverture
        Shown += (o, e) => { folder.SelectionLength = 0; ActiveControl = body; };
        ok.Click += (o, e) => {
            s.Folder = folder.Text.Trim(); s.SortByType = sort.Checked; s.Lang = lang.Value;
            s.Connections = conn.Value; s.MaxParallel = par.Value; s.SpeedLimitKB = limit.Value; s.Retries = retries.Value;
            s.Quality = q.Value; s.AudioFormat = af.Value; s.Loudnorm = loud.Checked; s.AudioSpeed = speed.Value;
            s.ScheduleOn = sched.Checked; s.ScheduleTime = (time.Value / 60).ToString("00") + ":" + (time.Value % 60).ToString("00"); s.ShutdownWhenDone = shut.Checked;
            s.Clipboard = clip.Checked; s.Notify = notify.Checked; s.CloseToTray = tray.Checked; s.StartWithWindows = start.Checked;
            s.FfmpegPath = ff.Text.Trim(); s.YtdlpPath = yt.Text.Trim(); Mgr.Held = s.ScheduleOn; Settings.Save(); Tr.Init();
        };
    }
}

// Un vrai bouton du rail : accessible au clavier et aux lecteurs d'ecran, avec couleur ET texte.
class ExtensionStatusButton : Button {
    public bool Connected { get; private set; }
    ExtensionState state = ExtensionState.Unknown;
    string caption, hint;
    public ExtensionStatusButton() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        SetState(ExtensionState.Unknown, false);
    }
    public void SetState(ExtensionState value, bool connected) {
        state = value; Connected = connected;
        caption = connected ? PrefStyle.L("התוסף מחובר", "Extension connectée", "Extension connected") : PrefStyle.L("התוסף מנותק", "Extension déconnectée", "Extension disconnected");
        hint = connected ? PrefStyle.L("החיבור לכרום פעיל", "Le lien avec Chrome est actif", "Chrome connection is active") : state == ExtensionState.Missing ? PrefStyle.L("לחצו להוספת התוסף", "Cliquer pour réinstaller", "Click to reinstall") : PrefStyle.L("לחצו לחידוש החיבור", "Rétablir la connexion", "Restore the connection");
        AccessibleName = caption + ". " + hint; Text = AccessibleName;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = Connected ? Color.FromArgb(15, 125, 88) : Color.FromArgb(180, 35, 52);
        g.Clear(St.Sunk);
        St.Fill(g, new Rectangle(0, 0, Width - 1, Height - 1), St.P(9), Connected ? Color.FromArgb(231, 245, 238) : Color.FromArgb(255, 237, 238));
        int dot = St.P(7), pad = St.P(10), dx = Tr.Rtl ? Width - pad - dot : pad;
        using (var b = new SolidBrush(color)) g.FillEllipse(b, dx, St.P(15), dot, dot);
        int tx = Tr.Rtl ? pad : pad + St.P(14);
        St.Text(g, caption, Fonts.M(11), new Rectangle(tx, St.P(7), Width - 2 * pad - St.P(14), St.P(22)), color, 0);
        St.Text(g, hint, Fonts.R(11), new Rectangle(pad, St.P(30), Width - 2 * pad, St.P(20)), color, 0);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 3, Width - 7, Height - 7));
    }
}
