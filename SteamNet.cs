using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 1 co-op networking: public Steam lobby create / browse / join.
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
    /// (A proper in-VR lobby browser comes in Phase 1.5; this proves the transport first.)
    /// </summary>
    internal static class SteamNet
    {
        private static ManualLogSource Log => Plugin.Logger;

        private const string ModKey = "invr_coop";   // lobby-data marker so the browser lists only our lobbies
        private const string ModVal = "1";
        private const string NameKey = "name";
        private static int MaxMembers => Config.CoopMaxPlayers;   // lobby member cap (Config-driven; see CoopMaxPlayers)

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
            set => _inLobby = value;
        }

        public struct LobbyEntry { public CSteamID Id; public string Name; public int Members; public int Max; }
        public static readonly List<LobbyEntry> Lobbies = new List<LobbyEntry>();

        public static void Tick()
        {
            EnsureInit();
            if (_inited && PumpCallbacks) { try { SteamAPI.RunCallbacks(); } catch { } }
            PollKeys();
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

        public static void CreateLobby()
        {
            try
            {
                if (_crCreate == null) { Log.LogWarning("[net] create: not ready"); return; }
                LeaveCurrentIfAny();   // one lobby at a time — don't accumulate orphan lobbies
                Log.LogInfo($"[net] creating PUBLIC lobby (max {MaxMembers})…");
                _crCreate.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, MaxMembers));
            }
            catch (Exception e) { Log.LogError("[net] CreateLobby: " + e); }
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
            Log.LogInfo($"[net] LOBBY CREATED  id={id.m_SteamID}  name='{name}'  (public, max {MaxMembers}). Other instances see it via F10.");
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
                string name = SteamMatchmaking.GetLobbyData(id, NameKey);
                int members = SteamMatchmaking.GetNumLobbyMembers(id);
                int max = SteamMatchmaking.GetLobbyMemberLimit(id);
                if (string.IsNullOrEmpty(name)) name = "(lobby " + id.m_SteamID + ")";
                Lobbies.Add(new LobbyEntry { Id = id, Name = name, Members = members, Max = max });
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
            int members = SteamMatchmaking.GetNumLobbyMembers(id);
            Log.LogInfo($"[net] ENTERED lobby {id.m_SteamID} — {members} member(s)  (enterResponse={e.m_EChatRoomEnterResponse})");
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

        private static void PollKeys()
        {
            if (Key(UnityEngine.InputSystem.Key.F7)) LobbyGui.Shown = !LobbyGui.Shown; // show/hide flatscreen lobby panel
            if (!_inited) return;
            if (Key(UnityEngine.InputSystem.Key.F9)) CreateLobby();
            if (Key(UnityEngine.InputSystem.Key.F10)) RefreshLobbyList();
            if (Key(UnityEngine.InputSystem.Key.F11)) JoinLobbyByIndex(0);
            if (Key(UnityEngine.InputSystem.Key.F12)) Leave();
        }
    }
}
