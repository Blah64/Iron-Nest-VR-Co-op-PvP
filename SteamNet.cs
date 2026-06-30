using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op networking: public Steam lobby create / browse / join.
    ///
    /// Rides the GAME's already-running Steamworks.NET (the demo ships Heathen + Steamworks.NET and
    /// SteamAPI is initialised before we load). So we must NOT call SteamAPI.Init/Shutdown. We register
    /// Callback/CallResult handlers into the game's IL2CPP CallbackDispatcher (a shared static), then call
    /// matchmaking APIs. Because the dispatcher is shared, our calling SteamAPI.RunCallbacks() is safe even
    /// if the game also pumps it: whoever drains the single client pipe dispatches to ALL registered
    /// handlers (ours included); the other finds an empty queue. Pumping ourselves removes any dependency
    /// on the game pumping every frame.
    ///
    /// "Public, no friend required": CreateLobby uses k_ELobbyTypePublic and tags the lobby with our mod
    /// key; the browser filters RequestLobbyList on that key so the list shows only IronNestVR lobbies.
    ///
    /// Debug triggers (keyboard): F9 create | F10 refresh list | F11 join first listed | F12 leave.
    /// (A proper in-VR lobby browser comes later; this proves the transport first.)
    /// </summary>
    internal static class SteamNet
    {
        private static ManualLogSource Log => Plugin.Logger;

        private const string ModKey = "invr_coop";   // lobby-data marker so the browser lists only our lobbies
        private const string ModVal = "1";
        private const string NameKey = "name";
        private const string ModeKey = "invr_mode";   // "coop" | "pvp" — host tags the lobby; members derive Config.PvpActive
        private const string LockKey = "invr_locked"; // "1" while the lobby is locked — browser hides it + clients show the badge
        private static bool _pendingPvp;              // mode of the lobby we're about to create (consumed in OnLobbyCreated)
        public static int MaxMembers => Config.CoopMaxPlayers;   // lobby member cap (Config-driven; see CoopMaxPlayers)

        // --- Lobby lock (host-owned) ---------------------------------------------------------------------------
        // Steam has no "kick" and a lobby's joinability is the lock primitive: SetLobbyJoinable(false) both stops new
        // joins AND drops the lobby from every RequestLobbyList, which is exactly "locked + removed from the browser".
        // Two independent reasons to lock, OR'd into the effective state: the host toggled it (_manualLock), or a
        // mission launched (_matchLock, auto-applied + auto-lifted by the phase watcher in Tick).
        private static bool _manualLock;
        private static bool _matchLock;
        public static bool IsLocked => _manualLock || _matchLock;
        public static bool ManualLock => _manualLock;
        private static int _lastPhase = -999;        // mission-phase edge tracker for the auto-lock

        // --- Kick (host-owned) ---------------------------------------------------------------------------------
        // There's no Steam kick API, so the host BROADCASTS a kick naming a SteamID; only that member acts (it leaves
        // itself). A banned set re-bounces anyone who rejoins (covers a kicked player clicking Join again).
        public const byte MSG_KICK = 47;            // [t][targetLo i32][targetHi i32]  host->all (origin must be the host)
        private static readonly HashSet<ulong> _banned = new HashSet<ulong>();
        private static float _nextBanSweep;
        private static Il2CppStructArray<byte> _kickBuf;

        // Roster scratch (members enumerated from the live Steam lobby; loopback falls back to the P2P peer set).
        public struct Member { public ulong Id; public string Name; public bool IsHost; public bool IsMe; }
        private static readonly List<ulong> _peerScratch = new List<ulong>();

        // Shared IL2CPP dispatcher, so pumping ourselves is safe. Flip off only if it ever proves otherwise.
        public static bool PumpCallbacks = true;

        private static bool _inited;
        private static float _nextTry;
        private static bool _steamUninit;   // Steam client up, but the GAME never called SteamAPI.Init
        private static bool _hintLogged;    // throttle the launch-via-Steam hint to once

        // Must stay referenced or the registrations are GC'd.
        private static Callback<LobbyEnter_t> _cbEnter;
        private static CallResult<LobbyCreated_t> _crCreate;
        private static CallResult<LobbyMatchList_t> _crList;

        public static CSteamID CurrentLobby;

        // A loopback test link counts as "in a session" so every co-op subsystem (which gates on
        // SteamNet.InLobby && CoopP2P.HasPeer) activates without a Steam lobby. The backing field stays the
        // real Steam-lobby state; loopback only ORs in on read. See LoopbackTransport.
        private static bool _inLobby;
        public static bool InLobby
        {
            get => _inLobby || LoopbackTransport.Connected;
            // Leaving any lobby clears the derived PvP mode + every host-owned lobby setting (lock/ban) in one place
            // (covers leave / join-fail / auto-leave / kick).
            set { _inLobby = value; if (!value) { Config.PvpActive = false; _manualLock = false; _matchLock = false; _lastPhase = -999; _banned.Clear(); } }
        }

        public struct LobbyEntry { public CSteamID Id; public string Name; public int Members; public int Max; public bool Locked; }
        public static readonly List<LobbyEntry> Lobbies = new List<LobbyEntry>();

        public static void Tick()
        {
            EnsureInit();
            if (_inited && PumpCallbacks) { try { SteamAPI.RunCallbacks(); } catch { } }
            PollKeys();
            HostLobbyMaintenance();
        }

        // Host-only, per-tick: (1) auto-lock the lobby when a mission launches and lift it on return to the lobby,
        // (2) re-bounce any banned member who rejoined. No-op for clients, when not in a lobby, or in loopback.
        private static void HostLobbyMaintenance()
        {
            if (!InLobby || !CoopP2P.IsHost) return;

            // Auto-lock on mission launch: while a mission is active the lobby is closed to newcomers and hidden from
            // the browser; returning to the lobby (any non-mission phase) lifts the AUTO lock but keeps a manual one.
            int phase = CurrentMissionPhase();
            if (phase != _lastPhase)
            {
                _lastPhase = phase;
                bool inMission = phase == (int)MissionManager.GamePhase.MissionActive;
                if (inMission && !_matchLock) { _matchLock = true; ApplyLockToSteam(); Log.LogInfo("[net] mission launched — lobby auto-locked"); }
                else if (!inMission && _matchLock) { _matchLock = false; ApplyLockToSteam(); Log.LogInfo("[net] back in lobby — auto-lock lifted"); }
            }

            // Re-bounce any banned id that slipped back into the lobby (only acts on a real Steam lobby).
            if (_banned.Count > 0 && Time.unscaledTime >= _nextBanSweep && CurrentLobby.m_SteamID != 0UL)
            {
                _nextBanSweep = Time.unscaledTime + 1f;
                try
                {
                    int m = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
                    for (int i = 0; i < m; i++)
                    {
                        ulong id = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobby, i).m_SteamID;
                        if (id != 0UL && id != CoopP2P.MyId && _banned.Contains(id)) BroadcastKick(id);
                    }
                }
                catch { }
            }
        }

        private static int CurrentMissionPhase()
        {
            try { var mm = MissionManager.Instance; return mm == null ? -1 : (int)mm.CurrentPhase; }
            catch { return -1; }
        }

        private static void EnsureInit()
        {
            if (_inited) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 2f;
            try
            {
                if (!SteamAPI.IsSteamRunning()) { Log.LogInfo("[net] waiting for Steam to be ready…"); return; }

                uint appId = SteamUtils.GetAppID().m_AppId;   // throws "not initialized" if game didn't SteamAPI.Init
                _steamUninit = false;
                ulong me = SteamUser.GetSteamID().m_SteamID;
                string persona = SteamFriends.GetPersonaName();
                Log.LogInfo($"[net] Steam reachable — appId={appId} steamID={me} persona='{persona}'");

                // Register into the game's shared dispatcher. Each in its own try so the log pinpoints any
                // IL2CPP generic instantiation the build didn't AOT-compile (then we'd pivot to Heathen).
                // Re-register only what isn't already registered, so a retry doesn't double-register the
                // generics that succeeded last pass (a failing one means the build didn't AOT-compile it).
                try { if (_cbEnter == null) { _cbEnter = Callback<LobbyEnter_t>.Create((Action<LobbyEnter_t>)OnLobbyEnter); Log.LogInfo("[net]  + LobbyEnter callback registered"); } }
                catch (Exception e) { Log.LogError("[net] LobbyEnter callback FAILED: " + e); }
                try { if (_crCreate == null) { _crCreate = CallResult<LobbyCreated_t>.Create((Action<LobbyCreated_t, bool>)OnLobbyCreated); Log.LogInfo("[net]  + LobbyCreated callresult registered"); } }
                catch (Exception e) { Log.LogError("[net] LobbyCreated callresult FAILED: " + e); }
                try { if (_crList == null) { _crList = CallResult<LobbyMatchList_t>.Create((Action<LobbyMatchList_t, bool>)OnLobbyList); Log.LogInfo("[net]  + LobbyMatchList callresult registered"); } }
                catch (Exception e) { Log.LogError("[net] LobbyMatchList callresult FAILED: " + e); }

                // LobbyEnter + LobbyCreated are essential: without them create/join silently never report
                // success. Don't claim READY (which lights up the UI) until both are registered — retry instead.
                if (_cbEnter == null || _crCreate == null)
                {
                    Log.LogError($"[net] essential callbacks missing — NOT ready (retrying). enter={_cbEnter != null} create={_crCreate != null} list={_crList != null}");
                    return;
                }
                if (_crList == null) Log.LogWarning("[net] lobby-list callresult missing — browse disabled, but create/join work");

                _inited = true;
                Log.LogInfo("[net] READY. Keys:  F9 create lobby | F10 refresh list | F11 join #0 | F12 leave");
            }
            catch (Exception e)
            {
                // Steam client is running (IsSteamRunning passed) but the GAME process never called
                // SteamAPI.Init, so every Steamworks.NET call throws "not initialized". Almost always the
                // game wasn't launched THROUGH Steam (or has no steam_appid.txt next to the exe). Give an
                // actionable hint once instead of looping a stack trace, and let StatusLine show the cause.
                _steamUninit = e is InvalidOperationException || e.Message.IndexOf("not initialized", StringComparison.OrdinalIgnoreCase) >= 0;
                if (_steamUninit)
                {
                    if (!_hintLogged)
                    {
                        _hintLogged = true;
                        Log.LogError("[net] Steam API not initialized in this process. LAUNCH THE GAME VIA STEAM (Play button) — or drop a steam_appid.txt containing 4300500 next to the game exe. Steam must be running and online.");
                    }
                }
                else Log.LogWarning("[net] init retry: " + e.Message);
            }
        }

        public static void CreateLobby() => CreateLobby(false);

        // pvp=true tags the new lobby invr_mode=pvp; every member then derives Config.PvpActive on enter.
        public static void CreateLobby(bool pvp)
        {
            try
            {
                if (_crCreate == null) { Log.LogWarning("[net] create: not ready"); return; }
                _pendingPvp = pvp;
                LeaveCurrentIfAny();   // one lobby at a time — don't accumulate orphan lobbies
                Log.LogInfo($"[net] creating PUBLIC {(pvp ? "PvP" : "co-op")} lobby (max {MaxMembers})…");
                _crCreate.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, MaxMembers));
            }
            catch (Exception e) { Log.LogError("[net] CreateLobby: " + e); }
        }

        // Derive Config.PvpActive from a lobby's invr_mode tag (called by both host on-create and every member on-enter).
        private static void ApplyMode(string mode)
        {
            bool pvp = string.Equals(mode, "pvp", StringComparison.OrdinalIgnoreCase);
            if (Config.PvpActive != pvp) Log.LogInfo($"[net] lobby mode = {(pvp ? "PvP" : "co-op")} → Config.PvpActive={pvp}");
            Config.PvpActive = pvp;
        }

        // Leave whatever lobby we're in (no-op if none). Steam lets one user join many lobbies at once, so
        // create/join must drop the previous one or they pile up (each shows 1/2 forever until you quit).
        private static void LeaveCurrentIfAny()
        {
            if (!InLobby) return;
            try { SteamMatchmaking.LeaveLobby(CurrentLobby); Log.LogInfo($"[net] (auto) left previous lobby {CurrentLobby.m_SteamID}"); }
            catch (Exception e) { Log.LogWarning("[net] auto-leave: " + e.Message); }
            InLobby = false;
        }

        public static void RefreshLobbyList()
        {
            try
            {
                if (_crList == null) { Log.LogWarning("[net] list: not ready"); return; }
                Log.LogInfo("[net] requesting public lobby list…");
                SteamMatchmaking.AddRequestLobbyListStringFilter(ModKey, ModVal, ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
                _crList.Set(SteamMatchmaking.RequestLobbyList());
            }
            catch (Exception e) { Log.LogError("[net] RefreshLobbyList: " + e); }
        }

        public static void JoinLobbyByIndex(int i)
        {
            try
            {
                if (i < 0 || i >= Lobbies.Count) { Log.LogWarning($"[net] join: no lobby #{i} (list has {Lobbies.Count}; press F10 first)"); return; }
                var e = Lobbies[i];
                // Clicking Join on the lobby we're ALREADY in would leave+rejoin and tear down a working P2P
                // session (avatar blinks out). No-op instead — this was the tester's self-inflicted disconnect.
                if (InLobby && CurrentLobby.m_SteamID == e.Id.m_SteamID) { Log.LogInfo("[net] already in that lobby — ignoring join"); return; }
                LeaveCurrentIfAny();   // leave our own/previous lobby before joining another
                Log.LogInfo($"[net] joining '{e.Name}' (id={e.Id.m_SteamID})…");
                SteamMatchmaking.JoinLobby(e.Id);
            }
            catch (Exception ex) { Log.LogError("[net] Join: " + ex); }
        }

        public static void Leave()
        {
            try
            {
                if (!InLobby) { Log.LogInfo("[net] not in a lobby"); return; }
                Log.LogInfo($"[net] leaving lobby {CurrentLobby.m_SteamID}");
                SteamMatchmaking.LeaveLobby(CurrentLobby);
                InLobby = false;
            }
            catch (Exception e) { Log.LogError("[net] Leave: " + e); }
        }

        public static bool Ready => _inited || LoopbackTransport.Connected;

        // One-line status for the VR menu / flatscreen GUI.
        public static string StatusLine()
        {
            try
            {
                if (!_inited) return _steamUninit ? "Steam not initialized — launch via Steam" : "Steam: connecting…";
                if (InLobby)
                {
                    int m = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
                    return $"In lobby ({m}/{MaxMembers})  id={CurrentLobby.m_SteamID}";
                }
                return "Not in a lobby";
            }
            catch { return "Steam: ?"; }
        }

        // Display label for the i-th browsable lobby ("—" if that slot is empty). Used by the VR menu's
        // fixed join slots, whose value text auto-refreshes each tick after a browse completes.
        public static string SlotLabel(int i)
        {
            if (i < 0 || i >= Lobbies.Count) return "—";
            var e = Lobbies[i];
            return $"{e.Name}  {e.Members}/{e.Max}";
        }

        // ---------------- lobby lock ----------------

        public static void ToggleLock() => SetManualLock(!_manualLock);

        public static void SetManualLock(bool on)
        {
            if (!CoopP2P.IsHost) { Log.LogInfo("[net] only the host can lock the lobby"); return; }
            if (_manualLock == on) return;
            _manualLock = on;
            ApplyLockToSteam();
        }

        // Push the effective lock to Steam: an unjoinable lobby is also dropped from RequestLobbyList, so this both
        // stops new joins and removes the lobby from everyone's browser. Plus a data tag so a member already inside
        // (or a stale browser row) can show the lock badge. Host-only; no-op without a real Steam lobby (loopback).
        private static void ApplyLockToSteam()
        {
            if (!CoopP2P.IsHost) return;
            if (CurrentLobby.m_SteamID == 0UL || !CurrentLobby.IsLobby()) return;
            bool locked = IsLocked;
            try
            {
                SteamMatchmaking.SetLobbyJoinable(CurrentLobby, !locked);
                SteamMatchmaking.SetLobbyData(CurrentLobby, LockKey, locked ? "1" : "0");
                Log.LogInfo($"[net] lobby {(locked ? "LOCKED" : "unlocked")} (manual={_manualLock} match={_matchLock})");
            }
            catch (Exception e) { Log.LogWarning("[net] lock apply: " + e.Message); }
        }

        // ---------------- kick ----------------

        // Host removes a player: ban them (so a rejoin is re-bounced) and broadcast the kick. The named member leaves
        // itself on receipt (see OnPacket). No-op for non-hosts / yourself.
        public static void Kick(ulong target)
        {
            if (!CoopP2P.IsHost) { Log.LogInfo("[net] only the host can kick"); return; }
            if (target == 0UL || target == CoopP2P.MyId) return;
            _banned.Add(target);
            BroadcastKick(target);
            Log.LogInfo($"[net] kicking '{NameForId(target)}' ({target})");
        }

        private static void BroadcastKick(ulong target)
        {
            try
            {
                if (_kickBuf == null) _kickBuf = new Il2CppStructArray<byte>(16);
                var w = new CoopWire.Writer(_kickBuf);
                w.Byte(MSG_KICK);
                w.Int((int)(target & 0xFFFFFFFFUL));
                w.Int((int)(target >> 32));
                if (w.Overflow) return;
                CoopP2P.Send(_kickBuf, w.Length, true);   // reliable; only the host authors it
            }
            catch (Exception e) { Log.LogWarning("[net] kick send: " + e.Message); }
        }

        // Routed here from CoopControls.OnPacket. Only the host may kick, and only the named target leaves.
        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_KICK) return;
            if (origin != CoopP2P.HostSteamId) return;   // ignore a kick not authored by the host (defends a relayed forgery)
            var r = new CoopWire.Reader(a, len, 1);
            ulong target = (uint)r.Int() | ((ulong)(uint)r.Int() << 32);
            if (r.Bad || target != CoopP2P.MyId) return; // not me — only the target acts
            Log.LogWarning("[net] removed from the lobby by the host");
            try { Notify.Show("Removed from the lobby by the host"); } catch { }
            Leave();
        }

        // ---------------- roster (player list) ----------------

        // Fill the current lobby's members. The live Steam lobby is the authoritative list everyone sees the same;
        // loopback (no Steam lobby) falls back to the P2P peer set so the panels are testable in two windows.
        public static void FillMembers(List<Member> dst)
        {
            dst.Clear();
            if (CurrentLobby.m_SteamID != 0UL && CurrentLobby.IsLobby())
            {
                ulong owner = 0; try { owner = SteamMatchmaking.GetLobbyOwner(CurrentLobby).m_SteamID; } catch { }
                int m = 0; try { m = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby); } catch { }
                for (int i = 0; i < m; i++)
                {
                    ulong id; try { id = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobby, i).m_SteamID; } catch { continue; }
                    if (id == 0UL) continue;
                    dst.Add(new Member { Id = id, Name = NameForId(id), IsHost = id == owner, IsMe = id == CoopP2P.MyId });
                }
                return;
            }
            if (LoopbackTransport.Connected)
            {
                dst.Add(new Member { Id = CoopP2P.MyId, Name = SelfName(), IsHost = CoopP2P.IsHost, IsMe = true });
                CoopP2P.CopyPeerIds(_peerScratch);
                for (int i = 0; i < _peerScratch.Count; i++)
                    dst.Add(new Member { Id = _peerScratch[i], Name = CoopP2P.NameFor(_peerScratch[i]), IsHost = false, IsMe = false });
            }
        }

        private static string NameForId(ulong id)
        {
            if (id == CoopP2P.MyId) return SelfName();
            try { var n = SteamFriends.GetFriendPersonaName(new CSteamID { m_SteamID = id }); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            var pn = CoopP2P.NameFor(id);
            return string.IsNullOrEmpty(pn) ? ("Player " + (id % 10000)) : pn;
        }

        private static string SelfName()
        {
            try { var n = SteamFriends.GetPersonaName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "Me";
        }

        private static void OnLobbyCreated(LobbyCreated_t r, bool ioFail)
        {
            if (ioFail || r.m_eResult != EResult.k_EResultOK)
            { Log.LogError($"[net] CREATE FAILED: result={r.m_eResult} ioFail={ioFail}"); return; }

            var id = new CSteamID { m_SteamID = r.m_ulSteamIDLobby };
            CurrentLobby = id; InLobby = true;
            // Suffix with the low digits of the lobby id so multiple lobbies are distinguishable in the
            // browser (otherwise every lobby from one account reads identically as "<name>'s turret").
            string name = SteamFriends.GetPersonaName() + "'s turret #" + (id.m_SteamID % 1000UL);
            SteamMatchmaking.SetLobbyData(id, ModKey, ModVal);   // marker so it appears in our filtered browser
            SteamMatchmaking.SetLobbyData(id, NameKey, name);
            string mode = _pendingPvp ? "pvp" : "coop";
            SteamMatchmaking.SetLobbyData(id, ModeKey, mode);    // co-op | pvp — members derive Config.PvpActive on enter
            SteamMatchmaking.SetLobbyData(id, LockKey, "0");     // a fresh lobby is open (joinable + browsable)
            _manualLock = false; _matchLock = false; _lastPhase = -999; _banned.Clear();
            ApplyMode(mode);                                     // host adopts its own chosen mode immediately
            Log.LogInfo($"[net] LOBBY CREATED  id={id.m_SteamID}  name='{name}'  mode={mode}  (public, max {MaxMembers}). Other instances see it via F10.");
        }

        private static void OnLobbyList(LobbyMatchList_t r, bool ioFail)
        {
            Lobbies.Clear();
            if (ioFail) { Log.LogError("[net] lobby list I/O failure"); return; }

            // NOTE: r.m_nLobbiesMatching marshals as GARBAGE through the IL2CPP CallResult (a single-uint
            // struct param doesn't pass cleanly), so we ignore it and walk GetLobbyByIndex until it returns
            // an invalid id — Steam yields k_steamIDNil (0) past the real count. (Cap guards against junk.)
            const int cap = 100;
            for (int i = 0; i < cap; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                if (id.m_SteamID == 0UL || !id.IsLobby()) break;
                // A locked lobby is unjoinable so Steam normally omits it from the list; skip it here too as a
                // belt-and-suspenders (the data tag may arrive before the joinable flag propagates).
                if (SteamMatchmaking.GetLobbyData(id, LockKey) == "1") continue;
                string name = SteamMatchmaking.GetLobbyData(id, NameKey);
                int members = SteamMatchmaking.GetNumLobbyMembers(id);
                int max = SteamMatchmaking.GetLobbyMemberLimit(id);
                if (string.IsNullOrEmpty(name)) name = "(lobby " + id.m_SteamID + ")";
                Lobbies.Add(new LobbyEntry { Id = id, Name = name, Members = members, Max = max, Locked = false });
            }

            Log.LogInfo($"[net] === LOBBY LIST ({Lobbies.Count}) ===");
            for (int k = 0; k < Lobbies.Count; k++)
                Log.LogInfo($"[net]   #{k}: '{Lobbies[k].Name}'  {Lobbies[k].Members}/{Lobbies[k].Max}  id={Lobbies[k].Id.m_SteamID}");
            if (Lobbies.Count == 0) Log.LogInfo("[net]   (none found — create one with F9 from another instance/account)");
        }

        private static void OnLobbyEnter(LobbyEnter_t e)
        {
            var id = new CSteamID { m_SteamID = e.m_ulSteamIDLobby };
            // Check the enter response BEFORE mutating state — a full/locked/denied join (likely here, since
            // MaxMembers=2 makes "full" a normal path) must not leave us in a phantom lobby. CoopP2P sees
            // InLobby=false next tick and clears its peer/session/avatar.
            if (e.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Log.LogError($"[net] ENTER FAILED for lobby {id.m_SteamID} — response={(EChatRoomEnterResponse)e.m_EChatRoomEnterResponse} ({e.m_EChatRoomEnterResponse})");
                CurrentLobby = default; InLobby = false;
                return;
            }
            CurrentLobby = id; InLobby = true;
            ApplyMode(SteamMatchmaking.GetLobbyData(id, ModeKey));   // derive PvP/co-op from the host's tag (default coop)
            int members = SteamMatchmaking.GetNumLobbyMembers(id);
            Log.LogInfo($"[net] ENTERED lobby {id.m_SteamID} — {members} member(s)  mode={(Config.PvpActive ? "pvp" : "coop")}  (enterResponse={e.m_EChatRoomEnterResponse})");
            for (int i = 0; i < members; i++)
            {
                var m = SteamMatchmaking.GetLobbyMemberByIndex(id, i);
                Log.LogInfo($"[net]   member {i}: '{SteamFriends.GetFriendPersonaName(m)}' ({m.m_SteamID})");
            }
        }

        private static bool Key(UnityEngine.InputSystem.Key k)
        {
            try { var kb = UnityEngine.InputSystem.Keyboard.current; return kb != null && kb[k].wasPressedThisFrame; }
            catch { return false; }
        }

        private static bool ShiftHeld()
        {
            try { var kb = UnityEngine.InputSystem.Keyboard.current; return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed); }
            catch { return false; }
        }

        private static void PollKeys()
        {
            if (Key(UnityEngine.InputSystem.Key.F7)) LobbyGui.Shown = !LobbyGui.Shown; // show/hide flatscreen lobby panel
            if (!_inited) return;
            if (Key(UnityEngine.InputSystem.Key.F9)) CreateLobby(false);   // F9 = co-op lobby (PvE/PvP choice is in the F7 panel)
            if (Key(UnityEngine.InputSystem.Key.F10)) RefreshLobbyList();
            if (Key(UnityEngine.InputSystem.Key.F11)) JoinLobbyByIndex(0);
            if (Key(UnityEngine.InputSystem.Key.F12)) Leave();
        }
    }
}
