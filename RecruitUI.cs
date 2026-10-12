using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Together;

namespace OptionalRecruit {
    internal static class RecruitUI {
        const string HeaderText = "Target a player to recruit:";
        const float HeaderHeight = 50f;
        const float RowHeight = 120f;
        static readonly Dictionary<Texture2D, Sprite> spriteCache = new Dictionary<Texture2D, Sprite>();
        static bool collapsed = false;
        static bool buildErrorLogged = false;
        static ushort chosenKnetId = ushort.MaxValue;
        static float lastCommandTime = -1000f;
        internal static void Build(PlayerCamera cam) {
            try {
                if (cam == null || cam.tradeMenu == null || !cam.tradeMenu.activeSelf || cam.currentTrader == null || cam.traderInventory == null) return;
                var btn = KrokRefs.recruitButtonField == null ? null : KrokRefs.recruitButtonField.GetValue(null) as GameObject;
                if (btn == null || !btn.activeSelf) return;
                var players = GetPlayers();
                if (players.Count == 0) return;
                bool found = false;
                foreach (var p in players) {
                    if (p.playerId.id == chosenKnetId) found = true;
                }
                float total = HeaderHeight + (collapsed ? 0f : (players.Count + 1) * RowHeight);
                Transform inv = cam.traderInventory;
                foreach (Transform child in inv) {
                    var childRt = child as RectTransform;
                    if (childRt != null) childRt.anchoredPosition += new Vector2(0f, -total);
                }
                AddHeader(inv);
                float y = HeaderHeight;
                if (!collapsed) {
                    AddRow(inv, y, null, !found);
                    y += RowHeight;
                    foreach (var p in players) {
                        AddRow(inv, y, p, found && p.playerId.id == chosenKnetId);
                        y += RowHeight;
                    }
                }
                var invRt = inv.GetComponent<RectTransform>();
                invRt.sizeDelta = new Vector2(invRt.sizeDelta.x, invRt.sizeDelta.y + total);
            } catch (Exception e) {
                if (buildErrorLogged) return;
                buildErrorLogged = true;
                DebugLog.Error("Recruit tab build failed (further failures not logged): " + e);
            }
        }
        static List<ScavPlayer> GetPlayers() {
            var result = new List<ScavPlayer>();
            var list = KrokRefs.GetEligible();
            if (list == null) return result;
            foreach (var item in list) {
                var p = item as ScavPlayer;
                if (p != null) result.Add(p);
            }
            return result;
        }
        static void AddHeader(Transform inv) {
            var go = Utils.Create("Special/TraderInvSplit", inv);
            go.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
            var label = go.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = HeaderText;
            var tip = go.GetComponent<UITooltip>();
            if (tip != null) {
                tip.skipLocale = true;
                tip.tipName = "Recruit target";
                tip.tipDesc = "Choose which dead player the Recruit button respawns.";
            }
            var arrow = go.transform.GetChild(1);
            arrow.localEulerAngles = new Vector3(0f, 0f, collapsed ? -90f : 0f);
            var arrowBtn = arrow.GetComponent<Button>();
            if (arrowBtn != null) arrowBtn.onClick.AddListener(delegate { ToggleCollapsed(); });
        }
        static void AddRow(Transform inv, float y, ScavPlayer plr, bool selected) {
            var row = Utils.Create("Special/TraderItemPanel", inv);
            if (row.transform.childCount < 8) {
                DebugLog.Warn("TraderItemPanel has " + row.transform.childCount + " children, expected 8; row skipped.");
                UnityEngine.Object.Destroy(row);
                return;
            }
            row.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -y);
            ushort id = plr == null ? ushort.MaxValue : plr.playerId.id;
            Color plrColor = Color.white;
            if (plr != null) plrColor = plr.playerColor;
            string name = plr == null ? "Automatic" : id + ": " + plr.playerName;
            string desc = plr == null ? "Let the Host pick the most suitable dead player." : "Respawn this player when Recruit is pressed.";
            var fill = row.transform.GetChild(0).GetComponent<Image>();
            if (fill != null) fill.enabled = false;
            var icon = row.transform.GetChild(1).GetComponent<Image>();
            Texture2D tex = plr == null ? null : plr.IconAnySmallToLarge;
            if (icon != null) {
                if (tex != null) {
                    icon.enabled = true;
                    icon.sprite = SpriteFor(tex);
                    icon.color = Color.white;
                    icon.GetComponent<RectTransform>().sizeDelta = PlayerCamera.ImageSizeDelta(tex, 8f, 64f);
                } else {
                    icon.enabled = false;
                }
            }
            SetText(row.transform.GetChild(2), name, plrColor);
            SetText(row.transform.GetChild(3), desc, null);
            var tip = row.transform.GetChild(3).GetComponent<UITooltip>();
            if (tip != null) {
                tip.skipLocale = true;
                tip.tipName = name;
                tip.tipDesc = desc;
            }
            SetText(row.transform.GetChild(4), selected ? "Selected" : "", null);
            SetText(row.transform.GetChild(5), "", null);
            var buy = row.transform.GetChild(6).GetComponent<Button>();
            if (buy != null) {
                buy.onClick.AddListener(delegate { Select(id); });
                var buyLabel = buy.GetComponentInChildren<TextMeshProUGUI>(true);
                if (buyLabel != null) buyLabel.text = selected ? "Selected" : "Target";
            }
            var strip = row.transform.GetChild(7).GetComponent<Image>();
            if (strip != null) strip.color = selected ? Color.green : (plr == null ? Color.gray : plrColor);
        }
        static TextMeshProUGUI SetText(Transform t, string text, Color? color) {
            var tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return null;
            tmp.richText = false;
            tmp.text = text;
            if (color.HasValue) tmp.color = color.Value;
            return tmp;
        }
        static Sprite SpriteFor(Texture2D tex) {
            Sprite s;
            if (spriteCache.TryGetValue(tex, out s) && s != null) return s;
            if (spriteCache.Count > 64) spriteCache.Clear();
            s = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            spriteCache[tex] = s;
            return s;
        }
        static void ToggleCollapsed() {
            collapsed = !collapsed;
            Rebuild();
        }
        static void Rebuild() {
            var cam = PlayerCamera.main;
            if (cam != null && cam.tradeMenu != null && cam.tradeMenu.activeSelf) cam.RefreshTraderInventories();
        }
        static void Select(ushort id) {
            DebugLog.Info("Recruit tab clicked: player ID " + (id == ushort.MaxValue ? "auto" : id.ToString()));
            chosenKnetId = id;
            SendArm(RecruitArm.UiArmName, id);
            Rebuild();
        }
        static bool CooldownReady(out float remaining) {
            remaining = (float)(RecruitArm.CommandCooldownSeconds + 0.5) - (Time.realtimeSinceStartup - lastCommandTime);
            return remaining <= 0f;
        }
        internal static void SetTarget(ushort knetId) {
            float wait;
            if (!CooldownReady(out wait)) {
                DebugLog.Warn("Recruit command on cooldown, " + Mathf.CeilToInt(wait) + "s left.");
                return;
            }
            lastCommandTime = Time.realtimeSinceStartup;
            chosenKnetId = knetId;
            SendArm(RecruitArm.ArmName, knetId);
            Rebuild();
        }
        static void SendArm(string name, ushort knetId) {
            try {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(name);
                writer.Put(knetId);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            } catch (Exception e) {
                DebugLog.Error("SendArm failed: " + e);
            }
        }
    }
}