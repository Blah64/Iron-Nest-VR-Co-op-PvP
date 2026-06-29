# IRON NEST VR, CO-OP & PVP
This mod lets players play in VR and to play cooperative or competitive multiplayer with up to 3 other players in the same nest for co-op or split between 2
nests for PvP.
Crossplay is supported with flatscreen players.

> This package contains **only the mod**. You install BepInEx yourself first
> (step 1) — it's a one-time setup.

## 1. Install BepInEx 6 (IL2CPP, x64)
This is an IL2CPP Unity game, so it needs **BepInEx 6 (IL2CPP)** — *not*
BepInEx 5 (Mono).

1. Download the latest **BepInEx 6 bleeding-edge** build for **Unity IL2CPP /
   win-x64** from <https://builds.bepinex.dev/projects/bepinex_be> (the
   `BepInEx-Unity.IL2CPP-win-x64` artifact, which bundles the .NET runtime).
   *(Developed against build `6.0.0-be.764`; but any recent IL2CPP x64 build should work.)*
2. Open the game folder: in Steam, right-click the demo → **Manage → Browse
   local files**.
3. Extract the BepInEx zip into that folder so **`winhttp.dll`** and the
   **`BepInEx`** folder sit next to `Iron Nest Heavy Turret Simulator.exe`.
4. Launch the game once, wait ~1–3 min at the menu (BepInEx builds its support
   files), then quit.

## 2. Install this mod
Extract this zip's contents directly into your game folder (the one containing
`Iron Nest Heavy Turret Simulator.exe`), choosing **Yes / Replace** if asked to
merge. That's it — it drops in:
- `openxr_loader.dll` next to the game `.exe` — the OpenXR loader the VR layer needs, and
- the mod under `BepInEx\plugins\IronNestVR\`.

## 3. Set launch options (required)
Steam → right-click the demo → **Properties → Launch Options**:
```
-force-d3d11 -force-gfx-direct
```

## 4. Play
Start your OpenXR VR runtime (SteamVR / Meta / WMR / etc.), then launch from
Steam. No headset? It also runs in flatscreen.

### Controls
- **F7** — toggle the on-screen co-op lobby browser (Create / Refresh / Join / Leave).
- In VR: click **both thumbsticks together** to open the in-headset menu.
   -'A'/'X' on the controller holding the clipboard brings up the pencil colors.
   -'A'/X' while looking at the map opens a zoomed map screen for easier reading.
   -Left menu button is 'esc'.
   -'B' on right controller deletes map lines.
   -Left thumbstick to move
   -Right thumstick to rotate
   -'Grab' grabs on to clipboards and Iron Nest controls.
   -Trigger interacts with map, maps pieces, punchcards, record player discs and coffee.  It is also a back up in case you do not like grabbing and pulling levers.

## Known Issues
-VR crashing and low frame rate is usually a CPU issue. The mod should automatically downgrade settings to 'very low' on its own if performance is struggling, but if
   it fails, then launching in flatscreen to turn graphics settings down to 'very low' can help.
   * '-force-d3d11 -force-gfx-direct'  in the launch parameters is also mandatory.  VR crashes will happen without it.
-'Mission Start' is on the clipboard after selecting a mission.  Pick up your clipboard from your waist to start a mission.
-Testing has never actually completed a full mission in co-op.  I expect bugs.
-More than 2 players is supported, but it has not been tested at all.  I expect bugs.

## Uninstall
Delete `openxr_loader.dll` from the game root and the
`BepInEx\plugins\IronNestVR` folder.

## Credits / licenses
- Khronos **OpenXR loader** (`openxr_loader.dll`) — Apache-2.0.
- **Silk.NET** — MIT. Runs under **BepInEx** — LGPL-2.1.
