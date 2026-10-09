using System;
using System.Collections.Generic;
using System.IO;
#if !UPDATER_CHECK
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using KrokoshaCasualtiesMP;
#endif

namespace MP3Client {
    internal static class UpdaterConfig {
        public const string RELEASE_URL = "https://api.github.com/repos/02huydini/MP3Client/releases/latest";
        public const string PLUGIN_VERSION = "0.6.3";
        public const string TAG = "MP3Client";
    }
    internal sealed class UpdateFiles {
        private readonly string dllPath;
        internal UpdateFiles(string dllPath) {
            this.dllPath = dllPath;
        }
        internal string DllPath { get { return dllPath; } }
        internal string PendingPath { get { return dllPath + ".pending"; } }
        internal string PendingTempPath { get { return dllPath + ".pending.tmp"; } }
        internal string BackupPath { get { return dllPath + ".bak"; } }
        internal string OriginalPrefix { get { return Path.GetFileName(dllPath) + ".orig-"; } }
        internal static bool LooksLikeDll(byte[] data) {
            return data != null && data.Length >= 1024 && data[0] == 0x4D && data[1] == 0x5A;
        }
        internal bool Promote() {
            if (!File.Exists(PendingPath)) return false;
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            File.Move(dllPath, BackupPath);
            try {
                File.Move(PendingPath, dllPath);
            } catch (Exception) {
                File.Move(BackupPath, dllPath);
                throw;
            }
            return true;
        }
        internal int Install(byte[] data, out string error, out bool promoted) {
            error = null;
            promoted = false;
            try {
                if (!LooksLikeDll(data)) throw new IOException("the file is not a valid plugin dll");
                File.WriteAllBytes(PendingTempPath, data);
                if (File.Exists(PendingPath)) File.Delete(PendingPath);
                File.Move(PendingTempPath, PendingPath);
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(PendingTempPath)) File.Delete(PendingTempPath); } catch (Exception) { }
                return 0;
            }
            try {
                promoted = Promote();
            } catch (Exception e) {
                error = e.Message;
            }
            return promoted ? 2 : 1;
        }
        internal bool FindOriginal(out string path, out string version) {
            path = null;
            version = null;
            Version best = null;
            string dir = Path.GetDirectoryName(dllPath);
            string prefix = OriginalPrefix;
            foreach (string file in Directory.GetFiles(dir, prefix + "*")) {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string text = name.Substring(prefix.Length);
                Version parsed;
                if (!Version.TryParse(text, out parsed)) continue;
                if (best == null || parsed < best) {
                    best = parsed;
                    path = file;
                    version = text;
                }
            }
            return path != null;
        }
        internal bool BackupOriginal(string version, out string error) {
            error = null;
            string target = dllPath + ".orig-" + version;
            string temp = target + ".tmp";
            try {
                string existing, existingVersion;
                if (FindOriginal(out existing, out existingVersion)) return false;
                byte[] data;
                using (var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length) {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != data.Length) throw new IOException("short read of the running plugin file");
                }
                if (!LooksLikeDll(data)) throw new IOException("the running plugin file is not a valid dll");
                File.WriteAllBytes(temp, data);
                if (new FileInfo(temp).Length != data.Length) throw new IOException("short write of the backup file");
                File.Move(temp, target);
                return true;
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
        }
        internal int Cleanup(List<string> notes) {
            int removed = 0;
            var targets = new List<string> { BackupPath, PendingTempPath };
            try {
                targets.AddRange(Directory.GetFiles(Path.GetDirectoryName(dllPath), OriginalPrefix + "*.tmp"));
            } catch (Exception) { }
            foreach (string path in targets) {
                try {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    removed++;
                    notes.Add("removed " + Path.GetFileName(path));
                } catch (Exception e) {
                    notes.Add("couldn't remove " + Path.GetFileName(path) + " yet (" + e.Message + "), will retry next launch");
                }
            }
            return removed;
        }
    }
#if !UPDATER_CHECK
    internal static class Updater {
        private static UpdateFiles files;
        private static ConfigEntry<bool> autoUpdate;
        private static ConfigEntry<string> skippedVersion;
        private static ConfigEntry<bool> revertRequested;
        private static string availableVersion;
        private static string availableUrl;
        private static bool queuing;
        private static bool swappedThisSession;
        private static bool revertedThisSession;
        private static UpdateFiles Files {
            get {
                if (files == null) files = new UpdateFiles(Assembly.GetExecutingAssembly().Location);
                return files;
            }
        }
        internal static bool AutoEnabled { get { return autoUpdate == null || autoUpdate.Value; } }
        internal static string SetAuto(bool enabled) {
            if (autoUpdate != null) autoUpdate.Value = enabled;
            return enabled ? "auto-update is on, new versions install by themselves." : "auto-update is off, nothing installs by itself. You get a prompt, and MP3update queues the newest version.";
        }
        internal static void BindConfig(ConfigFile config) {
            autoUpdate = config.Bind("Updater", "AutoUpdate", true, "On: new versions are downloaded and installed by themselves. Off: nothing is installed on its own, a prompt asks you, and the console command MP3update queues the newest version. Going back to your original version turns this off.");
            skippedVersion = config.Bind("Updater", "SkippedVersion", "", "The version you chose Skip for in the update prompt. It is cleared when you queue an update or go back to the original version.");
            revertRequested = config.Bind("Updater", "RevertToOriginal", false, "Set to true to go back to the version you first installed. A permanent copy of it is kept next to the plugin as MP3Client.dll.orig-<version>. The switch is made at once and is active after the next launch. This turns itself back to false.");
            revertRequested.SettingChanged += delegate (object sender, EventArgs args) {
                if (revertRequested.Value) RunRevertRequest();
            };
        }
        private static string Say(ManualLogSource log, string message) {
            log.LogInfo("[" + UpdaterConfig.TAG + "] " + message);
            try { KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: " + message); } catch (Exception) { }
            return message;
        }
        private static void RunRevertRequest() {
            revertRequested.Value = false;
            RevertToOriginal(MP3Client.Log);
        }
        internal static void ApplyRevertRequestAtStartup(ManualLogSource log) {
            if (revertRequested != null && revertRequested.Value) RunRevertRequest();
        }
        internal static void EnsureOriginalBackup(ManualLogSource log) {
            string error;
            bool created = Files.BackupOriginal(UpdaterConfig.PLUGIN_VERSION, out error);
            if (created) log.LogInfo("[" + UpdaterConfig.TAG + "] kept a permanent copy of the version you installed (" + UpdaterConfig.PLUGIN_VERSION + ") as " + Path.GetFileName(Files.DllPath) + ".orig-" + UpdaterConfig.PLUGIN_VERSION + ". Type MP3revert in the console to come back to it.");
            else if (error != null) log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't keep a copy of the installed version: " + error);
        }
        public static void ApplyPendingIfAny(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            if (!File.Exists(Files.PendingPath)) return;
            try {
                if (Files.Promote()) {
                    swappedThisSession = true;
                    log.LogInfo("[" + tag + "] applied pending update, now running the version downloaded last session.");
                    try {
                        string legacy = Path.Combine(Paths.ConfigPath, "mp3client.cfg");
                        if (File.Exists(legacy)) File.Delete(legacy);
                    } catch (Exception) { }
                }
            } catch (Exception e) {
                log.LogWarning("[" + tag + "] failed to apply pending update: " + e.Message);
            }
        }
        internal static void CleanupLeftovers(ManualLogSource log) {
            var notes = new List<string>();
            Files.Cleanup(notes);
            foreach (string note in notes) log.LogInfo("[" + UpdaterConfig.TAG + "] " + note);
        }
        internal static string RevertToOriginal(ManualLogSource log) {
            string path, version;
            if (!Files.FindOriginal(out path, out version)) return Say(log, "no copy of your original version was found next to the plugin, nothing to go back to.");
            SetAuto(false);
            skippedVersion.Value = "";
            Version local, original;
            bool sameVersion = Version.TryParse(UpdaterConfig.PLUGIN_VERSION, out local) && Version.TryParse(version, out original) && local == original;
            if (sameVersion && !swappedThisSession && !File.Exists(Files.PendingPath)) return Say(log, "you are already on the version you first installed (" + version + "). Auto-update is now off.");
            byte[] data;
            try {
                data = File.ReadAllBytes(path);
            } catch (Exception e) {
                return Say(log, "couldn't read your original version copy: " + e.Message);
            }
            string error;
            bool promoted;
            int result = Files.Install(data, out error, out promoted);
            if (result == 0) return Say(log, "couldn't go back to " + version + ": " + error);
            revertedThisSession = true;
            if (result == 2) return Say(log, "going back to your original version " + version + ". It is active after the next launch. Auto-update is now off, type MP3update when you want a newer version.");
            return Say(log, "your original version " + version + " is queued and will be applied on the next launch. Auto-update is now off.");
        }
        internal static string StatusText() {
            string path, version;
            string original = Files.FindOriginal(out path, out version) ? version : "none kept";
            string pending = File.Exists(Files.PendingPath) ? "yes" : "no";
            string available = availableVersion != null ? availableVersion : "unknown";
            return "running " + UpdaterConfig.PLUGIN_VERSION + ", original " + original + ", auto-update " + (AutoEnabled ? "on" : "off") + ", newer version seen " + available + ", change queued " + pending;
        }
        public static IEnumerator CheckForUpdate(ManualLogSource log) {
            return CheckForUpdate(log, false);
        }
        public static IEnumerator CheckForUpdate(ManualLogSource log, bool manual) {
            string tag = UpdaterConfig.TAG;
            string releaseUrl = UpdaterConfig.RELEASE_URL;
            string currentVersion = UpdaterConfig.PLUGIN_VERSION;
            if (!manual && revertedThisSession) yield break;
            if (File.Exists(Files.PendingPath)) {
                if (manual) Say(log, "a change is already queued and will be active after the next launch.");
                yield break;
            }
            using (UnityWebRequest www = UnityWebRequest.Get(releaseUrl)) {
                www.SetRequestHeader("User-Agent", "bepinex-plugin-updater");
                www.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) {
                    log.LogWarning("[" + tag + "] update check failed: " + www.error);
                    if (manual) Say(log, "update check failed: " + www.error);
                    yield break;
                }
                string json = www.downloadHandler.text;
                string remoteTag = ExtractField(json, "tag_name");
                if (string.IsNullOrEmpty(remoteTag)) yield break;
                string remoteVersion = remoteTag.TrimStart('v', 'V');
                Version local, remote;
                if (!Version.TryParse(currentVersion, out local) || !Version.TryParse(remoteVersion, out remote)) yield break;
                if (remote.CompareTo(local) <= 0) {
                    if (manual) Say(log, "MP3Client is up to date (" + currentVersion + ").");
                    yield break;
                }
                string dllUrl = ExtractField(json, "browser_download_url", ".dll");
                if (string.IsNullOrEmpty(dllUrl)) {
                    log.LogWarning("[" + tag + "] update " + remoteVersion + " found but release has no .dll asset.");
                    yield break;
                }
                availableVersion = remoteVersion;
                availableUrl = dllUrl;
                log.LogInfo("[" + tag + "] running " + currentVersion + ", found " + remoteVersion + " available.");
                if (!manual && !AutoEnabled) {
                    if (skippedVersion != null && skippedVersion.Value == remoteVersion) {
                        log.LogInfo("[" + tag + "] update " + remoteVersion + " was skipped by the player, not asking again.");
                        yield break;
                    }
                    MP3Client.Instance.StartCoroutine(ShowUpdatePrompt(log, remoteVersion, dllUrl));
                    yield break;
                }
            }
            yield return QueueDownload(log, availableVersion, availableUrl);
        }
        private static IEnumerator QueueDownload(ManualLogSource log, string remoteVersion, string dllUrl) {
            if (queuing) yield break;
            queuing = true;
            try {
                using (UnityWebRequest dl = UnityWebRequest.Get(dllUrl)) {
                    yield return dl.SendWebRequest();
                    if (dl.result != UnityWebRequest.Result.Success) {
                        Say(log, "update download failed: " + dl.error);
                        yield break;
                    }
                    string error;
                    bool promoted;
                    int result = Files.Install(dl.downloadHandler.data, out error, out promoted);
                    if (result == 0) {
                        Say(log, "failed to save update " + remoteVersion + ": " + error);
                        yield break;
                    }
                    if (skippedVersion != null) skippedVersion.Value = "";
                    swappedThisSession = swappedThisSession || promoted;
                    if (promoted) Say(log, "update " + remoteVersion + " installed, active after the next launch.");
                    else Say(log, "update " + remoteVersion + " downloaded, will be applied on the next launch." + (error != null ? " (" + error + ")" : ""));
                }
            } finally {
                queuing = false;
            }
        }
        private static IEnumerator ShowUpdatePrompt(ManualLogSource log, string remoteVersion, string dllUrl) {
            float deadline = Time.unscaledTime + 120f;
            while (UIChoicePrompt.instance == null && Time.unscaledTime < deadline) yield return new WaitForSecondsRealtime(0.5f);
            if (UIChoicePrompt.instance == null) {
                Say(log, "MP3Client " + remoteVersion + " is available. Type MP3update in the console to queue it.");
                yield break;
            }
            try {
                string path, originalVersion;
                string text = "MP3Client " + remoteVersion + " is available, you have " + UpdaterConfig.PLUGIN_VERSION + ". Queue the update for the next launch?";
                if (Files.FindOriginal(out path, out originalVersion) && originalVersion != UpdaterConfig.PLUGIN_VERSION) text += " Your original version " + originalVersion + " stays saved, MP3revert goes back to it.";
                var prompt = new UIChoicePrompt.Prompt();
                prompt.name = "MP3Client update";
                prompt.text = text;
                prompt.timeout = 60f;
                prompt.size = new Vector2(0.5f, 0.22f);
                prompt.defaultcanignore = true;
                var queue = new UIChoicePrompt.Prompt.PromptChoice();
                queue.text = "Queue update";
                queue.action = delegate () {
                    MP3Client.Instance.StartCoroutine(QueueDownload(log, remoteVersion, dllUrl));
                };
                var later = new UIChoicePrompt.Prompt.PromptChoice();
                later.text = "Not now";
                later.action = delegate () {
                    Say(log, "update " + remoteVersion + " postponed. Type MP3update whenever you want it.");
                };
                var skip = new UIChoicePrompt.Prompt.PromptChoice();
                skip.text = "Skip this version";
                skip.action = delegate () {
                    skippedVersion.Value = remoteVersion;
                    Say(log, "skipping " + remoteVersion + ". You won't be asked about it again.");
                };
                prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { queue, later, skip };
                UIChoicePrompt.ShowPrompt(prompt);
            } catch (Exception e) {
                CrashLog.Capture("update prompt", e);
                Say(log, "MP3Client " + remoteVersion + " is available. Type MP3update in the console to queue it.");
            }
        }
        private static string ExtractField(string json, string key, string suffix = null) {
            var pattern = "\"" + key + "\"\\s*:\\s*\"([^\"]+" + (suffix != null ? Regex.Escape(suffix) : "") + ")\"";
            var m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
    internal static class UpdaterCommands {
        private static void Add(string name, string help, Action<string[]> run) {
            try {
                Con.RegisterCommand(new Command(name, help, delegate (string[] splited) { run(splited); }, null, Array.Empty<ValueTuple<string, string>>()));
            } catch (Exception e) {
                CrashLog.Capture("command " + name, e);
            }
        }
        internal static void RegisterAll() {
            Add("MP3update", "MP3Client - check for a newer version and queue it for the next launch. Works whether auto-update is on or off.", delegate (string[] splited) {
                MP3Client.Instance.StartCoroutine(Updater.CheckForUpdate(MP3Client.Log, true));
            });
            Add("MP3autoupdate", "MP3Client - turn auto-update on or off. Usage: MP3autoupdate on, MP3autoupdate off, or no argument to flip it.", delegate (string[] splited) {
                bool enable = !Updater.AutoEnabled;
                if (splited != null && splited.Length > 1) {
                    string arg = splited[1].ToLowerInvariant();
                    if (arg == "on" || arg == "true" || arg == "1") enable = true;
                    else if (arg == "off" || arg == "false" || arg == "0") enable = false;
                }
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: " + Updater.SetAuto(enable));
            });
            Add("MP3revert", "MP3Client - go back to the version you first installed. Auto-update turns off.", delegate (string[] splited) {
                Updater.RevertToOriginal(MP3Client.Log);
            });
            Add("MP3version", "MP3Client - show the running version, the saved original version and whether auto-update is on.", delegate (string[] splited) {
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: " + Updater.StatusText());
            });
        }
    }
#endif
}