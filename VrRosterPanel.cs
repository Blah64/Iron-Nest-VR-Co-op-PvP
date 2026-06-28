using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using TMPro;
using UnityEngine;
using Action = System.Action;   // disambiguate from Silk.NET.OpenXR.Action

namespace IronNestVR
{
    /// <summary>
    /// VR player-list window — a world-space panel that floats OFF TO THE SIDE of the settings menu while it's open
    /// on the Lobbies tab (the VR lobby browser), so the player list lives in its own window instead of being crammed
    /// into the menu rows. It is the VR counterpart to the flatscreen top-center panels:
    ///   • CO-OP  → a single crew list (everyone's on one team), like <see cref="CoopRoster"/>.
    ///   • PvP    → two team columns with team-switch, like <see cref="PvpTeams"/> (this is the VR teams view that
    ///              was previously flatscreen-only).
    /// The HOST can tap a name to kick, toggle the lobby Lock, and (PvP, pre-match) LAUNCH the match. Everything routes
    /// through the shared SteamNet / PvpTeams / PvpMatch model, so VR and flatscreen see and do the same things.
    ///
    /// It's a plain managed object (not a MonoBehaviour): it builds Unity primitives + 3D TextMeshPro and does its own
    /// laser raycast against its row colliders — the same technique as <see cref="VrSettingsMenu"/>, which it sits
    /// beside. The two panels never overlap (placed with a fixed gap), so the shared trigger only ever activates the
    /// panel the laser is actually on. Rebuilt only when the displayed state changes (a signature compare), so joins /
    /// leaves / team switches / lock all refresh without per-frame churn.
    /// </summary>
    internal sealed class VrRosterPanel
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Layout (metres at MenuScale = 1), matched to VrSettingsMenu so the two panels read as a pair.
        private const float ROW_H = 0.05f;
        private const float ROW_GAP = 0.006f;
        private const float PAD = 0.03f;
        private const float TITLE_H = 0.06f;
        private const float Z_ROW = -0.004f, Z_TEXT = -0.008f;
        private const float MENU_HALF = 0.31f;   // VrSettingsMenu half-width (PANEL_W+0.02)/2 — to clear it sideways

        private static readonly Color C_BG = new Color(0.05f, 0.06f, 0.08f, 1f);
        private static readonly Color C_ROW = new Color(0.13f, 0.15f, 0.19f, 1f);
        private static readonly Color C_ROW_HOVER = new Color(0.18f, 0.42f, 0.80f, 1f);
        private static readonly Color C_HEADER = new Color(0.20f, 0.24f, 0.30f, 1f);
        private static readonly Color C_LABEL = new Color(0.92f, 0.94f, 0.98f, 1f);
        private static readonly Color C_DIM = new Color(0.55f, 0.58f, 0.64f, 1f);
        private static readonly Color C_ME = new Color(0.45f, 0.95f, 0.55f, 1f);
        private static readonly Color C_JOIN = new Color(0.16f, 0.34f, 0.22f, 1f);
        private static readonly Color C_LOCK = new Color(0.50f, 0.45f, 0.12f, 1f);
        private static readonly Color C_LAUNCH = new Color(0.15f, 0.45f, 0.18f, 1f);

        private sealed class Item { public Material Mat; public Color Base; public int ColliderId; public Action OnClick; }

        private readonly List<Item> _items = new List<Item>();
        private readonly Dictionary<int, int> _idToItem = new Dictionary<int, int>();
        private GameObject _root;
        private TMP_FontAsset _font;
        private int _layer;
        private int _hover = -1;
        private bool _prevTrigger;
        private string _sig;
        // Anchor pose captured the first time the window appears; reused across rebuilds (a join/leave/switch
        // shouldn't yank the panel back in front of you). Re-anchored to the current head when it next opens.
        private Vector3 _anchorPos;
        private Quaternion _anchorRot;
        private bool _anchored;

        // scratch
        private readonly List<SteamNet.Member> _mem = new List<SteamNet.Member>();
        private readonly List<ulong> _ids = new List<ulong>();
        private readonly List<string> _names = new List<string>();
        private readonly StringBuilder _sb = new StringBuilder(256);

        public bool IsShown => _root != null;

        // ---------------- lifecycle ----------------

        public void Tick(VrInput input, CameraRig rig)
        {
            if (rig == null) return;
            try
            {
                string sig = Signature();
                if (_root == null || sig != _sig) { Build(rig, sig); _prevTrigger = true; } // swallow a trigger held over a rebuild
                if (_root == null) return;

                var origin = rig.OriginTransform;
                int best = -1; float bestDist = float.MaxValue;
                if (input.AimValid && origin != null)
                {
                    Posef p = input.AimPose;
                    var lp = new Vector3(p.Position.X, p.Position.Y, -p.Position.Z);
                    var lr = new Quaternion(-p.Orientation.X, -p.Orientation.Y, p.Orientation.Z, p.Orientation.W);
                    Vector3 o = origin.TransformPoint(lp);
                    Vector3 d = (origin.rotation * lr) * Vector3.forward;
                    var hits = Physics.RaycastAll(o, d, Config.LaserMaxDistance + 1f, ~0, QueryTriggerInteraction.Ignore);
                    if (hits != null)
                        for (int i = 0; i < hits.Length; i++)
                        {
                            var col = hits[i].collider; if (col == null) continue;
                            if (_idToItem.TryGetValue(col.GetInstanceID(), out int ii) && hits[i].distance < bestDist)
                            { bestDist = hits[i].distance; best = ii; }
                        }
                }

                SetHover(best);

                bool held = input.TriggerHeld;
                if (held && !_prevTrigger && best >= 0)
                {
                    try { _items[best].OnClick?.Invoke(); } catch (Exception e) { Log.LogWarning("[roster] click: " + e.Message); }
                    try { input.Haptic(Config.HapticAmplitude, Config.HapticSeconds); } catch { }
                }
                _prevTrigger = held;
            }
            catch (Exception e) { Log.LogWarning("[roster] tick: " + e.Message); Hide(); }
        }

        public void Hide() { DestroyRoot(); _sig = null; _prevTrigger = false; _anchored = false; }
        public void Dispose() { Hide(); }

        // ---------------- signature (rebuild only on real change) ----------------

        private string Signature()
        {
            _sb.Clear();
            bool host = false; try { host = CoopP2P.IsHost; } catch { }
            bool pvp = Config.PvpActive;
            _sb.Append(pvp ? "pvp;" : "coop;").Append(host ? 'H' : '-').Append(';')
               .Append(SteamNet.IsLocked ? 'L' : '-').Append(SteamNet.ManualLock ? 'm' : '-').Append(';');
            if (!pvp)
            {
                SteamNet.FillMembers(_mem);
                for (int i = 0; i < _mem.Count; i++)
                    _sb.Append(_mem[i].Id).Append(_mem[i].IsHost ? '*' : ' ').Append(_mem[i].IsMe ? '>' : ' ').Append(_mem[i].Name).Append('|');
            }
            else
            {
                _sb.Append("mt").Append(SafeMyTeam()).Append(';').Append(PvpLocked() ? 'K' : '-').Append(LaunchPending() ? 'P' : '-').Append(';');
                int teams = PvpTeams.TeamCount;
                for (int t = 0; t < teams; t++)
                {
                    PvpTeams.FillTeam(t, _ids, _names);
                    _sb.Append('T').Append(t).Append(':');
                    for (int s = 0; s < _ids.Count; s++) _sb.Append(_ids[s]).Append(_names[s]).Append(',');
                    _sb.Append('/');
                }
            }
            return _sb.ToString();
        }

        private static int SafeMyTeam() { try { return PvpTeams.MyTeam; } catch { return -1; } }
        private static bool PvpLocked() { try { return PvpTeams.LockedForMatch(); } catch { return false; } }
        private static bool LaunchPending() { try { return PvpMatch.LaunchPending; } catch { return false; } }

        // ---------------- build ----------------

        private void Build(CameraRig rig, string sig)
        {
            DestroyRoot();
            _layer = 0;
            EnsureFont();
            try { if (Config.PvpActive) BuildPvp(rig); else BuildCoop(rig); }
            catch (Exception e) { Log.LogWarning("[roster] build: " + e.Message); DestroyRoot(); return; }
            _sig = sig;
        }

        private void BuildCoop(CameraRig rig)
        {
            SteamNet.FillMembers(_mem);
            bool host = CoopP2P.IsHost;
            int memCount = _mem.Count;
            int lines = Mathf.Max(1, memCount) + (host ? 1 : 0);   // members (+ a lock row for the host)
            const float W = 0.46f;
            float contentH = PAD + TITLE_H + lines * (ROW_H + ROW_GAP) + PAD;
            PlaceRoot(rig, W, contentH);

            MakeQuad(_root.transform, Vector3.zero, new Vector3(W + 0.02f, contentH, 1f), C_BG, false);
            float top = contentH * 0.5f - PAD;
            string title = $"CO-OP CREW  ({memCount}/{SteamNet.MaxMembers})" + (SteamNet.IsLocked ? "   [LOCKED]" : "");
            MakeText(_root.transform, title, TextAlignmentOptions.Center, new Vector3(0f, top - TITLE_H * 0.5f, Z_TEXT),
                     new Vector2(W - 0.04f, TITLE_H * 0.8f), C_LABEL, true);

            float rowW = W - 0.04f;
            float y = top - TITLE_H;
            if (memCount == 0) { Cell(new Vector3(0f, y - ROW_H * 0.5f, 0f), rowW, ROW_H, "(connecting...)", TextAlignmentOptions.Center, C_ROW, C_DIM, null); y -= ROW_H + ROW_GAP; }
            for (int i = 0; i < memCount; i++)
            {
                ulong id = _mem[i].Id; bool me = _mem[i].IsMe; bool isHost = _mem[i].IsHost; string nm = _mem[i].Name;
                bool canKick = host && !me;
                string lbl = (me ? "> " : "  ") + nm + (isHost ? "  (host)" : "") + (canKick ? "   [kick]" : "");
                Cell(new Vector3(0f, y - ROW_H * 0.5f, 0f), rowW, ROW_H, lbl, TextAlignmentOptions.Left, C_ROW, me ? C_ME : C_LABEL,
                     canKick ? (Action)(() => SteamNet.Kick(id)) : null);
                y -= ROW_H + ROW_GAP;
            }
            if (host) { LockCell(new Vector3(0f, y - ROW_H * 0.5f, 0f), rowW); }
        }

        private void BuildPvp(CameraRig rig)
        {
            bool host = CoopP2P.IsHost;
            bool locked = PvpLocked();
            int slots = PvpTeams.SlotsPerTeam;
            int teams = PvpTeams.TeamCount;
            int colLines = 1 + slots;                       // header + slot rows
            bool showLaunch = host && !locked;
            int belowRows = (host ? 1 : 0) + (showLaunch ? 1 : 0);

            const float colW = 0.30f, colGap = 0.04f;
            float W = colW * teams + colGap * (teams - 1) + 0.06f;
            float contentH = PAD + TITLE_H + colLines * (ROW_H + ROW_GAP) + belowRows * (ROW_H + ROW_GAP) + PAD;
            PlaceRoot(rig, W, contentH);

            MakeQuad(_root.transform, Vector3.zero, new Vector3(W + 0.02f, contentH, 1f), C_BG, false);
            float top = contentH * 0.5f - PAD;
            string title = locked ? "PvP TEAMS  (match in progress)" : "PvP TEAMS  (tap an open slot to switch)";
            MakeText(_root.transform, title, TextAlignmentOptions.Center, new Vector3(0f, top - TITLE_H * 0.5f, Z_TEXT),
                     new Vector2(W - 0.04f, TITLE_H * 0.8f), C_LABEL, true);

            float colTop = top - TITLE_H;
            // Two (or more) columns laid out symmetrically about centre.
            float span = colW * teams + colGap * (teams - 1);
            float startX = -span * 0.5f + colW * 0.5f;
            for (int t = 0; t < teams; t++)
                BuildTeamColumn(t, startX + t * (colW + colGap), colTop, colW, slots, host, locked);

            float rowW = W - 0.04f;
            float y = colTop - colLines * (ROW_H + ROW_GAP);
            if (host) { LockCell(new Vector3(0f, y - ROW_H * 0.5f, 0f), rowW); y -= ROW_H + ROW_GAP; }
            if (showLaunch)
            {
                bool enabled = PvpTeams.CountTeam(0) >= 1 && PvpTeams.CountTeam(1) >= 1;
                bool pending = LaunchPending();
                Color col = pending ? C_LOCK : (enabled ? C_LAUNCH : C_ROW);
                string lt = pending ? "LAUNCHING..." : (enabled ? "LAUNCH MATCH" : "LAUNCH  (need 1+ each)");
                Cell(new Vector3(0f, y - ROW_H * 0.5f, 0f), rowW, ROW_H, lt, TextAlignmentOptions.Center, col, C_LABEL,
                     (enabled && !pending) ? (Action)(() => { try { PvpMatch.LaunchArena(); } catch { } }) : null);
            }
        }

        private void BuildTeamColumn(int team, float cx, float yTop, float colW, int slots, bool host, bool locked)
        {
            PvpTeams.FillTeam(team, _ids, _names);
            int cnt = _ids.Count;
            Cell(new Vector3(cx, yTop - ROW_H * 0.5f, 0f), colW, ROW_H, $"TEAM {team + 1}  ({cnt}/{slots})",
                 TextAlignmentOptions.Center, C_HEADER, C_LABEL, null);
            float ry = yTop - (ROW_H + ROW_GAP);
            int myTeam = SafeMyTeam();
            for (int s = 0; s < slots; s++)
            {
                var center = new Vector3(cx, ry - ROW_H * 0.5f, 0f);
                if (s < cnt)
                {
                    ulong id = _ids[s]; string nm = _names[s]; bool me = id == CoopP2P.MyId;
                    bool canKick = host && !me && !locked;
                    string lbl = (me ? "> " : "  ") + nm + (canKick ? "  [kick]" : "");
                    Cell(center, colW, ROW_H, lbl, TextAlignmentOptions.Left, C_ROW, me ? C_ME : C_LABEL,
                         canKick ? (Action)(() => SteamNet.Kick(id)) : null);
                }
                else
                {
                    bool canJoin = !locked && team != myTeam;   // only the OTHER team's open slot is joinable
                    int capTeam = team;
                    Cell(center, colW, ROW_H, canJoin ? "[ join ]" : "[ empty ]", TextAlignmentOptions.Center,
                         canJoin ? C_JOIN : C_ROW, canJoin ? C_LABEL : C_DIM,
                         canJoin ? (Action)(() => PvpTeams.RequestSwitch(capTeam)) : null);
                }
                ry -= ROW_H + ROW_GAP;
            }
        }

        private void LockCell(Vector3 center, float w)
        {
            string ll = !SteamNet.IsLocked ? "Lock lobby  (block new players)"
                      : SteamNet.ManualLock ? "Locked - tap to unlock"
                      : "Locked (mission in progress)";
            Cell(center, w, ROW_H, ll, TextAlignmentOptions.Center, SteamNet.IsLocked ? C_LOCK : C_ROW, C_LABEL,
                 () => SteamNet.ToggleLock());
        }

        // A row: background quad (+ collider when interactive) and a label. onClick != null => hover + click.
        private void Cell(Vector3 center, float w, float h, string label, TextAlignmentOptions align, Color rowColor, Color textColor, Action onClick)
        {
            var go = MakeQuad(_root.transform, center + new Vector3(0f, 0f, Z_ROW), new Vector3(w, h, 1f), rowColor, onClick != null);
            if (onClick != null)
            {
                var item = new Item { OnClick = onClick, Base = rowColor };
                var mr = go.GetComponent<MeshRenderer>(); item.Mat = mr != null ? mr.material : null;
                var mc = go.GetComponent<MeshCollider>(); item.ColliderId = mc != null ? mc.GetInstanceID() : go.GetInstanceID();
                _idToItem[item.ColliderId] = _items.Count; _items.Add(item);
            }
            float inset = align == TextAlignmentOptions.Left ? 0.012f : 0f;
            MakeText(_root.transform, label, align, center + new Vector3(inset, 0f, Z_TEXT),
                     new Vector2(w - 0.018f, h * 0.78f), textColor, false);
        }

        private void SetHover(int idx)
        {
            if (idx == _hover) return;
            if (_hover >= 0 && _hover < _items.Count) SetMat(_items[_hover].Mat, _items[_hover].Base);
            if (idx >= 0 && idx < _items.Count) SetMat(_items[idx].Mat, C_ROW_HOVER);
            _hover = idx;
        }

        // ---------------- placement + primitives (mirrors VrSettingsMenu) ----------------

        private void PlaceRoot(CameraRig rig, float W, float contentH)
        {
            float scale = Mathf.Max(0.2f, Config.MenuScale);
            if (!_anchored)
            {
                rig.TryGetHeadPose(out var hp, out var hr);
                if (!rig.TryGetHeadBasis(out var fwd, out var right))
                {
                    fwd = hr * Vector3.forward; fwd.y = 0f;
                    fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                    right = Vector3.Cross(Vector3.up, fwd).normalized;
                }
                float side = (MENU_HALF + 0.05f + W * 0.5f) * scale;   // clear the menu sideways with a fixed gap
                _anchorPos = hp + fwd * Config.MenuDistance + right * side + Vector3.up * Config.MenuHeightOffset;
                _anchorRot = Quaternion.LookRotation((_anchorPos - hp).normalized, Vector3.up);
                if (Config.MenuFlip) _anchorRot *= Quaternion.Euler(0f, 180f, 0f);
                _anchored = true;
            }

            _root = new GameObject("IronNestVR_RosterPanel");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _root.transform.SetPositionAndRotation(_anchorPos, _anchorRot);
            _root.transform.localScale = Vector3.one * scale;
            _root.layer = _layer;
            _items.Clear(); _idToItem.Clear(); _hover = -1;
        }

        private GameObject MakeQuad(Transform parent, Vector3 localPos, Vector3 scale, Color color, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "roster_quad";
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;
            var mc = go.GetComponent<MeshCollider>();
            if (!collider && mc != null) UnityEngine.Object.Destroy(mc);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.material = MakeMat(color);
            return go;
        }

        private TextMeshPro MakeText(Transform parent, string s, TextAlignmentOptions align, Vector3 localPos, Vector2 size, Color color, bool bold)
        {
            var go = new GameObject("roster_text");
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            var tmp = go.AddComponent<TextMeshPro>();
            if (_font != null) tmp.font = _font;
            tmp.alignment = align;
            tmp.color = color;
            tmp.richText = false;
            tmp.enableWordWrapping = false;
            if (bold) tmp.fontStyle = FontStyles.Bold;
            tmp.rectTransform.sizeDelta = size;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.01f;
            tmp.fontSizeMax = 300f;
            tmp.text = s;
            tmp.ForceMeshUpdate(false, false);
            return tmp;
        }

        private static Material MakeMat(Color color)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var m = new Material(sh);
            SetMat(m, color);
            try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { }
            return m;
        }

        private static void SetMat(Material m, Color c)
        {
            if (m == null) return;
            try { m.color = c; } catch { }
            try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
            try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
        }

        private void EnsureFont()
        {
            if (_font != null) return;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None);
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var t = arr[i].TryCast<TMP_Text>();
                        if (t != null && t.font != null) { _font = t.font; break; }
                    }
            }
            catch { }
            if (_font == null) { try { _font = TMP_Settings.defaultFontAsset; } catch { } }
        }

        private void DestroyRoot()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _items.Clear();
            _idToItem.Clear();
            _hover = -1;
        }
    }
}
