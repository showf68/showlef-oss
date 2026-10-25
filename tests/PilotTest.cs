// Tests du moteur « comme IDM » (app/Pilot.cs) sur un jeu d'essai REEL (tests/fixture.mjs), sans reseau.
//   powershell -File tests\build-pilot-test.ps1   puis   pilot-test.exe <jeu d'essai> <sortie JS de frontier.mjs>
// 1. protobuf : relire puis reecrire chaque requete du lecteur rend les memes octets ;
// 2. UMP : chaque reponse lue d'un bloc ou coupee en morceaux de tailles quelconques donne les memes parties ;
// 3. infos : la reponse du lecteur et la page HTML donnent la meme video et les memes formats ;
// 4. FRONTIERE : pour chaque cas, le corps de requete du programme = celui de l'extension 0.7.21, octet pour octet ;
// 5. morceaux : rejouer les reponses ecrit sur le disque exactement les octets des morceaux, decoupage quelconque ou pas.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

static class PilotTest {
    static int fails, checks;
    static void Ok(bool c, string what) { checks++; if (!c) { fails++; Console.WriteLine("ECHEC " + what); } }
    static string Md5(byte[] b) { using (var m = MD5.Create()) return BitConverter.ToString(m.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

    static string Sig(List<PbF> l) { return string.Join(";", l.Select(x => x.F + "/" + x.W + "=" + (x.W == 0 ? x.V.ToString() : Md5(x.B)))); }

    static int Main(string[] args) {
        string fix = args[0], jsOut = args.Length > 1 ? args[1] : null;
        var reqs = Directory.GetFiles(fix, "req*.bin").OrderBy(x => x).ToList();
        var ress = Directory.GetFiles(fix, "res*.bin").OrderBy(x => x).ToList();
        var rnd = new Random(42);

        // 1. protobuf aller-retour. Le lecteur ecrit certaines longueurs sur 3 octets au lieu d'un (« d3 81 00 » = 211) :
        // la reecriture canonique change donc les octets mais pas le contenu (l'extension 0.7.21 faisait pareil).
        // Controle : meme suite de champs, et la forme canonique est stable.
        foreach (var f in reqs) {
            var b = File.ReadAllBytes(f); var c1 = Pb.Enc(Pb.Parse(b)); var c2 = Pb.Enc(Pb.Parse(c1));
            Ok(Md5(c1) == Md5(c2) && Sig(Pb.Parse(b)) == Sig(Pb.Parse(c1)), "pb aller-retour " + Path.GetFileName(f));
        }

        // 2. UMP : d'un bloc / en morceaux (100 decoupages aleatoires par reponse, dont des morceaux de 1 octet)
        foreach (var f in ress) {
            var b = File.ReadAllBytes(f);
            var whole = new UmpReader().Feed(b, 0, b.Length);
            string sig = string.Join(",", whole.Select(p => p.Key + ":" + Md5(p.Value)));
            for (int k = 0; k < 100; k++) {
                var rd = new UmpReader(); var parts = new List<KeyValuePair<int, byte[]>>();
                int p = 0, max = k < 10 ? 1 : k < 50 ? 1 + rnd.Next(64) : 1 + rnd.Next(200000);
                while (p < b.Length) { int n = Math.Min(b.Length - p, 1 + rnd.Next(max)); parts.AddRange(rd.Feed(b, p, n)); p += n; }
                Ok(rd.Pending == 0 && string.Join(",", parts.Select(x => x.Key + ":" + Md5(x.Value))) == sig, "ump decoupage " + Path.GetFileName(f) + " #" + k);
            }
            Console.WriteLine(Path.GetFileName(f) + " : " + whole.Count + " parties, types " + string.Join(" ", whole.GroupBy(x => x.Key).Select(g => g.Key + "x" + g.Count())));
        }

        // 3. infos : lecteur et page
        var fromPlayer = YtSite.FromPlayer(Json.Obj(File.ReadAllText(Path.Combine(fix, "player.json"), Encoding.UTF8)));
        var fromHtml = YtSite.FromHtml(File.ReadAllText(Path.Combine(fix, "page.html"), Encoding.UTF8));
        Ok(fromPlayer != null && fromHtml != null, "infos lues");
        if (fromPlayer != null && fromHtml != null) {
            Ok(fromPlayer.Vid == fromHtml.Vid && fromPlayer.Title == fromHtml.Title && fromPlayer.Duration == fromHtml.Duration, "infos : meme video");
            Ok(string.Join(",", fromPlayer.Formats.Select(x => x.Itag + "/" + x.Len + "/" + x.Lmt)) == string.Join(",", fromHtml.Formats.Select(x => x.Itag + "/" + x.Len + "/" + x.Lmt)), "infos : memes formats");
            var a = YtSite.BestAudio(fromPlayer, false); var v = YtSite.BestVideo(fromPlayer, false, YtSite.WantHeight(fromPlayer, "720", false));
            Console.WriteLine("choix 720 : audio " + a.Itag + " video " + v.Itag + " ; compat : audio " + YtSite.BestAudio(fromPlayer, true).Itag + " video "
                + YtSite.BestVideo(fromPlayer, true, YtSite.WantHeight(fromPlayer, "720", true)).Itag);
            Ok(a.Itag == 251 && v.Itag == 298, "choix des formats = ancien banc (251 + 298)");
        }
        Ok(YtSite.VidOf("https://www.youtube.com/watch?v=SfxNQPhEnM8&t=3") == "SfxNQPhEnM8" && YtSite.VidOf("https://m.youtube.com/shorts/abcDEF123") == "abcDEF123"
            && YtSite.VidOf("https://evil.com/watch?v=SfxNQPhEnM8") == null && YtSite.VidOf("https://www.youtube.com.evil.com/watch?v=SfxNQPhEnM8") == null, "identifiant de video");

        // 4. frontiere avec l'extension 0.7.21
        var inf = fromPlayer;
        if (jsOut != null && inf != null) {
            var cases = new[] { new { name = "v720", a = 251, v = 298, ao = false }, new { name = "v144", a = 251, v = 160, ao = false },
                new { name = "aud", a = 251, v = 0, ao = true }, new { name = "v1080", a = 140, v = 299, ao = false } };
            int same = 0, total = 0;
            foreach (var cs in cases) foreach (var rq in reqs) {
                var it = new Item { Id = "pilottest-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
                var A = inf.Formats.First(x => x.Itag == cs.a); var V = cs.v > 0 ? inf.Formats.First(x => x.Itag == cs.v) : null;
                var j = new SabrJob(it, inf, A, V, cs.ao, "https://x.googlevideo.com/videoplayback?sabr=1", File.ReadAllBytes(rq), 4);
                string tag = cs.name + "_" + Path.GetFileNameWithoutExtension(rq);
                total += 2;
                if (Md5(j.Body(0)) == Md5(File.ReadAllBytes(Path.Combine(jsOut, tag + "_t0.bin")))) same++; else Ok(false, "frontiere t0 " + tag);
                foreach (var rs in ress) j.Parts(new UmpReader().Feed(File.ReadAllBytes(rs), 0, (int)new FileInfo(rs).Length), 0);
                if (Md5(j.Body(123456)) == Md5(File.ReadAllBytes(Path.Combine(jsOut, tag + "_t1.bin")))) same++; else Ok(false, "frontiere t1 " + tag);
                try { Directory.Delete(it.WorkDir, true); } catch (System.Exception) { }
            }
            checks++;
            Console.WriteLine("frontiere : " + same + "/" + total + " corps identiques a l'extension 0.7.21");
        }

        // 5. morceaux ecrits = octets des parties MEDIA, d'un bloc ou decoupes
        if (inf != null) {
            var expect = new Dictionary<string, MemoryStream>();   // "itag/seq" -> octets attendus (lecture independante)
            foreach (var rs in ress) {
                var hdr = new Dictionary<int, string>();
                foreach (var p in new UmpReader().Feed(File.ReadAllBytes(rs), 0, (int)new FileInfo(rs).Length)) {
                    if (p.Key == 20) { var h = Pb.Parse(p.Value); hdr[(int)Pb.U(h, 1)] = Pb.U(h, 3) + "/" + (Pb.U(h, 8) != 0 ? "init" : Pb.U(h, 9).ToString("D8")); }
                    else if (p.Key == 21) { string k = hdr[p.Value[0]]; if (!expect.ContainsKey(k)) expect[k] = new MemoryStream(); expect[k].Write(p.Value, 1, p.Value.Length - 1); }
                }
            }
            var itags = expect.Keys.Select(k => int.Parse(k.Split('/')[0])).Distinct().ToList();
            Console.WriteLine("pistes dans les reponses : " + string.Join(" ", itags) + " (" + expect.Count + " morceaux)");
            var aud = inf.Formats.FirstOrDefault(x => x.Audio && itags.Contains(x.Itag)); var vid = inf.Formats.FirstOrDefault(x => !x.Audio && itags.Contains(x.Itag));
            foreach (bool chunked in new[] { false, true }) {
                var it = new Item { Id = "pilottest-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
                var j = new SabrJob(it, inf, aud, vid, vid == null, "https://x.googlevideo.com/videoplayback?sabr=1", File.ReadAllBytes(reqs[0]), 4);
                foreach (var rs in ress) {
                    var b = File.ReadAllBytes(rs);
                    if (!chunked) { j.Response(b, 0); continue; }
                    var rd = new UmpReader(); var parts = new List<KeyValuePair<int, byte[]>>(); int p = 0;
                    while (p < b.Length) { int n = Math.Min(b.Length - p, 1 + rnd.Next(30000)); parts.AddRange(rd.Feed(b, p, n)); p += n; }
                    j.Parts(parts, 0);
                }
                int good = 0;
                foreach (var kv in expect) {
                    string[] k = kv.Key.Split('/');
                    string f = Path.Combine(it.WorkDir, "i" + k[0], k[1] + ".bin");
                    if (File.Exists(f) && Md5(File.ReadAllBytes(f)) == Md5(kv.Value.ToArray())) good++;
                }
                Ok(good == expect.Count, "morceaux sur le disque (" + (chunked ? "decoupe" : "bloc") + ") " + good + "/" + expect.Count);
                Console.WriteLine("morceaux " + (chunked ? "decoupes" : "d'un bloc") + " : " + good + "/" + expect.Count + " identiques, recus " + it.Received + " octets, fins " + string.Join(" ", j.End.Select(e => e.Key + ":" + e.Value)));
                try { Directory.Delete(it.WorkDir, true); } catch (System.Exception) { }
            }
        }

        // 6. ATTAQUES : donnees forgees par une page (ou un programme tiers) => toutes refusees, rien d'enregistre
        {
            string req = Convert.ToBase64String(File.ReadAllBytes(reqs[0]));
            string yt = "https://www.youtube.com/watch?v=SfxNQPhEnM8";
            Func<Dictionary<string, object>, string> err = d => { var r = (Dictionary<string, object>)Pilot.Data(d); return Json.Bool(r, "ok") ? "ok" : Json.Str(r, "error"); };
            Func<string, string, string, string, Dictionary<string, object>> D = (page, rule, url, body) => new Dictionary<string, object> { { "page", page }, { "rule", rule }, { "url", url }, { "body", body } };
            var attacks = new[] {
                new { what = "modele vers un hote non autorise", d = D(yt, "sabr", "https://127.0.0.1:9555/videoplayback?sabr=1", req) },
                new { what = "modele en http", d = D(yt, "sabr", "http://rr1.googlevideo.com/videoplayback?sabr=1", req) },
                new { what = "hote sosie (googlevideo.com.evil.com)", d = D(yt, "sabr", "https://googlevideo.com.evil.com/videoplayback?sabr=1", req) },
                new { what = "page hors site", d = D("https://evil.example/watch?v=SfxNQPhEnM8", "sabr", "https://rr1.googlevideo.com/videoplayback?sabr=1", req) },
                new { what = "page sosie (youtube.com.evil.com)", d = D("https://www.youtube.com.evil.com/watch?v=SfxNQPhEnM8", "sabr", "https://rr1.googlevideo.com/videoplayback?sabr=1", req) },
                new { what = "page en http", d = D("http://www.youtube.com/watch?v=SfxNQPhEnM8", "player", "", req) },
                new { what = "regle inconnue", d = D(yt, "exec", "", req) },
                new { what = "base64 invalide", d = D(yt, "sabr", "https://rr1.googlevideo.com/videoplayback?sabr=1", "%%%") },
                new { what = "corps protobuf invalide", d = D(yt, "sabr", "https://rr1.googlevideo.com/videoplayback?sabr=1", Convert.ToBase64String(new byte[] { 0x0a, 0xff, 0xff, 0xff, 0x0f, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50 })) },
                new { what = "page sans video", d = D("https://www.youtube.com/feed/history", "sabr", "https://rr1.googlevideo.com/videoplayback?sabr=1", req) },
            };
            foreach (var at in attacks) { string r = err(at.d); Ok(r != "ok", "attaque acceptee : " + at.what); Console.WriteLine("attaque « " + at.what + " » => " + r); }
            var big = D(yt, "player", "", Convert.ToBase64String(new byte[17 * 1024 * 1024]));
            Ok(err(big) == "size", "corps trop gros refuse");
            var st = (Dictionary<string, object>)Pilot.Start(new Dictionary<string, object> { { "page", "https://evil.example/watch?v=SfxNQPhEnM8" }, { "noProbe", true } });
            Ok(!Json.Bool(st, "ok"), "demarrage hors site refuse");
            var sp = (Dictionary<string, object>)Pilot.Step(new Dictionary<string, object> { { "id", "../../etc" }, { "results", new object[0] } });
            Ok(!Json.Bool(sp, "ok"), "pas d'un travail inconnu refuse");
            // redirection SABR (partie 43) vers un hote non autorise : ignoree ; vers googlevideo : suivie
            if (inf != null) {
                var it = new Item { Id = "pilottest-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
                var j = new SabrJob(it, inf, inf.Formats.First(x => x.Itag == 251), null, true, "https://rr1.googlevideo.com/videoplayback?sabr=1", File.ReadAllBytes(reqs[0]), 4);
                Func<string, List<KeyValuePair<int, byte[]>>> redir = u => new List<KeyValuePair<int, byte[]>> { new KeyValuePair<int, byte[]>(43, Pb.Enc(new List<PbF> { PbF.Bytes(1, Encoding.UTF8.GetBytes(u)) })) };
                j.Parts(redir("https://evil.example/videoplayback"), 0);
                Ok(j.Url.StartsWith("https://rr1.googlevideo.com/"), "redirection vers un hote non autorise ignoree");
                j.Parts(redir("https://rr9---sn-x.googlevideo.com/videoplayback?sabr=1"), 0);
                Ok(j.Url.StartsWith("https://rr9---sn-x.googlevideo.com/"), "redirection googlevideo suivie");
                Console.WriteLine("redirections : hote etranger ignore, googlevideo suivi");
            }
        }

        // 7. revue Codex 21/10 : action perdue (B2), format sans taille (I7), empoisonnement d'une autre video (I4)
        if (inf != null) {
            var it = new Item { Id = "pilottest-" + Guid.NewGuid().ToString("N").Substring(0, 8), Status = "downloading" };
            var j = new SabrJob(it, inf, inf.Formats.First(x => x.Itag == 251), null, true, "https://rr1.googlevideo.com/videoplayback?sabr=1", File.ReadAllBytes(reqs[0]), 2);
            var a1 = j.Plan();
            Ok(a1.Count == 2 && j.Out.Count == 2, "2 requetes en vol");
            var a2 = j.Plan();
            Ok(a2.Count == 0, "rien de nouveau tant que les requetes sont en vol");
            j.ExpireForTest();   // les deux reponses du programme se sont perdues en route
            var a3 = j.Plan();
            Ok(a3.Count == 2 && j.Out.Count == 2 && !a3.Select(x => x["rid"]).Intersect(a1.Select(x => x["rid"])).Any(), "actions perdues redemandees apres l'echeance (" + a3.Count + ")");
            Console.WriteLine("action perdue : redemandee apres l'echeance, nouveaux numeros " + string.Join(",", a3.Select(x => x["rid"])));
            try { Directory.Delete(it.WorkDir, true); } catch (System.Exception) { }
            var noLen = YtSite.FromPlayer(Json.Obj(File.ReadAllText(Path.Combine(fix, "player.json"), Encoding.UTF8)));
            noLen.Formats.First(x => x.Itag == 251).Len = 0;
            Ok(YtSite.BestAudio(noLen, false).Itag != 251, "format sans taille jamais retenu");
            // B connue ; une page A envoie des infos forgees de B (avec DRM) : B ne change pas
            Pilot.Keep(inf);
            var fake = File.ReadAllText(Path.Combine(fix, "player.json"), Encoding.UTF8).Replace("\"streamingData\":{", "\"streamingData\":{\"drmFamilies\":[\"x\"],");
            Pilot.Data(new Dictionary<string, object> { { "page", "https://www.youtube.com/watch?v=AAAAAAAAAAA" }, { "rule", "player" }, { "url", "" }, { "body", Convert.ToBase64String(Encoding.UTF8.GetBytes(fake)) } });
            var media = (Dictionary<string, object>)((Dictionary<string, object>)Pilot.Page(new Dictionary<string, object> { { "page", "https://www.youtube.com/watch?v=" + inf.Vid } }))["media"];
            Ok(!Json.Bool(media, "drm"), "infos de B non remplacees depuis la page A");
            Console.WriteLine("empoisonnement depuis une autre page : refuse (drm=" + Json.Bool(media, "drm") + ")");
        }

        // 8. protection du flux (partie 58, statut 3 ; mesure 21/10, profil Chrome vierge) : relecture du lecteur, puis on
        // continue ; au 3e refus, echec
        if (inf != null) {
            var it = new Item { Id = "pilottest-" + Guid.NewGuid().ToString("N").Substring(0, 8), Status = "downloading" };
            var j = new SabrJob(it, inf, inf.Formats.First(x => x.Itag == 251), null, true, "https://rr1.googlevideo.com/videoplayback?sabr=1", File.ReadAllBytes(reqs[0]), 2);
            var prot = new List<KeyValuePair<int, byte[]>> { new KeyValuePair<int, byte[]>(58, Pb.Enc(new List<PbF> { PbF.Int(1, 3) })) };
            for (int round = 1; round <= 4; round++) {
                var a = j.Plan();
                j.Parts(prot, 0);
                foreach (var x in a) j.Result((int)x["rid"], false, null);
                var w0 = j.Plan();
                if (round < 4) Ok(w0.Count == 0, "protection " + round + " : rien pendant l'attente");
                j.NotBefore = DateTime.UtcNow;   // banc : fin de l'attente
                var w = j.Plan();
                if (round < 4) {
                    Ok(w.Count == 1 && (string)w[0]["op"] == "warm" && j.Out.Count == 0, "protection " + round + " : relecture demandee, aucune requete");
                    j.Result((int)w[0]["rid"], false, null);
                    Ok(j.WarmDone, "protection " + round + " : relecture finie vue");
                    j.WarmDone = false; j.Resume();
                } else Ok(j.Failed != null && j.ErrWhy == "protection_3", "4e protection : echec (" + j.Failed + ")");
            }
            Console.WriteLine("protection du flux : 3 relectures puis echec (" + j.ErrWhy + ")");
            try { Directory.Delete(it.WorkDir, true); } catch (System.Exception) { }
        }

        // 9. marqueur « l'extension est connectee » (guide d'installation 0.7.22) : un ping ecrit ext-seen.txt dans le dossier du programme
        {
            string old = App.Dir, tmp = Path.Combine(Path.GetTempPath(), "pilottest-dir-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp); App.Dir = tmp;
            Ok(!File.Exists(Path.Combine(tmp, "ext-seen.txt")), "pas de marqueur avant le ping");
            Ipc.Process("{\"cmd\":\"ping\"}");
            Ok(File.Exists(Path.Combine(tmp, "ext-seen.txt")), "le ping de l'extension ecrit le marqueur");
            App.Dir = old; try { Directory.Delete(tmp, true); } catch (System.Exception) { }
        }

        // 10. conversion audio : l'avancement (MergePct) monte pendant ffmpeg, puis le fichier existe (0.7.23)
        if (Tools.Ffmpeg != null) {
            var it = new Item { Id = "pilottest-mux-" + Guid.NewGuid().ToString("N").Substring(0, 8), Status = "merging", AudioOnly = true, AudioFormat = "mp3" };
            Directory.CreateDirectory(it.WorkDir);
            it.Folder = it.WorkDir + "-out"; it.Name = "sortie.mp3";   // hors du dossier de travail, que Mux efface
            var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg, "-y -loglevel error -f lavfi -i sine=frequency=440:duration=600 -c:a libopus \"" + Path.Combine(it.WorkDir, "audio.webm") + "\"") { UseShellExecute = false, CreateNoWindow = true });
            gen.WaitForExit();
            int maxPct = 0; bool done = false;
            var th = new System.Threading.Thread(() => { while (!done) { maxPct = Math.Max(maxPct, it.MergePct); System.Threading.Thread.Sleep(30); } }); th.Start();
            Capture.Mux(it, System.Threading.CancellationToken.None); done = true; th.Join();
            Ok(File.Exists(it.FilePath) && new FileInfo(it.FilePath).Length > 10000, "conversion audio : fichier produit");
            Ok(maxPct >= 1, "conversion audio : avancement vu (max " + maxPct + " %)");
            Ok(it.MergePct == 0, "conversion audio : avancement remis a zero");
            Console.WriteLine("conversion audio : avancement max vu " + maxPct + " %");

            // « tel quel » (0.7.24) : la piste Opus est recopiee en .opus, sans conversion, quasi instantanement
            var it2 = new Item { Id = "pilottest-orig-" + Guid.NewGuid().ToString("N").Substring(0, 8), Status = "merging", AudioOnly = true, AudioFormat = "original" };
            Directory.CreateDirectory(it2.WorkDir);
            it2.Folder = it2.WorkDir + "-out"; it2.Name = "sortie.opus";
            var gen2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg, "-y -loglevel error -f lavfi -i sine=frequency=440:duration=600 -c:a libopus \"" + Path.Combine(it2.WorkDir, "audio.webm") + "\"") { UseShellExecute = false, CreateNoWindow = true });
            gen2.WaitForExit();
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            Capture.Mux(it2, System.Threading.CancellationToken.None);
            Ok(Path.GetExtension(it2.FilePath) == ".opus" && File.Exists(it2.FilePath) && new FileInfo(it2.FilePath).Length > 10000, "tel quel : fichier .opus produit");
            Ok(sw2.ElapsedMilliseconds < 5000, "tel quel : instantane (" + sw2.ElapsedMilliseconds + " ms)");
            Console.WriteLine("tel quel : " + sw2.ElapsedMilliseconds + " ms, " + new FileInfo(it2.FilePath).Length + " octets");
            try { Directory.Delete(it2.Folder, true); } catch (System.Exception) { }
            try { Directory.Delete(it.Folder, true); } catch (System.Exception) { }
        }

        // 11. signal quotidien (0.7.25) : lecture des comptes Chrome sans erreur ; rien n'est envoye depuis un banc d'essai ni si le reglage est coupe
        {
            var acc = UsersPing.ChromeAccounts();
            Ok(acc != null && acc.All(x => x.Contains("@")), "comptes Chrome lus (" + acc.Count + ")");
            string old = App.Dir, tmp = Path.Combine(Path.GetTempPath(), "pilottest-ping-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp); App.Dir = tmp;
            UsersPing.Once();   // App.Dir != dossier par defaut : banc d'essai => aucun envoi, aucun fichier
            Ok(Directory.GetFiles(tmp).Length == 0, "banc d'essai : aucun signal ni fichier");
            App.Dir = old; try { Directory.Delete(tmp, true); } catch (System.Exception) { }
        }

        // 12. file d'attente et disque (0.7.27)
        {
            // Items est du plus recent au plus ancien : c = le plus recent
            var c = new Item { Id = "c" }; var b2 = new Item { Id = "b" }; var a0 = new Item { Id = "a" }; var run = new Item { Id = "r", Status = "downloading" };
            var ord = string.Join("", Mgr.QueueOrder(new[] { c, run, b2, a0 }).Select(x => x.Id));
            Ok(ord == "abc", "file : le plus ancien d'abord (" + ord + ")");
            b2.Prio = 5;
            ord = string.Join("", Mgr.QueueOrder(new[] { c, run, b2, a0 }).Select(x => x.Id));
            Ok(ord == "bac", "file : « ensuite » passe devant (" + ord + ")");
            Ok(Mgr.DiskShort(Path.GetTempPath(), 1024, 1.0) == null, "disque : petit fichier accepte");
            string msg = Mgr.DiskShort(Path.GetTempPath(), long.MaxValue / 4, 1.0);
            Ok(msg != null && ErrCode.Of(msg) == "disk", "disque : enorme refuse et traduit en code disk (" + msg + ")");
        }

        // 13. informations pour le support : ni cookie, ni adresse complete, ni nom d'utilisateur Windows
        {
            var sec = new Item { Id = "sup", Url = "https://www.example.com/watch?v=SECRETTOKEN123&sig=ABCDEF", Kind = "capture", Status = "error", Error = @"HTTP 403 C:\Users\" + Environment.UserName + @"\x" };
            sec.Cookies.Add(new Dictionary<string, object> { { "name", "SID" }, { "value", "COOKIEVALUE999" } });
            lock (Mgr.Items) Mgr.Items.Insert(0, sec);
            string info = UsersPing.SupportInfo();
            lock (Mgr.Items) Mgr.Items.Remove(sec);
            Ok(info.Contains("ShowLef " + App.Version) && info.Contains("https://www.example.com/…"), "support : versions et adresse reduite au site");
            Ok(!info.Contains("SECRETTOKEN123") && !info.Contains("COOKIEVALUE999") && !info.Contains("sig=ABCDEF"), "support : ni jeton d'adresse ni cookie");
            Ok(Environment.UserName.Length < 3 || !info.Contains(Environment.UserName), "support : nom d'utilisateur Windows masque");
        }

        // 13. noms de fichiers propres (0.7.29)
        {
            Func<string, string, string> chk = (inp, exp) => { string got = Fmt.ShortTitle(inp); Ok(got == exp, "nom : \"" + inp + "\" => \"" + got + "\""); return got; };
            chk("שיעור במשנה ברורה #שיעור #תורה \uD83D\uDD25", "שיעור במשנה ברורה");
            chk("Avraham Fried - Hatzemach Tzedek (Official Video) [HD]", "Avraham Fried - Hatzemach Tzedek");
            chk("Cours n°12 - Chabbat (partie 2) - YouTube", "Cours n°12 - Chabbat (partie 2)");
            Ok(Fmt.ShortTitle("#### ").Length > 0, "nom : jamais vide");
        }

        // 14. balises audio (0.7.30) : titre + pochette ecrits dans le MP3
        if (Tools.Ffmpeg != null) {
            var it = new Item { Id = "pilottest-tag-" + Guid.NewGuid().ToString("N").Substring(0, 8), Status = "merging", AudioOnly = true, AudioFormat = "mp3", Title = "שיעור בדיקה #תורה", Url = "https://example.com/watch?v=1" };
            Directory.CreateDirectory(it.WorkDir); it.Folder = it.WorkDir + "-out"; it.Name = "tag.mp3"; Directory.CreateDirectory(Thumbs.Dir);
            string cov = Path.Combine(Thumbs.Dir, it.Id + ".jpg");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg, "-y -loglevel error -f lavfi -i color=c=blue:s=208x117 -frames:v 1 \"" + cov + "\"") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg, "-y -loglevel error -f lavfi -i sine=frequency=440:duration=5 -c:a libopus \"" + Path.Combine(it.WorkDir, "audio.webm") + "\"") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            Capture.Mux(it, System.Threading.CancellationToken.None);
            var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg, "-hide_banner -i \"" + it.FilePath + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 });
            string info = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            // ffmpeg ecrit sa sortie d'erreur en page de code Windows (« ? » pour l'hebreu) : on cherche donc le « ש » (U+05E9, UTF-16 E9 05) dans les octets ID3
            byte[] mp3 = File.ReadAllBytes(it.FilePath); bool heb = false; for (int q = 0; q + 1 < mp3.Length; q++) if (mp3[q] == 0xE9 && mp3[q + 1] == 0x05) { heb = true; break; }
            Ok(heb, "balises : titre hebreu ecrit dans l'ID3");
            Ok(info.Contains("Video: mjpeg") || info.Contains("Video: png") || info.Contains("attached pic"), "balises : pochette ecrite");
            try { File.Delete(cov); Directory.Delete(it.Folder, true); } catch (System.Exception) { }
        }

        // 15. mises a jour : comparaison de versions et lecture reelle des fiches publiees sur le site
        {
            Ok(SelfUpdate.Cmp("0.7.22", "0.7.32") < 0 && SelfUpdate.Cmp("0.7.32", "0.7.32") == 0 && SelfUpdate.Cmp("0.7.9", "0.7.10") < 0 && SelfUpdate.Cmp("0.8.0", "0.7.99") > 0, "versions : comparaison numerique (0.7.9 < 0.7.10)");
            Ok(!SelfUpdate.Newer(App.Version) && SelfUpdate.Newer("99.0.0") && !SelfUpdate.Newer("abc"), "versions : « plus recente » juste");
            try { var l = SelfUpdate.Latest(); Ok(Json.Str(l, "version") != null && System.Text.RegularExpressions.Regex.IsMatch(Json.Str(l, "sha256") ?? "", "^[0-9a-f]{64}$"), "site : fiche showlef-latest.json lue (" + Json.Str(l, "version") + ")"); }
            catch (System.Exception e) { Ok(false, "site : fiche illisible " + e.Message); }
        }

        // 16. bandeau de l'extension pendant la conversion audio (0.7.34) : pourcentage et temps restant
        {
            var cv = new Item { Id = "pilottest-conv", Name = "c.mp3", Status = "merging", MergePct = 40, MergeStart = DateTime.UtcNow.AddSeconds(-20) };
            Mgr.Items.Insert(0, cv);
            var lst = (List<Dictionary<string, object>>)Mgr.ListForExtension(); var e0 = lst.First(x => (string)x["id"] == "pilottest-conv");
            Ok((string)e0["status"] == "downloading" && (long)e0["total"] == 1000 && (long)e0["received"] == 400, "bandeau : conversion 40 % presentee comme un telechargement");
            long eta0 = (long)e0["eta"]; Ok(eta0 >= 28 && eta0 <= 32, "bandeau : temps restant ~30 s (20 s pour 40 % => 30 s ; lu " + eta0 + ")");
            Mgr.Items.Remove(cv);
        }

        Console.WriteLine((fails == 0 ? "OK" : "ECHECS " + fails) + " (" + checks + " controles)");
        return fails == 0 ? 0 : 1;
    }
}
