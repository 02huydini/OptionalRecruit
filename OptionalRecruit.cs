using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace OptionalRecruit {
    [BepInPlugin("02dumbass.optionalrecruit", "OptionalRecruit", "0.1")]
    [BepInDependency("KrokoshaCasualtiesMP")]
    public class OptionalRecruit : BaseUnityPlugin {
        internal static ManualLogSource log;
        internal static OptionalRecruit Instance;
        internal static ConfigEntry<bool> DebugMode;
        internal static bool DebugEnabled { get { return DebugMode != null && DebugMode.Value; } }
        void Awake() {
            log = Logger;
            Instance = this;
            DebugLog.Init(Logger);
            DebugMode = Config.Bind("General", "Debug", false, "Active debug mode: detailed [debug] log lines (needs the optional debug build).");
            TryUpgradeDebugLog();
            RecruitArm.ApplyHostTargetToAll = Config.Bind("General", "ApplyHostTargetToAll", true, "When a player has not chosen a recruit target themselves (for example they do not have this plugin), their Recruit click uses the Host's chosen target. Players who picked Automatic keep auto-select.");
            var harmony = new Harmony("02dumbass.optionalrecruit");
            PatchSafe(harmony, typeof(Patch_RefreshTraderInventories));
            PatchSafe(harmony, typeof(Patch_Server_SelectMostSuitablePlayerToRespawn));
            PatchSafe(harmony, typeof(Patch_ServerReceiver_RecruitPressed_Scope));
            PatchSafe(harmony, typeof(Patch_ServerChatMessage));
            PatchSafe(harmony, typeof(Patch_ConsoleScript_RegisterSpawnEntities_Guard));
            PatchOptional(harmony, "OptionalRecruit.Patch_ConsoleScript_RegisterAllCommands_Recruit");
            PatchOptional(harmony, "OptionalRecruit.Patch_Chat_CheckForCommand");
            RunSafe("VerifyRefs", VerifyRefs);
            RunSafe("RecruitArm.RegisterReceiver", RecruitArm.RegisterReceiver);
            log.LogInfo("OptionalRecruit loaded, now you can hate your friends.");
        }
        static void TryUpgradeDebugLog() {
            if (!DebugEnabled) return;
            var richType = Type.GetType("OptionalRecruit.RecruitDebug");
            if (richType == null || !typeof(RecruitLog).IsAssignableFrom(richType)) return;
            try {
                DebugLog.Sink = (RecruitLog)Activator.CreateInstance(richType);
            } catch (Exception e) {
                log.LogWarning("optional debug logger present but failed to load: " + e.Message);
            }
        }
        void PatchOptional(Harmony harmony, string typeName) {
            var t = Type.GetType(typeName);
            if (t != null) PatchSafe(harmony, t);
        }
        void RunSafe(string label, Action action) {
            try {
                action();
            } catch (Exception e) {
                DebugLog.Error(label + " threw uncaught: " + e);
            }
        }
        void PatchSafe(Harmony harmony, Type t) {
            try {
                harmony.CreateClassProcessor(t).Patch();
                DebugLog.Info("Patched " + t.Name);
            } catch (Exception e) {
                DebugLog.Error("FAILED to patch " + t.Name + ": " + e);
            }
        }
        void VerifyRefs() {
            if (KrokRefs.patchType == null) DebugLog.Error("KrokoshaCasualtiesMP.PlayerCamera_ToggleTradeMenu_MultiplayerPatch not found - wrong KrokMP version?");
            if (KrokRefs.recruitButtonField == null) DebugLog.Error("RECRUITButton field not found on PlayerCamera_ToggleTradeMenu_MultiplayerPatch.");
            if (KrokRefs.eligibleMethod == null) DebugLog.Error("GetPlayersEligibleForRespawn method not found on PlayerCamera_ToggleTradeMenu_MultiplayerPatch.");
        }
    }
    internal class RecruitLog {
        public virtual void Info(string msg) { }
        public virtual void Verbose(string msg) { }
    }
    internal static class DebugLog {
        internal static ManualLogSource src;
        internal static RecruitLog Sink = new RecruitLog();
        internal static void Init(ManualLogSource s) {
            src = s;
        }
        internal static void Info(string msg) {
            Sink.Info(msg);
        }
        internal static void Verbose(string msg) {
            Sink.Verbose(msg);
        }
        internal static void Warn(string msg) {
            if (src != null) src.LogWarning(msg);
        }
        internal static void Error(string msg) {
            if (src != null) src.LogError(msg);
        }
    }
    [HarmonyPatch(typeof(ConsoleScript), "RegisterSpawnEntities")]
    internal static class Patch_ConsoleScript_RegisterSpawnEntities_Guard {
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) {
                DebugLog.Warn("Suppressed a pre-existing base-game exception in ConsoleScript.RegisterSpawnEntities (unrelated to this plugin, the \"spawn\" console command's SearchExact lookup returning null): " + __exception.Message);
            }
            return null;
        }
    }
}