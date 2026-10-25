using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;

static class SettingsTest {
    static int checks;
    static object Field(object o, string name) { return o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o); }
    static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args); }
    static void Until(Func<bool> done) { var end = DateTime.UtcNow.AddSeconds(4); do { Application.DoEvents(); System.Threading.Thread.Sleep(10); } while (!done() && DateTime.UtcNow < end); Expect(done(), "Attente bornee terminee"); }
    static void Expect(bool yes, string message) { checks++; if (!yes) throw new Exception(message); }
    static IEnumerable<Control> All(Control c) { foreach (Control ch in c.Controls) { yield return ch; foreach (Control sub in All(ch)) yield return sub; } }
    static void Write(string root, string name, string text) { string f = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text); }
    static string Registered(bool disabled) { return "{\"extensions\":{\"settings\":{\"" + ExtensionPresence.DevId + "\":{\"path\":\"extension\",\"state\":" + (disabled ? "0" : "1") + "}}}}"; }
    [STAThread]
    static int Main(string[] args) {
        string root = Path.Combine(Path.GetTempPath(), "showlef-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); App.Dir = Path.Combine(root, "app"); Directory.CreateDirectory(App.Dir);
        try {
            string ud = Path.Combine(root, "chrome");
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Missing, "Chrome absent");
            Write(ud, "Local State", "{\"profile\":{\"last_used\":\"Default\"}}");
            Write(ud, "Default/Secure Preferences", "{\"extensions\":{\"settings\":{}}}");
            Write(App.Dir, "ext-seen.txt", DateTime.UtcNow.ToString("o"));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Missing, "Un ancien ping ne masque pas la desinstallation");
            Write(ud, "Default/Secure Preferences", "{\"extensions\":{\"settings\":{\"" + ExtensionPresence.DevId + "\":{}}}}");
            Write(ud, "Default/Preferences", Registered(false));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Missing, "Entree vide prioritaire sur Preferences perimees");
            Write(ud, "Default/Secure Preferences", Registered(false));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Present, "Extension active");
            Write(ud, "Default/Secure Preferences", Registered(true));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Disabled, "Extension desactivee");
            Write(ud, "Default/Secure Preferences", Registered(false).Replace("\"state\":1", "\"disable_reasons\":[1]"));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Disabled, "Chrome recent : tableau disable_reasons");
            Write(ud, "Default/Secure Preferences", Registered(false).Replace("\"state\":1", "\"disable_reasons\":[]"));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Present, "Chrome recent : sans motif de desactivation");
            Write(ud, "Default/Secure Preferences", Registered(false).Replace(ExtensionPresence.DevId, ExtensionPresence.StoreId));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Present, "Identifiant Store");
            Write(ud, "Default/Secure Preferences", "{");
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Unknown, "Lecture incomplete != absente");
            Write(ud, "Default/Secure Preferences", "{\"extensions\":{\"settings\":{}}}");
            Write(ud, "Profile 1/Secure Preferences", Registered(false));
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Missing, "Un autre profil ne masque pas le profil courant");
            Write(ud, "Local State", "{\"profile\":{\"last_used\":\"Profile 1\"}}");
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Present, "Changement de profil suivi");
            Write(ud, "Local State", "broken");
            Expect(ExtensionPresence.Check(ud) == ExtensionState.Unknown, "Local State illisible");
            Expect(!Ipc.ExtensionConnected, "Le vieux fichier ext-seen ne rend pas le voyant vert");
            Ipc.Process("{\"cmd\":\"ping\"}");
            Expect(Ipc.ExtensionConnected, "Une communication reelle rend le contact recent");
            Expect(!ExtensionPresence.RecentContact(DateTime.UtcNow.AddSeconds(-31).Ticks, DateTime.UtcNow), "Le contact expire apres 30 s");
            Expect(!ExtensionPresence.RecentContact(DateTime.UtcNow.AddSeconds(5).Ticks, DateTime.UtcNow), "Un horodatage futur ne valide rien");
            string expired = Guid.NewGuid().ToString("N");
            Expect(!ExtensionProbe.Check(expired, 0), "Aucune reponse : pas de confirmation");
            Expect(!ExtensionProbe.Acknowledge(expired, DateTime.UtcNow.AddSeconds(5)), "Reponse expiree refusee");
            Expect(!ExtensionProbe.Acknowledge(Guid.NewGuid().ToString("N"), DateTime.UtcNow), "Reponse inconnue refusee");
            Expect(ExtensionProbe.Acknowledge(expired, DateTime.UtcNow), "Reponse correlee acceptee");
            Expect(!ExtensionProbe.Check(Guid.NewGuid().ToString("N"), 0), "Une ancienne reponse ne valide pas une nouvelle demande");
            Wake.Wait(1); // vider seulement les demandes du banc precedent
            string pipeName = "showlef-guide-test-" + Guid.NewGuid().ToString("N"), token = Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut)) {
                var relay = new Thread(() => { server.WaitForConnection(); var line = new StreamReader(server).ReadLine(); var writer = new StreamWriter(server) { AutoFlush = true }; writer.WriteLine(Json.Ser(Ipc.Process(line))); });
                var extension = new Thread(() => { var response = (Dictionary<string, object>)Wake.Wait(1); foreach (var wake in (List<Dictionary<string, object>>)response["wakes"]) if (Convert.ToString(wake["token"]) == token) Ipc.Process("{\"cmd\":\"extension_probe_reply\",\"token\":\"" + token + "\"}"); });
                relay.IsBackground = extension.IsBackground = true; relay.Start(); extension.Start();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Expect(SetupMain.ConfirmConnection(token, pipeName), "Guide -> pipe -> attente extension -> reponse -> guide, sans preferences Chrome");
                Console.WriteLine("Confirmation directe : " + watch.ElapsedMilliseconds + " ms (reponse extension simulee)");
                Expect(relay.Join(2000) && extension.Join(2000), "Canal de verification termine");
            }

            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Fonts.Init();
            foreach (int language in new[] { 0, 1, 2 }) {
                Settings.Current = new Settings(); Tr.Lang = language; St.S = 1;
                using (var f = new SettingsForm()) {
                    f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-32000, -32000); f.Show(); Application.DoEvents();
                    var tabs = All(f).OfType<PrefTab>().ToList(); Expect(tabs.Count == 5, "Cinq rubriques");
                    foreach (var tab in tabs) { tab.PerformClick(); Application.DoEvents(); Expect(tabs.Count(x => x.Selected) == 1 && tab.Selected, "Selection de rubrique"); }
                    var con = All(f).OfType<NumericUpDown>().Single(c => c.AccessibleName == Tr.T("s_conn")); con.Value = 12;
                    All(f).OfType<NumericUpDown>().Single(c => c.AccessibleName == Tr.T("s_parallel")).Value = 4;
                    All(f).OfType<NumericUpDown>().Single(c => c.AccessibleName == Tr.T("s_limit")).Value = 800;
                    All(f).OfType<NumericUpDown>().Single(c => c.AccessibleName == Tr.T("s_retries")).Value = 3;
                    All(f).OfType<TextBox>().Single(c => c.AccessibleName == Tr.T("saveTo")).Text = Path.Combine(root, "downloads");
                    All(f).OfType<TextBox>().Single(c => c.AccessibleName == Tr.T("s_ffmpeg")).Text = "C:/tools/ffmpeg.exe";
                    All(f).OfType<TextBox>().Single(c => c.AccessibleName == Tr.T("s_ytdlp")).Text = "C:/tools/yt-dlp.exe";
                    All(f).OfType<ComboBox>().Single(c => c.AccessibleName == Tr.T("quality")).SelectedIndex = 1;
                    All(f).OfType<ComboBox>().Single(c => c.AccessibleName == Tr.T("audioFmt")).SelectedIndex = 2;
                    All(f).OfType<ComboBox>().Single(c => c.AccessibleName == Tr.T("s_speed")).SelectedIndex = 2;
                    All(f).OfType<DateTimePicker>().Single().Value = DateTime.Today.AddHours(16).AddMinutes(35);
                    var allTiles = All(f).OfType<PrefTile>().ToList(); Expect(allTiles.Count == 8, "Tous les reglages booleens presents");
                    var first = allTiles.First(); bool before = first.Checked;
                    typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(first, new object[] { EventArgs.Empty });
                    Expect(first.Checked != before, "Tuile cliquable change son etat");
                    foreach (var tile in allTiles) tile.Checked = true;
                    f.SelectPage(1); ((Button)f.AcceptButton).PerformClick();
                    Expect(Settings.Current.Connections == 12, "Enregistrer conserve la valeur modifiee");
                    Expect(Settings.Current.MaxParallel == 4 && Settings.Current.SpeedLimitKB == 800 && Settings.Current.Retries == 3 && Settings.Current.Quality == "480", "Reglages de telechargement tous conserves");
                    Expect(Settings.Current.AudioFormat == "original" && Settings.Current.AudioSpeed == "1.5" && Settings.Current.Loudnorm, "Audio conserve");
                    Expect(Settings.Current.ScheduleOn && Settings.Current.ScheduleTime == "16:35" && Settings.Current.ShutdownWhenDone, "Programmation conservee");
                    Expect(Settings.Current.SortByType && Settings.Current.StartWithWindows && Settings.Current.CloseToTray && Settings.Current.Notify && Settings.Current.Clipboard, "Options generales conservees");
                    Expect(Settings.Current.Folder == Path.Combine(root, "downloads") && Settings.Current.FfmpegPath == "C:/tools/ffmpeg.exe" && Settings.Current.YtdlpPath == "C:/tools/yt-dlp.exe", "Chemins conserves");
                }
                Settings.Load(); Expect(Settings.Current.Connections == 12, "Valeur conservee sur disque");
                using (var f = new SettingsForm()) {
                    All(f).OfType<NumericUpDown>().Single(c => c.AccessibleName == Tr.T("s_conn")).Value = 7;
                    ((Button)f.CancelButton).PerformClick();
                    Expect(Settings.Current.Connections == 12, "Annuler ne modifie rien");
                }
                int repairs = 0;
                using (var f = new Form { ClientSize = new Size(860, 490) }) {
                    var surface = new Surface { Dock = DockStyle.Fill }; f.Controls.Add(surface);
                    surface.OnAction = (key, item) => { if (key == "extension") repairs++; };
                    f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-32000, -32000); f.Show(); Application.DoEvents();
                    surface.ExtensionStatus.SetState(ExtensionState.Missing, false);
                    surface.ExtensionStatus.PerformClick(); Expect(repairs == 1, "Indicateur du rail branche a l'action de reparation");
                    Expect(surface.ExtensionStatus.Bottom <= surface.Height, "Indicateur visible dans une petite fenetre");
                    Expect(!surface.ExtensionStatus.Connected, "Suppression : indicateur deconnecte");
                    surface.ExtensionStatus.SetState(ExtensionState.Present, true);
                    Expect(surface.ExtensionStatus.Connected, "Communication : indicateur connecte");
                    surface.ExtensionStatus.SetState(ExtensionState.Present, false);
                    Expect(!surface.ExtensionStatus.Connected, "Installee mais sans communication : pas de vert");
                }
                SetupMain.L = language == 0 ? 1 : language == 1 ? 2 : 0;
                bool installed = false;
                using (var guide = new SetupForm(false, true, () => installed)) {
                    guide.ShowInTaskbar = false; guide.StartPosition = FormStartPosition.Manual; guide.Location = new Point(-32000, -32000); guide.Show();
                    Until(() => Field(guide, "gSpinner") != null);
                    Call(guide, "GuideGo", 3);
                    var spinner = (GuideSpinner)Field(guide, "gSpinner");
                    Expect(spinner.Running, "Etape 3 : animation automatique");
                    int angle = (int)Field(spinner, "angle"); Until(() => (int)Field(spinner, "angle") != angle);
                    var check = (Button)Field(guide, "gMain"); check.PerformClick();
                    Expect(check.Enabled, "Verification manuelle disponible pendant la veille");
                    Until(() => !(bool)Field(guide, "checking"));
                    Expect(spinner.Running, "Pas detectee : verification reste active");
                    string shots = Path.Combine(Path.GetTempPath(), "showlef-guide-0739"); Directory.CreateDirectory(shots);
                    using (var bmp = new Bitmap(guide.Width, guide.Height)) { guide.DrawToBitmap(bmp, new Rectangle(Point.Empty, guide.Size)); bmp.Save(Path.Combine(shots, "step3-" + language + ".png")); }
                    Call(guide, "GuideGo", 2); Expect(!spinner.Running, "Retour : animation arretee");
                    Call(guide, "GuideGo", 3); installed = true;
                    Until(() => spinner.IsDisposed);
                    Expect(!((System.Windows.Forms.Timer)Field(guide, "guideTimer")).Enabled, "Detection automatique : veille arretee sur succes");
                }
            }
            Tr.Lang = 0; St.S = 1.5f;
            using (var f = new SettingsForm()) {
                f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-32000, -32000); f.Show(); Application.DoEvents();
                var tile = All(f).OfType<PrefTile>().First(c => c.Glyph == "link");
                var scroller = (ScrollableControl)tile.Parent.Parent;
                scroller.ScrollControlIntoView(tile); Application.DoEvents();
                var bounds = scroller.RectangleToClient(tile.RectangleToScreen(tile.ClientRectangle));
                Expect(bounds.Bottom <= scroller.ClientSize.Height && bounds.Top >= 0, "Derniere tuile accessible a 150 %");
                Expect(((Control)f.AcceptButton).Visible, "Enregistrer reste visible a 150 %");
            }
            Console.WriteLine("OK " + checks + " controles detection et reglages; profil reel : " + ExtensionPresence.Check()); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally {
            string full = Path.GetFullPath(root), prefix = Path.Combine(Path.GetTempPath(), "showlef-settings-test-");
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Nettoyage hors du banc refuse");
            Directory.Delete(full, true);
        }
    }
}
