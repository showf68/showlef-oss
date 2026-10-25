// showlef-host.exe - hote « native messaging » lance par Chrome/Edge.
// Lit les messages (4 octets de longueur + JSON) un par un, relaie chacun a ShowLef.exe par le tube nomme
// (en lancant ShowLef.exe s'il ne tourne pas), renvoie la reponse au meme format ; quitte quand Chrome ferme l'entree.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Host {
    static int Main(string[] args) {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        // Boucle (0.7.11) : sendNativeMessage n'envoie qu'UN message puis ferme l'entree (meme comportement qu'avant) ;
        // connectNative garde l'hote ouvert et y fait passer les morceaux YouTube sans relancer un processus a chaque
        // fois (un lancement par morceau plafonnait le MP3 direct a ~1 Mo/s). Reponses dans l'ordre des messages.
        while (true) {
            string reply, msg;
            try {
                msg = Read(stdin);
                if (msg == null) return 0;
                reply = Relay(msg);
            } catch (Exception e) {
                reply = "{\"ok\":false,\"error\":" + Quote("host: " + e.Message) + "}";
            }
            Write(stdout, reply ?? "{\"ok\":false,\"error\":\"no reply\"}");
        }
    }

    static string Pipe { get { return "showlef-" + Environment.UserName.ToLowerInvariant(); } }

    static string Relay(string msg) {
        try { return Send(msg, 400); }
        catch (TimeoutException) { }
        catch (IOException) { }
        // l'attente de reprise (pilot_wait) ne lance jamais le programme : elle n'a de sens que s'il tourne deja
        if (msg.IndexOf("\"cmd\":\"pilot_wait\"", StringComparison.Ordinal) >= 0) return "{\"ok\":false,\"error\":\"app down\"}";
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ShowLef.exe");
        if (!File.Exists(exe)) throw new Exception("ShowLef.exe not found next to the host");
        // UseShellExecute = true : ShowLef.exe ne doit PAS heriter de stdout, sinon Chrome attend sa fermeture
        Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true });
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true) {
            try { return Send(msg, 1000); }
            catch (Exception) { if (DateTime.UtcNow > until) throw new Exception("ShowLef.exe did not answer"); Thread.Sleep(250); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

    // Le tube porte le nom de l'utilisateur : un autre programme pourrait le creer avant ShowLef et recevoir les
    // cookies et adresses envoyes par Chrome (revue Codex n° 3). On ne parle qu'au ShowLef.exe de CE dossier.
    static void CheckServer(NamedPipeClientStream c) {
        uint pid;
        if (!GetNamedPipeServerProcessId(c.SafePipeHandle.DangerousGetHandle(), out pid)) throw new Exception("pipe server unknown");
        string want = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ShowLef.exe"));
        string got;
        using (var p = Process.GetProcessById((int)pid)) got = Path.GetFullPath(p.MainModule.FileName);
        if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) throw new Exception("pipe server is not ShowLef: " + got);
    }

    static string Send(string json, int timeoutMs) {
        using (var c = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut)) {
            c.Connect(timeoutMs);
            CheckServer(c);
            var b = new UTF8Encoding(false).GetBytes(json.Replace("\r", "").Replace("\n", " ") + "\n");
            c.Write(b, 0, b.Length); c.Flush();
            return new StreamReader(c, new UTF8Encoding(false)).ReadLine();
        }
    }

    static string Read(Stream s) {
        var len = new byte[4];
        if (Fill(s, len) < 4) return null;
        int n = BitConverter.ToInt32(len, 0);
        if (n <= 0 || n > 64 * 1024 * 1024) return null;
        var buf = new byte[n];
        if (Fill(s, buf) < n) return null;
        return Encoding.UTF8.GetString(buf);
    }
    static int Fill(Stream s, byte[] b) {
        int got = 0, r;
        while (got < b.Length && (r = s.Read(b, got, b.Length - got)) > 0) got += r;
        return got;
    }
    static void Write(Stream s, string json) {
        var b = new UTF8Encoding(false).GetBytes(json);
        s.Write(BitConverter.GetBytes(b.Length), 0, 4);
        s.Write(b, 0, b.Length);
        s.Flush();
    }
    static string Quote(string v) {
        var sb = new StringBuilder("\"");
        foreach (char c in v) {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
