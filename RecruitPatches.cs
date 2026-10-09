using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using KrokoshaCasualtiesMP;

namespace OptionalRecruit {
    internal static class KrokRefs {
        internal static readonly System.Type patchType = AccessTools.TypeByName("KrokoshaCasualtiesMP.PlayerCamera_ToggleTradeMenu_MultiplayerPatch");
        internal static readonly FieldInfo recruitButtonField = AccessTools.Field(patchType, "RECRUITButton");
        internal static readonly MethodInfo eligibleMethod = AccessTools.Method(patchType, "GetPlayersEligibleForRespawn");
        internal static IList GetEligible() {
            if (eligibleMethod == null) return null;
            return eligibleMethod.Invoke(null, null) as IList;
        }
        internal static string PlayerName(object netPlayer) {
            if (netPlayer == null) return "?";
            var f = AccessTools.Field(netPlayer.GetType(), "playername");
            if (f != null) return f.GetValue(netPlayer) as string ?? "?";
            return netPlayer.ToString();
        }
        internal static ushort PlayerKnetId(object netPlayer) {
            if (netPlayer == null) return ushort.MaxValue;
            var p = netPlayer as NetPlayer;
            if (p != null) return p.clientId.id;
            return ushort.MaxValue;
        }
    }
    internal static class RecruitArm {
        internal const ushort ArmMsgId = 60173;
        internal const ushort UiArmMsgId = 60174;
        internal const double CommandCooldownSeconds = 10.0;
        internal const ushort None = ushort.MaxValue;
        internal const ushort HostRequesterId = 0;
        internal static ConfigEntry<bool> ApplyHostTargetToAll;
        internal static ushort activeRequester = None;
        static readonly Dictionary<ushort, ushort> arms = new Dictionary<ushort, ushort>();
        static readonly Dictionary<ushort, DateTime> lastCommand = new Dictionary<ushort, DateTime>();
        internal static bool TryBeginCommand(ushort requester, out double remaining) {
            remaining = 0.0;
            DateTime last;
            if (lastCommand.TryGetValue(requester, out last)) {
                double elapsed = (DateTime.UtcNow - last).TotalSeconds;
                if (elapsed < CommandCooldownSeconds) {
                    remaining = CommandCooldownSeconds - elapsed;
                    return false;
                }
            }
            lastCommand[requester] = DateTime.UtcNow;
            return true;
        }
        internal static void Set(ushort requester, ushort target) {
            arms[requester] = target;
        }
        internal static void Clear(ushort requester) {
            arms[requester] = None;
        }
        internal static bool ResolveTarget(ushort requester, out ushort target, out string source) {
            if (arms.TryGetValue(requester, out target)) {
                source = "own";
                return target != None;
            }
            if (ApplyHostTargetToAll != null && ApplyHostTargetToAll.Value && requester != HostRequesterId && arms.TryGetValue(HostRequesterId, out target) && target != None) {
                source = "host default";
                return true;
            }
            target = None;
            source = "auto";
            return false;
        }
        internal static void RegisterReceiver() {
            try {
                ServerMain.RegisterServerReceiver(ArmMsgId, OnArmReceived);
                ServerMain.RegisterServerReceiver(UiArmMsgId, OnUiArmReceived);
                DebugLog.Info("Registered custom netmsg " + ArmMsgId + " (console/command, cooldown) and " + UiArmMsgId + " (UI, no cooldown).");
            } catch (Exception e) {
                DebugLog.Error("Failed to register custom netmsgs " + ArmMsgId + "/" + UiArmMsgId + ": " + e);
            }
        }
        static void Apply(ushort requester, ushort kid, string source) {
            if (kid == None) {
                Clear(requester);
                DebugLog.Info("Recruit target cleared for requester " + requester + " (" + source + ")");
            } else {
                Set(requester, kid);
                DebugLog.Info("Recruit target armed: player ID " + kid + " for requester " + requester + " (" + source + ")");
            }
        }
        static void OnArmReceived(knetid clientId, ref NetDataReader reader) {
            try {
                ushort kid = reader.GetUShort();
                double remaining;
                if (!TryBeginCommand(clientId.id, out remaining)) {
                    DebugLog.Info("Recruit arm from requester " + clientId.id + " ignored, cooldown " + remaining.ToString("0.0") + "s left.");
                    return;
                }
                Apply(clientId.id, kid, "command");
            } catch (Exception e) {
                DebugLog.Error("OnArmReceived failed: " + e);
            }
        }
        static void OnUiArmReceived(knetid clientId, ref NetDataReader reader) {
            try {
                ushort kid = reader.GetUShort();
                Apply(clientId.id, kid, "UI");
            } catch (Exception e) {
                DebugLog.Error("OnUiArmReceived failed: " + e);
            }
        }
    }
    internal static class ChatRecruit {
        internal static bool TryParseRecruitCommand(string message, out ushort targetKnetId) {
            targetKnetId = ushort.MaxValue;
            if (string.IsNullOrEmpty(message)) return false;
            string[] parts = message.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !string.Equals(parts[0], "recruit", StringComparison.OrdinalIgnoreCase)) return false;
            if (parts.Length < 2) return true;
            ushort id;
            if (!ushort.TryParse(parts[1], out id)) return false;
            targetKnetId = id;
            return true;
        }
        internal static void SendPrivateReply(knetid target, string message) {
            try {
                NetPlayer player;
                if (NetPlayer.TryGetPlayerFromClientId(target, out player) && player != null) {
                    player.Server_DoAlertSingle(message, false, true);
                } else {
                    DebugLog.Warn("SendPrivateReply: could not resolve NetPlayer for client " + target);
                }
            } catch (Exception e) {
                DebugLog.Error("SendPrivateReply failed: " + e);
            }
        }
    }
    [HarmonyPatch]
    internal static class Patch_ServerChatMessage {
        static MethodBase TargetMethod() {
            var m = AccessTools.Method(AccessTools.TypeByName("KrokoshaCasualtiesMP.Chat"), "Server_PlayerChatMessageSend");
            if (m == null) DebugLog.Error("TargetMethod not found: Chat.Server_PlayerChatMessageSend");
            return m;
        }
        static bool Prefix(knetid clientId, ref NetDataReader reader) {
            try {
                int len = reader.AvailableBytes;
                byte[] copy = new byte[len];
                Array.Copy(reader.RawData, reader.Position, copy, 0, len);
                string msg = null;
                try {
                    var peek = new NetDataReader(copy);
                    bool discard;
                    peek.Get(out discard);
                    msg = peek.GetString();
                } catch (Exception e) {
                    DebugLog.Warn("Chat peek parse failed: " + e.Message);
                }
                if (msg == null) return true;
                ushort targetKnetId;
                if (!ChatRecruit.TryParseRecruitCommand(msg, out targetKnetId)) {
                    DebugLog.Verbose("Chat (not a command): \"" + msg + "\"");
                    return true;
                }
                double remaining;
                if (!RecruitArm.TryBeginCommand(clientId.id, out remaining)) {
                    ChatRecruit.SendPrivateReply(clientId, "Recruit command on cooldown, " + Math.Ceiling(remaining) + "s left.");
                    DebugLog.Info("Chat recruit command from " + clientId.id + " blocked by cooldown.");
                    return false;
                }
                if (targetKnetId == RecruitArm.None) {
                    RecruitArm.Clear(clientId.id);
                    ChatRecruit.SendPrivateReply(clientId, "Recruit target cleared, will auto-select.");
                    DebugLog.Info("Chat recruit command from " + clientId.id + " -> cleared.");
                } else {
                    RecruitArm.Set(clientId.id, targetKnetId);
                    ChatRecruit.SendPrivateReply(clientId, "Recruit target set to player ID " + targetKnetId + ".");
                    DebugLog.Info("Chat recruit command from " + clientId.id + " -> player ID " + targetKnetId);
                }
                return false;
            } catch (Exception e) {
                DebugLog.Error("Patch_ServerChatMessage failed: " + e);
                return true;
            }
        }
    }
    [HarmonyPatch]
    internal static class Patch_Server_SelectMostSuitablePlayerToRespawn {
        static MethodBase TargetMethod() {
            var m = AccessTools.Method(KrokRefs.patchType, "Server_SelectMostSuitablePlayerToRespawn");
            if (m == null) DebugLog.Error("TargetMethod not found: Server_SelectMostSuitablePlayerToRespawn");
            return m;
        }
        static bool Prefix(ref object __result) {
            ushort requester = RecruitArm.activeRequester;
            if (requester == RecruitArm.None) return true;
            ushort kid;
            string source;
            if (!RecruitArm.ResolveTarget(requester, out kid, out source)) return true;
            var list = KrokRefs.GetEligible();
            if (list != null) {
                foreach (var item in list) {
                    if (KrokRefs.PlayerKnetId(item) == kid) {
                        __result = item;
                        DebugLog.Info("Recruit override (requester " + requester + ", " + source + " target player ID " + kid + ") -> " + KrokRefs.PlayerName(__result));
                        return false;
                    }
                }
            }
            DebugLog.Warn("Target player ID " + kid + " (" + source + ", requester " + requester + ") not found among eligible players, falling back to auto-select.");
            return true;
        }
    }
    [HarmonyPatch]
    internal static class Patch_ServerReceiver_RecruitPressed_Scope {
        static MethodBase TargetMethod() {
            var m = AccessTools.Method(KrokRefs.patchType, "ServerReceiver_RecruitPressed");
            if (m == null) DebugLog.Error("TargetMethod not found: ServerReceiver_RecruitPressed");
            return m;
        }
        static void Prefix(knetid clientId) {
            RecruitArm.activeRequester = clientId.id;
            ushort armed;
            string source;
            bool has = RecruitArm.ResolveTarget(clientId.id, out armed, out source);
            DebugLog.Info("Recruit click from requester " + clientId.id + ", target: " + (has ? armed.ToString() + " (" + source + ")" : "auto"));
        }
        static void Finalizer() {
            RecruitArm.activeRequester = RecruitArm.None;
        }
    }
    [HarmonyPatch(typeof(PlayerCamera), "RefreshTraderInventories")]
    internal static class Patch_RefreshTraderInventories {
        static void Postfix(PlayerCamera __instance) {
            RecruitUI.Build(__instance);
        }
    }
}