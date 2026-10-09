using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace OptionalRecruit {
    internal class RecruitDebug : RecruitLog {
        const string TAG = "[debug] ";
        readonly ConfigEntry<bool> verboseEntry;
        public RecruitDebug() {
            verboseEntry = OptionalRecruit.Instance.Config.Bind("General", "DebugVerbose", false, "With Debug on, also log high-volume lines (every chat message that is not a recruit command).");
        }
        public override void Info(string msg) {
            if (DebugLog.src != null) DebugLog.src.LogInfo(TAG + msg);
        }
        public override void Verbose(string msg) {
            if (verboseEntry.Value && DebugLog.src != null) DebugLog.src.LogInfo(TAG + msg);
        }
    }
    [HarmonyPatch(typeof(ConsoleScript), "RegisterAllCommands")]
    internal static class Patch_ConsoleScript_RegisterAllCommands_Recruit {
        static void Postfix() {
            RecruitCommands.RegisterAll();
        }
    }
    internal static class RecruitCommands {
        internal static void RegisterAll() {
            if (ConsoleScript.SearchExact("recruit") != null) return;
            try {
                Con.RegisterCommand(new Command("recruit", "OptionalRecruit - recruit a specific dead player by KrokMP player ID (usage: recruit <player ID>, or just 'recruit' to clear and use auto-select).", delegate (string[] splited) {
                    HandleRecruitCommand(splited);
                }, null, Array.Empty<ValueTuple<string, string>>()));
                DebugLog.Info("Registered console command 'recruit'.");
            } catch (Exception e) {
                DebugLog.Error("Failed to register console command 'recruit': " + e);
            }
        }
        static void HandleRecruitCommand(string[] splited) {
            try {
                int startIdx = 0;
                if (splited != null && splited.Length > 0 && string.Equals(splited[0], "recruit", StringComparison.OrdinalIgnoreCase)) startIdx = 1;
                if (splited == null || startIdx >= splited.Length) {
                    RecruitUI.SetTarget(ushort.MaxValue);
                    DebugLog.Info("Console recruit command with no ID -> cleared, auto-select.");
                    return;
                }
                ushort id;
                if (!ushort.TryParse(splited[startIdx], out id)) {
                    DebugLog.Warn("Console recruit: invalid player ID \"" + splited[startIdx] + "\". Usage: recruit <player ID>.");
                    return;
                }
                RecruitUI.SetTarget(id);
                DebugLog.Info("Console recruit command -> player ID " + id);
            } catch (Exception e) {
                DebugLog.Error("HandleRecruitCommand failed: " + e);
            }
        }
    }
    [HarmonyPatch(typeof(Chat), "CheckForCommandInMessageInput")]
    internal static class Patch_Chat_CheckForCommand {
        static readonly FieldInfo inputField = AccessTools.Field(typeof(Chat), "CHAT_current_input");
        static void Postfix(bool __result) {
            if (!__result) return;
            try {
                string input = inputField == null ? null : inputField.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(input) || !input.StartsWith("/")) return;
                string cmd = input.Substring(1).Trim();
                if (cmd.Length == 0) return;
                string name = cmd.Split(' ')[0];
                if (name == "clear" || name == "?" || name == "help") return;
                if (ConsoleScript.SearchExact(name) == null) return;
                if (ConsoleScript.instance == null) {
                    DebugLog.Warn("ConsoleScript.instance is null; cannot route chat command to console.");
                    return;
                }
                Con.RunCommand(cmd);
            } catch (System.Exception e) {
                DebugLog.Error("Chat command execution failed: " + e);
            }
        }
    }
}