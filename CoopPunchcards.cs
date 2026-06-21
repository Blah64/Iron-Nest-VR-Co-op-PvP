using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op: replicate the REQUISITION PUNCHCARDS (the loadout/ability cards inserted into the
    /// <c>RequisitionSlot</c>) — NOT to be confused with <see cref="CoopCards"/>, which mirrors the printed
    /// FireMissionCard firing-solution slip.
    ///
    /// THE PROBLEM (tester 2026-06-21): "punch cards do not match between players, and then results from using a
    /// card do not match." Two independent root causes:
    ///   (A) DECK MISMATCH. Each machine builds its punchcard deck from its OWN local save:
    ///       <c>MissionManager.LoadOperationState</c> maps the save's <c>OperationState.CardStates</c>
    ///       ({CardID -> RemainingUses}) to <c>PunchcardDefinitionV2</c>s, and <c>RequisitionConsoleManager</c>
    ///       spawns those as the physical cards. Different unlocks / remaining-uses -> different cards on the table.
    ///   (B) REDEMPTION DIVERGES. Inserting a card + pulling the lever runs <c>RequisitionSlot.AttemptRequisition()</c>,
    ///       which spends requisition points and runs the card's <c>PunchcardGraph</c> LOCALLY on the acting machine
    ///       only. The peer sees nothing; spawn-effects are even killed on the client by the narrow spawn-gate
    ///       (<c>State_SpawnMapEntity.OnEnter</c>), and any in-graph RNG / point-spend / use-decrement resolves
    ///       independently per machine.
    ///
    /// THE FIX (host-authoritative, per user direction 2026-06-21):
    ///   (A) DECK = HOST-AUTHORITATIVE. The host polls its own deck (<c>GetAllCards()</c> -> [{ID, RemainingUses}])
    ///       and broadcasts it; the client resolves each ID against <c>AllDefinitions</c> and <c>RebuildDeck</c>s to
    ///       match (overriding its own save-derived deck). Rides the join-in-progress snapshot.
    ///   (B) REDEMPTION = HOST RUNS THE GRAPH. A Harmony prefix on <c>AttemptRequisition</c> makes a CLIENT capture
    ///       the card ID + the player-chosen <c>PunchcardVariable</c> values, send a redeem INTENT to the host, then
    ///       consume its own physical card and SKIP the local run. The host applies those variables to its own scene
    ///       <c>PunchcardVariable</c>s, places the matching deck card in its slot and runs the REAL
    ///       <c>AttemptRequisition()</c> — so the graph executes once, authoritatively, and its effects replicate
    ///       through the existing entity / impact / score wiring. A host's own redemption just runs locally; the
    ///       resulting deck change re-broadcasts so the client's deck stays in lock-step.
    ///
    /// LIMITATIONS (v1, noted): the host's slot visibly redeems a card for a client-initiated use (host executes it);
    /// mid-mission the client's requisition-point counter may read high until CoopScore reconciles it out-of-mission
    /// (same finalize-on-return model CoopScore already documents); near-simultaneous redemptions on the host slot are
    /// processed in arrival order. Variables are copied by direct field-set (the graph reads them via Get()).
    /// </summary>
    internal static class CoopPunchcards
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_PUNCH_DECK   = 31;  // host->client: [t][count i32]( [id str][remainingUses i32] )*               reliable
        public const byte MSG_PUNCH_REDEEM = 32;  // client->host: [t][cardId str][varCount i32]( var )*                         reliable
        // var = [varId str][type i32][int i32][float f32][bool u8][gridLoc i32][gridX i32][gridY i32][shellSlot i32][text str]

        // --- PHYSICAL CARD MOVEMENT (the "cards don't match / don't move together" fix) ---
        // Mirrors the CoopMap token mechanism (grab→own→stream→place) but keys each card by Fnv(CurrentDefinition.ID)
        // instead of transform path — the cards are runtime-spawned CLONES with identical names, so a path hash
        // collides (that was the cross-wiring CoopMap exhibited). Positions are console-relative (anchored to the
        // RequisitionConsoleManager transform) so they survive the card reparenting that a drag can cause.
        public const byte MSG_PUNCH_GRAB    = 33;   // either->peer: [t][cardKey i32]                              reliable   claim a card
        public const byte MSG_PUNCH_POS     = 34;   // owner->peer:  [t][cardKey i32][pos 3f][rot 4f]              unreliable stream while held
        public const byte MSG_PUNCH_PLACE   = 35;   // owner->peer:  [t][cardKey i32][pos 3f][rot 4f][inReqSlot u8] reliable  final drop (+ slot state)
        public const byte MSG_PUNCH_CONSUME = 36;   // host->client: [t][cardId str]                              reliable   authoritative "this card was redeemed — drop yours"

        private const int GridNull = int.MinValue;   // sentinel: VariableCoordinate was null
        private const float StaleSec = 2f;            // remote ownership lease (matches CoopMap)

        // echo guard: true while WE apply a host-driven deck / run a host-side redemption, so our own polling/hooks
        // don't treat it as a fresh local action.
        private static bool _applyingRemote;
        // true while a CLIENT applies a host MSG_PUNCH_CONSUME (so the resulting OnCardUsed doesn't re-broadcast).
        private static bool _consumingMirror;

        private static string _lastDeckSig;          // host: last-broadcast deck signature (re-send only on change)
        private static float _nextPoll;
        private static List<DeckEntry> _pendingDeck;  // client: a host deck received before our console was ready
        private static int _sentDeck, _appliedDeck, _sentRedeem, _ranRedeem, _sentConsume, _ranConsume;

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        private static readonly byte[] _scratch = new byte[512];

        private struct DeckEntry { public string Id; public int Uses; }

        private struct VarSnap
        {
            public string Id; public int Type;
            public int I; public float F; public bool B;
            public int GridLoc, GridX, GridY; public int Shell; public string Text;
        }

        // --- physical card movement registry (keyed by Fnv(CurrentDefinition.ID)) ---
        private sealed class Card
        {
            public int Key;
            public PunchcardRuntime Rt;
            public DraggableItem Token;
            public Transform T;
            public bool LocalOwned;
            public bool PrevDragging;
            public ulong RemoteOwner;        // SteamID dragging this card on the peer (0 = none)
            public float RemoteUntil;
            public bool HasRemotePos;
            public Vector3 RemoteLocal;      // console-relative target position
            public Quaternion RemoteRot;     // console-relative target rotation
        }

        private static readonly Dictionary<int, Card> _cards = new Dictionary<int, Card>();
        private static float _nextCardScan, _nextCardSend;
        private static int _dupWarned;

        // PLACE packets that arrived before our console / card registry was ready (e.g. join mid mission-load).
        // Drained in Tick once EnsureCards resolves the matching card. (POS is dropped — only finals are stashed.)
        private struct PendPose { public Vector3 P; public Quaternion R; public bool InSlot; }
        private static readonly Dictionary<int, PendPose> _pendingPose = new Dictionary<int, PendPose>();
        private static readonly List<int> _drainKeys = new List<int>();

        // ================= per-frame =================

        public static void Tick(float dt)
        {
            if (!Config.CoopPunchcardSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer)
            {
                _lastDeckSig = null; _pendingDeck = null;
                if (_cards.Count > 0) { ClearAllOwnership(); _cards.Clear(); }
                if (_pendingPose.Count > 0) _pendingPose.Clear();
                return;
            }

            EnsureCards();
            DrainPendingPoses();   // apply any PLACEs that arrived before their card registered
            TickMovement();        // grab / own / stream / release — shared pool, either player may drive a card

            // Deck-composition (uses) overlay — dormant unless CoopPunchcardDeckSync (the catalog already matches;
            // uses only diverge through redemption, which is deferred). Kept wired for the redemption layer.
            if (Config.CoopPunchcardDeckSync)
            {
                if (CoopP2P.IsHost) HostPollDeck();
                else if (_pendingDeck != null && ConsoleReady()) { var d = _pendingDeck; _pendingDeck = null; ApplyDeck(d); }
            }
        }

        // ================= physical card movement (def-ID keyed; mirrors CoopMap tokens) =================

        // Build / refresh the card registry from the console's own card list. Keyed by Fnv(definition ID) so a
        // dup-named clone can't collide (the CoopMap path-hash bug). Re-binds dead entries after a scene reload.
        private static void EnsureCards(bool force = false)
        {
            if (!force && _cards.Count > 0 && Time.unscaledTime < _nextCardScan) return;
            _nextCardScan = Time.unscaledTime + 3f;

            var mgr = Mgr(); if (mgr == null) return;
            var cards = mgr.GetAllCards(); if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
            {
                var c = cards[i]; if (c == null) continue;
                PunchcardDefinitionV2 def = null; try { def = c.CurrentDefinition; } catch { }
                if (def == null) continue;
                string id = null; try { id = def.ID; } catch { }
                if (string.IsNullOrEmpty(id)) continue;
                DraggableItem di = null; try { di = c.DraggableItem; } catch { }
                if (di == null) continue;
                Transform t = null; try { t = di.transform; } catch { }
                if (t == null) continue;

                int key = Fnv(id);
                if (_cards.TryGetValue(key, out var ex))
                {
                    if (ex.T == null || ex.Rt == null || ex.Rt == c)
                    { ex.Rt = c; ex.Token = di; ex.T = t; }   // re-bind a dead/same entry to the live object
                    else if (_dupWarned < 4)
                    { _dupWarned++; Log.LogWarning($"[punch] duplicate card def ID '{id}' — only the first is synced"); }
                    continue;
                }
                _cards[key] = new Card { Key = key, Rt = c, Token = di, T = t, RemoteRot = Quaternion.identity };
            }
        }

        private static void TickMovement()
        {
            var anchor = ConsoleTransform(); if (anchor == null) return;
            float now = Time.unscaledTime;
            bool sendNow = Config.CoopSendHz <= 0f || now >= _nextCardSend;
            if (sendNow && Config.CoopSendHz > 0f) _nextCardSend = now + 1f / Config.CoopSendHz;

            foreach (var c in _cards.Values)
            {
                if (c.Token == null || c.T == null) continue;
                bool dragging = false; try { dragging = c.Token.IsBeingDragged; } catch { }

                if (dragging && !c.PrevDragging)
                {
                    if (!(c.RemoteOwner != 0 && now < c.RemoteUntil)) { c.LocalOwned = true; SendGrab(c.Key); }
                }
                else if (!dragging && c.PrevDragging && c.LocalOwned)
                {
                    c.LocalOwned = false; SendPlace(c, anchor);
                }
                c.PrevDragging = dragging;

                if (c.LocalOwned && sendNow) SendPos(c, anchor);
                if (c.RemoteOwner != 0 && now >= c.RemoteUntil) { c.RemoteOwner = 0; ClearExternal(c); }
            }
        }

        // Apply remote-owned card poses after the game's Update so our placement wins the frame (mirrors CoopMap).
        public static void LateApply()
        {
            if (!Config.CoopPunchcardSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
            var anchor = ConsoleTransform(); if (anchor == null) return;
            float now = Time.unscaledTime;
            foreach (var c in _cards.Values)
            {
                if (c.LocalOwned || c.RemoteOwner == 0 || now >= c.RemoteUntil || !c.HasRemotePos || c.Token == null || c.T == null) continue;
                try
                {
                    if (!c.Token._externallyControlled) c.Token._externallyControlled = true;
                    c.T.position = anchor.TransformPoint(c.RemoteLocal);
                    c.T.rotation = anchor.rotation * c.RemoteRot;
                }
                catch { }
            }
        }

        private static void DrainPendingPoses()
        {
            if (_pendingPose.Count == 0) return;
            _drainKeys.Clear();
            foreach (var kv in _pendingPose) if (_cards.ContainsKey(kv.Key)) _drainKeys.Add(kv.Key);
            for (int i = 0; i < _drainKeys.Count; i++)
            {
                int k = _drainKeys[i];
                var pend = _pendingPose[k]; _pendingPose.Remove(k);
                if (_cards.TryGetValue(k, out var c)) ApplyPlace(c, pend.P, pend.R, pend.InSlot);
            }
        }

        private static void ClearAllOwnership()
        {
            foreach (var c in _cards.Values)
            { c.LocalOwned = false; c.RemoteOwner = 0; c.PrevDragging = false; c.HasRemotePos = false; ClearExternal(c); }
        }

        private static void ClearExternal(Card c)
        {
            try { if (c.Token != null && c.Token._externallyControlled) c.Token._externallyControlled = false; } catch { }
        }

        // ----- movement send -----

        private static void SendGrab(int key)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_PUNCH_GRAB; o = PutInt(o, key);
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendPos(Card c, Transform anchor)
        {
            if (!EnsureBuf() || c.T == null || anchor == null) return;
            Vector3 p; Quaternion r;
            try { p = anchor.InverseTransformPoint(c.T.position); r = Quaternion.Inverse(anchor.rotation) * c.T.rotation; }
            catch { return; }
            if (!Finite(p)) return;
            int o = 0; _buf[o++] = MSG_PUNCH_POS; o = PutInt(o, c.Key); o = PutV(o, p); o = PutQ(o, r);
            CoopP2P.Send(_buf, o, false);
        }

        private static void SendPlace(Card c, Transform anchor, bool log = true)
        {
            if (!EnsureBuf() || c.T == null || anchor == null) return;
            Vector3 p; Quaternion r;
            try { p = anchor.InverseTransformPoint(c.T.position); r = Quaternion.Inverse(anchor.rotation) * c.T.rotation; }
            catch { return; }
            bool inSlot = IsInReqSlot(c);
            int o = 0; _buf[o++] = MSG_PUNCH_PLACE; o = PutInt(o, c.Key); o = PutV(o, p); o = PutQ(o, r); _buf[o++] = (byte)(inSlot ? 1 : 0);
            CoopP2P.Send(_buf, o, true);
            if (log) Log.LogInfo($"[punch] placed card key={c.Key} (slot={inSlot}) -> peer");
        }

        // ----- movement receive -----

        private static void OnGrab(ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (len < 5) return;
            float now = Time.unscaledTime; int o = 1;
            int key = GetInt(a, ref o);
            if (!_cards.TryGetValue(key, out var c)) return;
            if (c.LocalOwned)
            {
                if (CoopP2P.GrabBeats(CoopP2P.MyId, origin)) return;   // I win — keep mine
                c.LocalOwned = false;                                  // I lose — yield
            }
            if (c.RemoteOwner == 0 || now >= c.RemoteUntil || c.RemoteOwner == origin || CoopP2P.GrabBeats(origin, c.RemoteOwner))
            { c.RemoteOwner = origin; c.RemoteUntil = now + StaleSec; }
        }

        private static void OnPos(ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (len < 1 + 4 + 12 + 16) return;
            float now = Time.unscaledTime; int o = 1;
            int key = GetInt(a, ref o);
            Vector3 p = GetV(a, ref o); Quaternion r = GetQ(a, ref o);
            if (!_cards.TryGetValue(key, out var c) || !Finite(p)) return;
            if (c.RemoteOwner == 0 || now >= c.RemoteUntil || c.RemoteOwner == origin || CoopP2P.GrabBeats(origin, c.RemoteOwner))
            { c.RemoteLocal = p; c.RemoteRot = r; c.HasRemotePos = true; c.RemoteOwner = origin; c.RemoteUntil = now + StaleSec; }
        }

        private static void OnPlace(ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (len < 1 + 4 + 12 + 16 + 1) return;
            int o = 1;
            int key = GetInt(a, ref o);
            Vector3 p = GetV(a, ref o); Quaternion r = GetQ(a, ref o);
            bool inSlot = a[o++] != 0;
            if (!_cards.TryGetValue(key, out var c)) { _pendingPose[key] = new PendPose { P = p, R = r, InSlot = inSlot }; return; }
            ApplyPlace(c, p, r, inSlot);
            // A place is a release: clear ownership if this origin holds it (at the 2-player cap origin == owner).
            if (c.RemoteOwner == origin || c.RemoteOwner == 0) { c.RemoteOwner = 0; c.HasRemotePos = false; }
        }

        // Set the final pose and reconcile slot occupancy. Uses RequisitionSlot.PlaceCard / ClearSlot (the card-aware
        // path) — NOT the generic ItemSlot.PlaceItem that fought the submit when CoopMap mishandled these.
        private static void ApplyPlace(Card c, Vector3 p, Quaternion r, bool inSlot)
        {
            if (c.T == null) return;
            var anchor = ConsoleTransform();
            _applyingRemote = true;
            try
            {
                if (anchor != null && Finite(p))
                {
                    try { if (!c.Token._externallyControlled) c.Token._externallyControlled = true; } catch { }
                    c.T.position = anchor.TransformPoint(p);
                    c.T.rotation = anchor.rotation * r;
                }
                var mgr = Mgr();
                var slot = mgr != null ? SafeReqSlot(mgr) : null;
                if (slot != null && c.Rt != null)
                {
                    try
                    {
                        if (inSlot)
                        {
                            bool already = false; try { already = slot.HasCard && slot.CurrentCard == c.Rt; } catch { }
                            if (!already) { try { if (slot.HasCard) slot.ClearSlot(); } catch { } slot.PlaceCard(c.Rt); }
                        }
                        else
                        {
                            bool hasMine = false; try { hasMine = slot.HasCard && slot.CurrentCard == c.Rt; } catch { }
                            if (hasMine) slot.ClearSlot();
                        }
                    }
                    catch (Exception e) { Log.LogWarning("[punch] apply slot: " + e.Message); }
                }
            }
            catch (Exception e) { Log.LogWarning("[punch] apply place: " + e.Message); }
            finally { _applyingRemote = false; }
            ClearExternal(c);   // final placement done — let the game's DraggableItem resume control
        }

        // ================= (A) DECK — host broadcast =================

        private static void HostPollDeck()
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.5f;

            var mgr = Mgr();
            if (mgr == null) { _lastDeckSig = null; return; }   // no console here — re-send when one appears

            var entries = ReadHostDeck(mgr);
            if (entries == null) return;

            string sig = DeckSig(entries);
            if (sig == _lastDeckSig) return;
            _lastDeckSig = sig;
            BroadcastDeck(entries);
        }

        // Sent to a late joiner from CoopP2P.SendJoinSnapshot (host only). Forces a re-send by clearing the cached sig.
        public static void SendSnapshot()
        {
            if (!Config.CoopPunchcardSync || !CoopP2P.IsHost) return;

            // Host-authoritative LAYOUT: re-send every card's current console-relative pose + slot state so the
            // joiner adopts the host's table arrangement instead of its own default spawn. Reuses MSG_PUNCH_PLACE
            // (the receiver applies pose and reconciles the slot, stashing any card whose console isn't up yet).
            var anchor = ConsoleTransform();
            if (anchor != null)
            {
                EnsureCards(true);
                foreach (var c in _cards.Values) { if (c.T != null) SendPlace(c, anchor, log: false); }
                Log.LogInfo($"[punch] layout snapshot -> joiner ({_cards.Count} card(s))");
            }

            // Deck-uses overlay rides along only when that (dormant) layer is enabled.
            if (Config.CoopPunchcardDeckSync)
            {
                var mgr = Mgr();
                var entries = mgr != null ? ReadHostDeck(mgr) : null;
                if (entries != null) { _lastDeckSig = DeckSig(entries); BroadcastDeck(entries); }
            }
        }

        private static List<DeckEntry> ReadHostDeck(RequisitionConsoleManager mgr)
        {
            try
            {
                var cards = mgr.GetAllCards();
                if (cards == null) return null;
                var list = new List<DeckEntry>(cards.Length);
                for (int i = 0; i < cards.Length; i++)
                {
                    var c = cards[i]; if (c == null) continue;
                    PunchcardDefinitionV2 def = null; try { def = c.CurrentDefinition; } catch { }
                    if (def == null) continue;
                    string id = null; int uses = 0;
                    try { id = def.ID; } catch { }
                    try { uses = def.RemainingUses; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new DeckEntry { Id = id, Uses = uses });
                }
                return list;
            }
            catch (Exception e) { Log.LogWarning("[punch] read deck: " + e.Message); return null; }
        }

        private static void BroadcastDeck(List<DeckEntry> entries)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_PUNCH_DECK;
            o = PutInt(o, entries.Count);
            for (int i = 0; i < entries.Count; i++) { o = PutStr(o, entries[i].Id); o = PutInt(o, entries[i].Uses); }
            CoopP2P.Send(_buf, o, true);
            _sentDeck++;
            Log.LogInfo($"[punch] deck -> peer ({entries.Count} card(s): {DeckSig(entries)})");
        }

        // ================= (A) DECK — client apply =================

        private static void OnDeckPacket(Il2CppStructArray<byte> a, int len)
        {
            if (CoopP2P.IsHost || !Config.CoopPunchcardDeckSync) return;   // host is authoritative; overlay dormant by default
            int o = 1;
            if (o + 4 > len) return;
            int count = GetInt(a, ref o);
            if (count < 0 || count > 256) return;
            var entries = new List<DeckEntry>(count);
            for (int i = 0; i < count; i++)
            {
                string id = GetStr(a, ref o, len); if (id == null) break;
                if (o + 4 > len) break;
                int uses = GetInt(a, ref o);
                entries.Add(new DeckEntry { Id = id, Uses = uses });
            }

            if (ConsoleReady()) ApplyDeck(entries);
            else _pendingDeck = entries;   // console not up yet — apply when it is (Tick)
        }

        // NON-DESTRUCTIVE overlay: the punchcard CATALOG is identical on both machines (same build → same 18 card
        // defs); only the per-save RemainingUses differs. So we DON'T RebuildDeck (clearing + respawning the deck
        // yanks a card mid-drag and breaks placement — the 2026-06-21 regression). We just copy the host's
        // RemainingUses onto the matching local card's shared definition and refresh its visuals; the physical cards
        // stay put.
        private static void ApplyDeck(List<DeckEntry> entries)
        {
            try
            {
                var mgr = Mgr(); if (mgr == null) { _pendingDeck = entries; return; }
                var cards = mgr.GetAllCards();
                if (cards == null) { _pendingDeck = entries; return; }

                var map = new Dictionary<string, int>(entries.Count);
                for (int i = 0; i < entries.Count; i++) map[entries[i].Id] = entries[i].Uses;

                int matched = 0;
                _applyingRemote = true;
                try
                {
                    for (int i = 0; i < cards.Length; i++)
                    {
                        var c = cards[i]; if (c == null) continue;
                        PunchcardDefinitionV2 def = null; try { def = c.CurrentDefinition; } catch { }
                        if (def == null) continue;
                        string id = null; try { id = def.ID; } catch { }
                        if (string.IsNullOrEmpty(id) || !map.TryGetValue(id, out int uses)) continue;
                        try { def.RemainingUses = uses; } catch { }
                        try { c.UpdateVisuals(); } catch { }
                        matched++;
                    }
                }
                finally { _applyingRemote = false; }
                _appliedDeck++;
                Log.LogInfo($"[punch] overlaid host uses onto {matched}/{entries.Count} local card(s) <- peer (non-destructive)");
            }
            catch (Exception e) { Log.LogWarning("[punch] apply deck: " + e.Message); }
        }

        // ================= (B) REDEMPTION — host-authoritative routing =================
        //
        // WHY a prefix here is SAFE (the earlier "it breaks the submit" was a misdiagnosis): for SOLO and the HOST
        // this prefix is a pure pass-through (returns true → the game's own AttemptRequisition runs unchanged). The
        // base-game scout-plane "slot rejection" reproduces SOLO, where this prefix never acts — so it was never the
        // cause. The ONLY behavior change is: a CLIENT-with-peer redemption is forwarded to the host instead of run
        // locally (the client's State_SpawnMapEntity is gated, so a local run spawns nothing anyway). The host runs
        // the graph authoritatively → entities replicate via CoopEntities → both maps match. Gated by
        // Config.CoopPunchcardRedeemSync for instant rollback without touching the working movement layer.
        //
        // We do NOT consume the client's card here (no optimistic consume): the host runs the REAL redemption, and
        // when it succeeds its PunchcardRuntime.OnCardUsed fires → the host broadcasts MSG_PUNCH_CONSUME → the client
        // consumes its matching card then. If the host rejects (requirements/points), nothing is consumed anywhere.

        // Registered by CoopSim.ApplyPatches. Returning false SKIPS the real redemption (client divert path only).
        public static bool OnAttemptRequisition(RequisitionSlot __instance)
        {
            try
            {
                if (_applyingRemote) return true;                 // host running a client's intent — run for real
                if (!Config.CoopPunchcardSync || !Config.CoopPunchcardRedeemSync) return true;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return true;   // solo — stock behavior
                if (CoopP2P.IsHost) return true;                  // host is authoritative — runs locally

                // CLIENT: capture the card + the player's chosen variables, send the intent to the host, skip the
                // local run. Card stays in the slot until the host's MSG_PUNCH_CONSUME confirms success.
                var card = __instance != null ? SafeCurrentCard(__instance) : null;
                if (card == null) return true;                    // nothing to redeem — let the game handle it
                string id = null; try { id = card.CurrentDefinition != null ? card.CurrentDefinition.ID : null; } catch { }
                if (string.IsNullOrEmpty(id)) return true;

                var vars = CaptureVariables();
                SendRedeem(id, vars);
                Log.LogInfo($"[punch] client redeem intent '{id}' -> host ({vars.Count} var(s)); awaiting host consume");
                return false;
            }
            catch (Exception e) { Log.LogWarning("[punch] attempt: " + e.Message); return true; }
        }

        // Prefix on PunchcardRuntime.OnCardUsed — capture the card ID BEFORE the run (the card may be destroyed by it).
        public static void OnCardUsedPre(PunchcardRuntime __instance, out string __state)
        {
            __state = null;
            try { if (__instance != null && __instance.CurrentDefinition != null) __state = __instance.CurrentDefinition.ID; } catch { }
        }

        // Postfix on PunchcardRuntime.OnCardUsed — fires when a card is actually consumed (the REAL success signal,
        // since AttemptRequisition returns void). On the HOST (its own redemption OR a client's forwarded one, which
        // also consumes a host card) broadcast an authoritative consume so every client drops its matching card.
        public static void OnCardUsedPost(string __state, bool __result)
        {
            try
            {
                if (!__result) return;                            // the card was not actually consumed
                if (_consumingMirror) return;                     // we're applying a host consume — don't echo
                if (!Config.CoopPunchcardSync || !Config.CoopPunchcardRedeemSync) return;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
                if (!CoopP2P.IsHost) return;                      // only the host issues authoritative consumes
                if (string.IsNullOrEmpty(__state)) return;
                SendConsume(__state);
            }
            catch (Exception e) { Log.LogWarning("[punch] oncardused: " + e.Message); }
        }

        private static void SendConsume(string cardId)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_PUNCH_CONSUME; o = PutStr(o, cardId);
            CoopP2P.Send(_buf, o, true);
            _sentConsume++;
            Log.LogInfo($"[punch] consume '{cardId}' -> peers");
        }

        // Client applies a host-authoritative consume: drop the matching local card (clear the slot if it's sitting
        // there, then OnCardUsed to decrement/destroy). Guarded by _consumingMirror so the resulting OnCardUsed
        // postfix doesn't re-broadcast.
        private static void OnConsumePacket(Il2CppStructArray<byte> a, int len)
        {
            if (CoopP2P.IsHost) return;
            int o = 1;
            string id = GetStr(a, ref o, len);
            if (string.IsNullOrEmpty(id)) return;
            var mgr = Mgr(); if (mgr == null) return;
            var card = FindDeckCard(mgr, id);
            if (card == null) { Log.LogWarning($"[punch] consume: no local card '{id}'"); return; }
            _consumingMirror = true;
            try
            {
                var slot = SafeReqSlot(mgr);
                try { if (slot != null && slot.HasCard && slot.CurrentCard == card) slot.ClearSlot(); } catch { }
                try { card.OnCardUsed(0f); } catch (Exception e) { Log.LogWarning("[punch] consume run: " + e.Message); }
            }
            finally { _consumingMirror = false; }
            _ranConsume++;
            Log.LogInfo($"[punch] consumed local card '{id}' <- host");
        }

        private static PunchcardRuntime SafeCurrentCard(RequisitionSlot slot)
        {
            try { if (!slot.HasCard) return null; } catch { }
            try { return slot.CurrentCard; } catch { return null; }
        }

        private static List<VarSnap> CaptureVariables()
        {
            var list = new List<VarSnap>();
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PunchcardVariable>(), FindObjectsSortMode.None);
                if (arr == null) return list;
                for (int i = 0; i < arr.Length && list.Count < 32; i++)
                {
                    var pv = arr[i].TryCast<PunchcardVariable>(); if (pv == null) continue;
                    string vid = null; try { vid = pv.VariableID; } catch { }
                    if (string.IsNullOrEmpty(vid)) continue;

                    var s = new VarSnap { Id = vid, GridLoc = GridNull, Text = "" };
                    try { s.Type = (int)pv.VariableType; } catch { }
                    try { s.I = pv.VariableInt; } catch { }
                    try { s.F = pv.VariableFloat; } catch { }
                    try { s.B = pv.VariableBool; } catch { }
                    try { s.Text = pv.VariableText ?? ""; } catch { }
                    try { s.Shell = (int)pv.VariableShellSlot; } catch { }
                    try { var gr = pv.VariableCoordinate; if (gr != null) { s.GridLoc = (int)gr.Location; s.GridX = gr.X; s.GridY = gr.Y; } } catch { }
                    list.Add(s);
                    string detail = s.Type == 3 ? (s.GridLoc != GridNull ? $"grid loc={s.GridLoc} x={s.GridX} y={s.GridY}" : "coord UNSET") : s.Type == 1 ? $"f={s.F}" : s.Type == 0 ? $"i={s.I}" : s.Type == 2 ? $"text='{s.Text}'" : s.Type == 4 ? $"b={s.B}" : $"shell={s.Shell}";
                    Log.LogInfo($"[punch] captured var '{s.Id}' type={s.Type} {detail}");
                }
            }
            catch (Exception e) { Log.LogWarning("[punch] capture vars: " + e.Message); }
            return list;
        }

        private static void SendRedeem(string cardId, List<VarSnap> vars)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_PUNCH_REDEEM;
            o = PutStr(o, cardId);
            o = PutInt(o, vars.Count);
            for (int i = 0; i < vars.Count; i++)
            {
                var v = vars[i];
                o = PutStr(o, v.Id);
                o = PutInt(o, v.Type);
                o = PutInt(o, v.I);
                o = PutF(o, v.F);
                _buf[o++] = (byte)(v.B ? 1 : 0);
                o = PutInt(o, v.GridLoc); o = PutInt(o, v.GridX); o = PutInt(o, v.GridY);
                o = PutInt(o, v.Shell);
                o = PutStr(o, v.Text);
            }
            CoopP2P.Send(_buf, o, true);
            _sentRedeem++;
        }

        // ================= (B) REDEMPTION — host runs it =================

        private static void OnRedeemPacket(Il2CppStructArray<byte> a, int len)
        {
            if (!CoopP2P.IsHost) return;   // only the host executes redemptions authoritatively
            int o = 1;
            string cardId = GetStr(a, ref o, len);
            if (string.IsNullOrEmpty(cardId)) return;
            if (o + 4 > len) return;
            int vc = GetInt(a, ref o);
            if (vc < 0 || vc > 64) return;

            var vars = new List<VarSnap>(vc);
            for (int i = 0; i < vc; i++)
            {
                var v = new VarSnap();
                v.Id = GetStr(a, ref o, len); if (v.Id == null) break;
                if (o + 4 > len) break; v.Type = GetInt(a, ref o);
                if (o + 4 > len) break; v.I = GetInt(a, ref o);
                if (o + 4 > len) break; v.F = GetF(a, ref o);
                if (o + 1 > len) break; v.B = a[o++] != 0;
                if (o + 12 > len) break; v.GridLoc = GetInt(a, ref o); v.GridX = GetInt(a, ref o); v.GridY = GetInt(a, ref o);
                if (o + 4 > len) break; v.Shell = GetInt(a, ref o);
                v.Text = GetStr(a, ref o, len) ?? "";
                vars.Add(v);
            }

            RunRedeem(cardId, vars);
        }

        private static void RunRedeem(string cardId, List<VarSnap> vars)
        {
            try
            {
                var mgr = Mgr();
                if (mgr == null) { Log.LogWarning($"[punch] client wants '{cardId}' but host has no requisition console"); return; }

                ApplyVariables(vars);

                var card = FindDeckCard(mgr, cardId);
                if (card == null) { Log.LogWarning($"[punch] host has no deck card '{cardId}' to run (deck out of sync?)"); return; }

                RequisitionSlot slot = null; try { slot = mgr.RequisitionSlot; } catch { }
                if (slot == null) { Log.LogWarning("[punch] host console has no RequisitionSlot"); return; }

                // Diagnostic pre-state: AttemptRequisition is void + validates silently, so capture the inputs that
                // can make it reject (cost vs the host's points, remaining uses) to reveal WHY a redeem produced nothing.
                int cost = -1, usesBefore = -1, maxUses = -1, points = -1;
                try { var d = card.CurrentDefinition; if (d != null) { cost = d.Cost; usesBefore = d.RemainingUses; maxUses = d.MaxUses; } } catch { }
                try { var mm = MissionManager.Instance; if (mm != null) { var st = mm.SaveOperationState(); if (st != null) points = st.RequisitionPoints; } } catch { }

                _applyingRemote = true;
                try
                {
                    try { if (slot.HasCard) slot.ClearSlot(); } catch { }
                    try { slot.PlaceCard(card); } catch (Exception e) { Log.LogWarning("[punch] place: " + e.Message); }
                    try { slot.AttemptRequisition(); } catch (Exception e) { Log.LogWarning("[punch] run: " + e.Message); }
                }
                finally { _applyingRemote = false; }

                int usesAfter = usesBefore; try { var d = card.CurrentDefinition; if (d != null) usesAfter = d.RemainingUses; } catch { }
                bool stillHeld = false; try { stillHeld = slot.HasCard && slot.CurrentCard == card; } catch { }
                bool succeeded = (usesBefore < 0 ? false : usesAfter < usesBefore) || !stillHeld;

                _ranRedeem++;
                _lastDeckSig = null;   // force a deck re-broadcast next poll so the client mirrors the consumed use
                Log.LogInfo($"[punch] host ran '{cardId}': success={succeeded} cost={cost} uses {usesBefore}->{usesAfter}/{maxUses} hostPoints={points} stillHeld={stillHeld} ({vars.Count} var)");
                if (!succeeded)
                    Log.LogWarning($"[punch] REDEEM REJECTED on host for '{cardId}' — cost={cost} vs hostPoints={points}, remainingUses={usesBefore}. The host runs redemptions authoritatively, so the HOST must have the points/uses (shared-pool resources aren't host-synced mid-mission yet).");
            }
            catch (Exception e) { Log.LogWarning("[punch] run redeem: " + e.Message); }
        }

        // VariableTypes (nested in PunchcardVariable): Int=0, Float=1, Text=2, Coordinate=3, Bool=4, ShellSlot=5.
        private static void ApplyVariables(List<VarSnap> vars)
        {
            if (vars.Count == 0) { Log.LogInfo("[punch] redeem carried 0 variables — host runs with its own scene state"); return; }
            PunchcardVariable[] scene = null;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PunchcardVariable>(), FindObjectsSortMode.None);
                if (arr != null)
                {
                    scene = new PunchcardVariable[arr.Length];
                    for (int i = 0; i < arr.Length; i++) scene[i] = arr[i].TryCast<PunchcardVariable>();
                }
            }
            catch (Exception e) { Log.LogWarning("[punch] find vars: " + e.Message); return; }
            if (scene == null) return;

            foreach (var v in vars)
            {
                PunchcardVariable pv = null;
                for (int i = 0; i < scene.Length; i++)
                {
                    var c = scene[i]; if (c == null) continue;
                    string id = null; try { id = c.VariableID; } catch { }
                    if (id == v.Id) { pv = c; break; }
                }
                if (pv == null) { Log.LogWarning($"[punch] var '{v.Id}' (type {v.Type}) — no matching PunchcardVariable in host scene"); continue; }

                // Use the Set* methods, NOT a raw field-set: they fire the change events the redemption requirement
                // and graph depend on. A field-set leaves the validator's view of the target "unset" → the host
                // rejects a client-forwarded redeem even though its OWN (dial-driven) redeem of the same card works.
                string detail;
                try
                {
                    switch (v.Type)
                    {
                        case 0: pv.SetInt(v.I); detail = $"i={v.I}"; break;
                        case 1: pv.SetFloat(v.F); detail = $"f={v.F}"; break;
                        case 2: pv.SetText(v.Text ?? ""); detail = $"text='{v.Text}'"; break;
                        case 3:
                            if (v.GridLoc != GridNull)
                            { var gr = new GridReference(); gr.Location = (GridLocations)v.GridLoc; gr.X = v.GridX; gr.Y = v.GridY; pv.SetCoordinate(gr); detail = $"grid loc={v.GridLoc} x={v.GridX} y={v.GridY}"; }
                            else { detail = "coordinate UNSET (GridNull) — client never picked a target?"; }
                            break;
                        case 4: try { pv.VariableBool = v.B; } catch { } detail = $"b={v.B}"; break;
                        case 5: pv.SetShellSlot(v.Shell); detail = $"shell={v.Shell}"; break;
                        default: detail = "unknown type"; break;
                    }
                }
                catch (Exception e) { detail = "SET FAILED: " + e.Message; }
                Log.LogInfo($"[punch] applied var '{v.Id}' type={v.Type} {detail}");
            }
        }

        private static PunchcardRuntime FindDeckCard(RequisitionConsoleManager mgr, string cardId)
        {
            try
            {
                var cards = mgr.GetAllCards();
                if (cards == null) return null;
                for (int i = 0; i < cards.Length; i++)
                {
                    var c = cards[i]; if (c == null) continue;
                    string id = null; try { id = c.CurrentDefinition != null ? c.CurrentDefinition.ID : null; } catch { }
                    if (id == cardId) return c;
                }
            }
            catch { }
            return null;
        }

        // ================= dispatch =================

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            switch (type)
            {
                case MSG_PUNCH_DECK:    OnDeckPacket(a, len); break;
                case MSG_PUNCH_REDEEM:  OnRedeemPacket(a, len); break;
                case MSG_PUNCH_GRAB:    OnGrab(origin, a, len); break;
                case MSG_PUNCH_POS:     OnPos(origin, a, len); break;
                case MSG_PUNCH_PLACE:   OnPlace(origin, a, len); break;
                case MSG_PUNCH_CONSUME: OnConsumePacket(a, len); break;
            }
        }

        // ================= diagnostics =================

        public static string Status()
        {
            int owned = 0, remote = 0;
            foreach (var c in _cards.Values) { if (c.LocalOwned) owned++; if (c.RemoteOwner != 0) remote++; }
            return $"punch: cards={_cards.Count}(local={owned} remote={remote} pend={_pendingPose.Count}) redeem(sent={_sentRedeem} ran={_ranRedeem}) consume(sent={_sentConsume} ran={_ranConsume})";
        }

        // ================= helpers =================

        private static RequisitionConsoleManager Mgr()
        {
            try { return RequisitionConsoleManager.Instance; } catch { return null; }
        }

        // Shared anchor for card positions: the console manager's transform. Same scene singleton on both machines,
        // so console-relative coordinates place a card identically relative to the console regardless of each
        // player's own VR playspace, and survive a card reparenting to a hand/drag-root mid-drag.
        private static Transform ConsoleTransform()
        {
            var mgr = Mgr(); if (mgr == null) return null;
            try { return mgr.transform; } catch { return null; }
        }

        private static RequisitionSlot SafeReqSlot(RequisitionConsoleManager mgr)
        {
            try { return mgr.RequisitionSlot; } catch { return null; }
        }

        private static bool IsInReqSlot(Card c)
        {
            try
            {
                var mgr = Mgr(); if (mgr == null) return false;
                var slot = SafeReqSlot(mgr); if (slot == null) return false;
                if (!slot.HasCard) return false;
                return slot.CurrentCard == c.Rt;
            }
            catch { return false; }
        }

        private static bool ConsoleReady()
        {
            var mgr = Mgr(); if (mgr == null) return false;
            try { return mgr.initialized; } catch { return true; }   // no flag readable -> assume usable
        }

        private static string DeckSig(List<DeckEntry> entries)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++) { sb.Append(entries[i].Id); sb.Append(':'); sb.Append(entries[i].Uses); sb.Append('|'); }
            return sb.ToString();
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(2048); return true; }
            catch (Exception e) { Log.LogWarning("[punch] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { _buf[o] = (byte)v; _buf[o + 1] = (byte)(v >> 8); _buf[o + 2] = (byte)(v >> 16); _buf[o + 3] = (byte)(v >> 24); return o + 4; }
        private static int PutF(int o, float v) { int b = BitConverter.SingleToInt32Bits(v); _buf[o] = (byte)b; _buf[o + 1] = (byte)(b >> 8); _buf[o + 2] = (byte)(b >> 16); _buf[o + 3] = (byte)(b >> 24); return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }
        private static int PutQ(int o, Quaternion q) { o = PutF(o, q.x); o = PutF(o, q.y); o = PutF(o, q.z); o = PutF(o, q.w); return o; }
        private static Vector3 GetV(Il2CppStructArray<byte> a, ref int o) { float x = GetF(a, ref o), y = GetF(a, ref o), z = GetF(a, ref o); return new Vector3(x, y, z); }
        private static Quaternion GetQ(Il2CppStructArray<byte> a, ref int o) { float x = GetF(a, ref o), y = GetF(a, ref o), z = GetF(a, ref o), w = GetF(a, ref o); return new Quaternion(x, y, z, w); }

        private static int Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        private static int PutStr(int o, string s)
        {
            s ??= "";
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length; if (n > 200) n = 200;
            o = PutInt(o, n);
            for (int i = 0; i < n; i++) _buf[o + i] = bytes[i];
            return o + n;
        }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
        private static float GetF(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }

        private static string GetStr(Il2CppStructArray<byte> a, ref int o, int len)
        {
            if (o + 4 > len) return null;
            int n = GetInt(a, ref o);
            if (n < 0 || o + n > len || n > _scratch.Length) return null;
            for (int i = 0; i < n; i++) _scratch[i] = a[o + i];
            o += n;
            return Encoding.UTF8.GetString(_scratch, 0, n);
        }
    }
}
