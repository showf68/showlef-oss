using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

// Meme preuve pour le programme et son assistant. Un ancien ping n'est pas une preuve de presence.
enum ExtensionState { Missing, Present, Disabled, Unknown }
static class ExtensionPresence {
    public static bool RecentContact(long ticks, DateTime now) {
        long age = now.Ticks - ticks;
        return ticks > 0 && age >= 0 && age <= TimeSpan.FromSeconds(30).Ticks;
    }
    public const string DevId = "mepcfiemlmpffknhaebjocpddbaahdfc";
    public const string StoreId = "hibookeofnmdllfleebckdkbgijpklfb";
    public static string UserData { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data"); } }
    static Dictionary<string, object> Map(object o) { return o as Dictionary<string, object>; }
    static object Get(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) ? o : null; }
    static Dictionary<string, object> Read(string file) {
        return Map(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(file)));
    }
    public static ExtensionState Check() { return Check(UserData); }
    public static ExtensionState Check(string userData) {
        try {
            if (!Directory.Exists(userData)) return ExtensionState.Missing;
            string profile = null, local = Path.Combine(userData, "Local State");
            if (File.Exists(local)) profile = Get(Map(Get(Read(local), "profile")), "last_used") as string;
            if (!string.IsNullOrEmpty(profile)) {
                if (profile != Path.GetFileName(profile) || profile == "." || profile == "..") return ExtensionState.Unknown;
                return CheckProfile(Path.Combine(userData, profile));
            }
            // Anciennes installations / profils de banc : Default d'abord, sinon le seul profil disponible.
            if (Directory.Exists(Path.Combine(userData, "Default"))) return CheckProfile(Path.Combine(userData, "Default"));
            var profiles = Directory.GetDirectories(userData, "Profile *");
            return profiles.Length == 0 ? ExtensionState.Missing : profiles.Length == 1 ? CheckProfile(profiles[0]) : ExtensionState.Unknown;
        } catch (Exception) { return ExtensionState.Unknown; }
    }
    static ExtensionState CheckProfile(string dir) {
        if (!Directory.Exists(dir)) return ExtensionState.Missing;
        bool read = false;
        foreach (string file in new[] { "Secure Preferences", "Preferences" }) {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            var root = Read(path); // lecture partielle / verrou : Unknown, jamais « absente »
            if (root == null) return ExtensionState.Unknown;
            read = true;
            var settings = Map(Get(Map(Get(root, "extensions")), "settings"));
            if (settings == null) continue;
            bool disabled = false;
            foreach (string id in new[] { DevId, StoreId }) {
                var item = Map(Get(settings, id));
                string extensionPath = Get(item, "path") as string;
                if (string.IsNullOrWhiteSpace(extensionPath)) continue;
                object state = Get(item, "state"), reasons = Get(item, "disable_reasons");
                bool off = state != null && Convert.ToInt32(state) != 1;
                var list = reasons as ICollection;
                if (list != null) off |= list.Count > 0;
                else if (reasons != null) off |= Convert.ToInt64(reasons) != 0;
                if (!off) return ExtensionState.Present;
                disabled = true;
            }
            // Secure Preferences est prioritaire : une entree vide ne doit pas ressusciter via l'autre fichier.
            return disabled ? ExtensionState.Disabled : ExtensionState.Missing;
        }
        return read ? ExtensionState.Missing : ExtensionState.Unknown;
    }
}
